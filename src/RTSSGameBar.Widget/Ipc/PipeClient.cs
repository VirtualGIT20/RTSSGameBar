using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RTSSGameBar.Protocol;

namespace RTSSGameBar.Widget.Ipc
{
    internal sealed class PipeClient : IDisposable
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private NamedPipeClientStream _pipe;
        private StreamReader _reader;
        private StreamWriter _writer;

        public async Task<RtssResponse> SendAsync(RtssRequest request, int timeoutMs = 1500)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureConnectedAsync(timeoutMs).ConfigureAwait(false);

                try
                {
                    await _writer.WriteLineAsync(ProtocolJson.Serialize(request)).ConfigureAwait(false);
                    var readTask = _reader.ReadLineAsync();
                    var completed = await Task.WhenAny(readTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                    if (completed != readTask)
                        throw new TimeoutException("Timed out waiting for the helper IPC response.");

                    var responseLine = await readTask.ConfigureAwait(false);
                    if (responseLine == null)
                        throw new IOException("Helper closed the IPC connection without a response.");

                    var response = ProtocolJson.Deserialize<RtssResponse>(responseLine);
                    if (response.ProtocolVersion != ProtocolConstants.Version)
                        throw new InvalidOperationException("Helper protocol version mismatch.");
                    return response;
                }
                catch
                {
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

        private async Task EnsureConnectedAsync(int timeoutMs)
        {
            if (_pipe != null && _pipe.IsConnected)
                return;

            ResetConnection();
            var pipe = new NamedPipeClientStream(
                ".",
                ProtocolConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(timeoutMs).ConfigureAwait(false);
                _pipe = pipe;
                _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 4096, true);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            }
            catch
            {
                pipe.Dispose();
                throw;
            }
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

        public void Dispose()
        {
            ResetConnection();
            _gate.Dispose();
        }
    }
}
