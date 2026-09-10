using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RTSSGameBar.Protocol;
using Windows.Storage;

namespace RTSSGameBar.Widget.Ipc
{
    internal sealed class PipeClient : IDisposable
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;

        private static readonly object DiagnosticsLock = new object();
        private const string DiagnosticsLogFileName = "widget-ipc.log";
        private const string DiagnosticsPreviousLogFileName = "widget-ipc.previous.log";
        private const long DiagnosticsRotateAfterBytes = 1L * 1024L * 1024L;

        public async Task<RtssResponse> SendAsync(RtssRequest request, int timeoutMs = 1500)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            var totalClock = Stopwatch.StartNew();
            var gateClock = Stopwatch.StartNew();
            LogIpc("BEGIN id=" + request.RequestId + " command=" + request.Command + " timeoutMs=" + timeoutMs + ".");

            await _gate.WaitAsync().ConfigureAwait(false);
            gateClock.Stop();
            if (gateClock.ElapsedMilliseconds >= 50)
                LogIpc("GATE id=" + request.RequestId + " command=" + request.Command + " waitMs=" + gateClock.ElapsedMilliseconds + ".");

            try
            {
                await EnsureConnectedAsync(timeoutMs, request).ConfigureAwait(false);

                try
                {
                    await _writer.WriteLineAsync(ProtocolJson.Serialize(request)).ConfigureAwait(false);
                    LogIpc("SENT id=" + request.RequestId + " command=" + request.Command + " elapsedMs=" + totalClock.ElapsedMilliseconds + ".");

                    var responseClock = Stopwatch.StartNew();
                    var readTask = _reader.ReadLineAsync();
                    var completed = await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (completed != readTask)
                    {
                        responseClock.Stop();
                        LogIpc(
                            "TIMEOUT id=" + request.RequestId +
                            " command=" + request.Command +
                            " timeoutMs=" + timeoutMs +
                            " responseWaitMs=" + responseClock.ElapsedMilliseconds +
                            " totalMs=" + totalClock.ElapsedMilliseconds +
                            " pipeConnected=" + IsPipeConnectedForDiagnostics() + ".");
                        throw new TimeoutException("Timed out waiting for the helper IPC response.");
                    }

                    var responseLine = await readTask.ConfigureAwait(false);
                    responseClock.Stop();
                    if (responseLine == null)
                        throw new IOException("Helper closed the IPC connection without a response.");

                    var response = ProtocolJson.Deserialize<RtssResponse>(responseLine);
                    if (response.ProtocolVersion != ProtocolConstants.Version)
                        throw new InvalidOperationException("Helper protocol version mismatch.");

                    LogIpc(
                        "RECV id=" + request.RequestId +
                        " command=" + request.Command +
                        " responseId=" + (response.RequestId ?? "<null>") +
                        " success=" + response.Success +
                        " errorCode=" + (response.ErrorCode ?? "<none>") +
                        " responseWaitMs=" + responseClock.ElapsedMilliseconds +
                        " totalMs=" + totalClock.ElapsedMilliseconds + ".");

                    if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal))
                    {
                        LogIpc(
                            "MISMATCH id=" + request.RequestId +
                            " command=" + request.Command +
                            " responseId=" + (response.RequestId ?? "<null>") + ".");
                    }

                    return response;
                }
                catch (Exception ex)
                {
                    LogIpc(
                        "ERROR id=" + request.RequestId +
                        " command=" + request.Command +
                        " totalMs=" + totalClock.ElapsedMilliseconds +
                        " hresult=0x" + ex.HResult.ToString("X8") +
                        " type=" + ex.GetType().FullName +
                        " message=" + OneLine(ex.Message) + ".");
                    ResetConnection();
                    throw;
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        public Task<RtssResponse> PingAsync(int timeoutMs = 500)
        {
            return SendAsync(new RtssRequest { Command = RtssCommand.Ping }, timeoutMs);
        }

        public void Disconnect()
        {
            ResetConnection();
        }

        private async Task EnsureConnectedAsync(int timeoutMs, RtssRequest request)
        {
            if (_pipe != null && _pipe.IsConnected)
                return;

            ResetConnection();
            var pipe = new NamedPipeClientStream(
                ".",
                ProtocolConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            var connectClock = Stopwatch.StartNew();
            LogIpc("CONNECT_BEGIN id=" + request.RequestId + " command=" + request.Command + " timeoutMs=" + timeoutMs + ".");

            try
            {
                await pipe.ConnectAsync(timeoutMs).ConfigureAwait(false);
                connectClock.Stop();
                _pipe = pipe;
                _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, true);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                LogIpc("CONNECT_OK id=" + request.RequestId + " command=" + request.Command + " elapsedMs=" + connectClock.ElapsedMilliseconds + ".");
            }
            catch (Exception ex)
            {
                connectClock.Stop();
                LogIpc(
                    "CONNECT_FAIL id=" + request.RequestId +
                    " command=" + request.Command +
                    " elapsedMs=" + connectClock.ElapsedMilliseconds +
                    " hresult=0x" + ex.HResult.ToString("X8") +
                    " type=" + ex.GetType().FullName +
                    " message=" + OneLine(ex.Message) + ".");
                pipe.Dispose();
                throw;
            }
        }

        private bool IsPipeConnectedForDiagnostics()
        {
            try { return _pipe != null && _pipe.IsConnected; }
            catch { return false; }
        }

        private void ResetConnection()
        {
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            _writer = null;
            _reader = null;
            _pipe = null;
        }

        private static void RotateDiagnosticsLogIfNeeded(string filePath, string previousFilePath)
        {
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists || info.Length < DiagnosticsRotateAfterBytes)
                    return;

                try { File.Delete(previousFilePath); }
                catch { }
                File.Move(filePath, previousFilePath);
            }
            catch
            {
                // Rotation is best-effort. Diagnostics must never affect IPC behavior.
            }
        }

        private static void LogIpc(string message)
        {
            try
            {
                lock (DiagnosticsLock)
                {
                    var folder = ApplicationData.Current.LocalFolder.Path;
                    var filePath = Path.Combine(folder, DiagnosticsLogFileName);
                    var previousFilePath = Path.Combine(folder, DiagnosticsPreviousLogFileName);
                    RotateDiagnosticsLogIfNeeded(filePath, previousFilePath);
                    File.AppendAllText(
                        filePath,
                        string.Format("{0:O} [IPC] {1}{2}", DateTimeOffset.Now, message, Environment.NewLine),
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never become another Widget failure path.
            }
        }

        private static string OneLine(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        public void Dispose()
        {
            ResetConnection();
            _gate.Dispose();
        }
    }
}
