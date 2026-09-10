using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RTSSGameBar.Helper.Integration;
using RTSSGameBar.Helper.Platform;
using RTSSGameBar.Helper.Rtss;
using RTSSGameBar.Protocol;

namespace RTSSGameBar.Helper.Ipc
{
    internal sealed class PipeServer
    {
        private readonly RtssController _rtss;
        private static readonly object DiagnosticsLock = new object();
        private static readonly string DiagnosticsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RTSSGameBar");
        private static readonly string DiagnosticsFilePath = Path.Combine(DiagnosticsDirectory, "helper-ipc.log");
        private static readonly string DiagnosticsPreviousFilePath = Path.Combine(DiagnosticsDirectory, "helper-ipc.previous.log");
        private const long DiagnosticsRotateAfterBytes = 1L * 1024L * 1024L;
        private long _nextSessionId;
        private int _activeSessions;

        public PipeServer(RtssController rtss)
        {
            _rtss = rtss ?? throw new ArgumentNullException(nameof(rtss));
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Log.Info("IPC server starting on pipe " + ProtocolConstants.PipeName + ". Protocol v" + ProtocolConstants.Version + "; plugin-only backend; multi-client persistent sessions.");

            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreatePipe();
                    await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                    var sessionId = Interlocked.Increment(ref _nextSessionId);
                    var activeSessions = Interlocked.Increment(ref _activeSessions);
                    Log.Info("IPC client connected (persistent session). session=" + sessionId + " active=" + activeSessions + ".");
                    LogIpc("CONNECTED session=" + sessionId + " active=" + activeSessions + ".");

                    // Game Bar can create a replacement widget before an older widget process
                    // releases its persistent pipe. Hand the accepted connection to its own
                    // session and immediately create another listener for the replacement.
                    _ = HandleAcceptedConnectionAsync(pipe, sessionId, cancellationToken);
                    pipe = null;
                }
                catch (OperationCanceledException)
                {
                    try { pipe?.Dispose(); }
                    catch { }
                    return;
                }
                catch (Exception ex)
                {
                    try { pipe?.Dispose(); }
                    catch { }
                    Log.Error("IPC listener failed: " + ex);
                }
            }
        }

        private static NamedPipeServerStream CreatePipe()
        {
            var security = PipeSecurityFactory.Create();
            return new NamedPipeServerStream(
                ProtocolConstants.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                security);
        }

        private async Task HandleAcceptedConnectionAsync(
            NamedPipeServerStream pipe,
            long sessionId,
            CancellationToken cancellationToken)
        {
            // Force the accepted session off the listener continuation even if the client has
            // already written its first request and ReadLineAsync could complete synchronously.
            await Task.Yield();

            try
            {
                using (pipe)
                {
                    await HandleConnectionAsync(pipe, cancellationToken, sessionId).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Log.Warn("IPC client session canceled unexpectedly. session=" + sessionId + ".");
                    LogIpc("SESSION_CANCEL session=" + sessionId + ".");
                }
            }
            catch (Exception ex)
            {
                Log.Error("IPC client session failed. session=" + sessionId + ": " + ex);
                LogIpc(
                    "SESSION_FAIL session=" + sessionId +
                    " hresult=0x" + ex.HResult.ToString("X8") +
                    " type=" + ex.GetType().FullName +
                    " message=" + OneLine(ex.Message) + ".");
            }
            finally
            {
                var activeSessions = Interlocked.Decrement(ref _activeSessions);
                Log.Info("IPC client disconnected. session=" + sessionId + " active=" + activeSessions + ".");
                LogIpc("DISCONNECTED session=" + sessionId + " active=" + activeSessions + ".");
            }
        }

        private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken, long sessionId)
        {
            using (var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null)
                        return;

                    RtssRequest request = null;
                    RtssResponse response;
                    var requestClock = Stopwatch.StartNew();
                    try
                    {
                        request = ProtocolJson.Deserialize<RtssRequest>(line);
                        LogIpc(
                            "RECEIVED session=" + sessionId +
                            " id=" + (request.RequestId ?? "<null>") +
                            " command=" + request.Command + ".");
                        response = Dispatch(request);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Bad IPC request: " + ex.Message);
                        response = RtssResponse.Fail(request, "bad_request", ex.Message);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await writer.WriteLineAsync(ProtocolJson.Serialize(response)).ConfigureAwait(false);
                        requestClock.Stop();
                        LogIpc(
                            "SENT session=" + sessionId +
                            " id=" + (request?.RequestId ?? response?.RequestId ?? "<null>") +
                            " command=" + (request == null ? "<unknown>" : request.Command.ToString()) +
                            " success=" + (response != null && response.Success) +
                            " errorCode=" + (response?.ErrorCode ?? "<none>") +
                            " elapsedMs=" + requestClock.ElapsedMilliseconds + ".");
                    }
                    catch (Exception ex)
                    {
                        requestClock.Stop();
                        var writeFailure =
                            "IPC response write failed session=" + sessionId +
                            " id=" + (request?.RequestId ?? response?.RequestId ?? "<null>") +
                            " command=" + (request == null ? "<unknown>" : request.Command.ToString()) +
                            " elapsedMs=" + requestClock.ElapsedMilliseconds +
                            " hresult=0x" + ex.HResult.ToString("X8") +
                            " type=" + ex.GetType().FullName +
                            " message=" + OneLine(ex.Message);
                        LogIpc("WRITE_FAIL " + writeFailure);
                        Log.Error(writeFailure);
                        throw;
                    }
                }
            }
        }


        private static void LogIpc(string message)
        {
            try
            {
                lock (DiagnosticsLock)
                {
                    Directory.CreateDirectory(DiagnosticsDirectory);
                    RotateDiagnosticsLogIfNeeded();
                    File.AppendAllText(
                        DiagnosticsFilePath,
                        string.Format("{0:O} [IPC] {1}{2}", DateTimeOffset.Now, message, Environment.NewLine),
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never affect helper IPC behavior.
            }
        }

        private static void RotateDiagnosticsLogIfNeeded()
        {
            try
            {
                var info = new FileInfo(DiagnosticsFilePath);
                if (!info.Exists || info.Length < DiagnosticsRotateAfterBytes)
                    return;

                try { File.Delete(DiagnosticsPreviousFilePath); }
                catch { }
                File.Move(DiagnosticsFilePath, DiagnosticsPreviousFilePath);
            }
            catch
            {
                // Rotation is best-effort.
            }
        }

        private static string OneLine(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            return value.Replace("\r", " ").Replace("\n", " ");
        }

        private RtssResponse Dispatch(RtssRequest request)
        {
            if (request == null)
                return RtssResponse.Fail(null, "bad_request", "Request is null.");
            if (request.ProtocolVersion != ProtocolConstants.Version)
                return RtssResponse.Fail(request, "protocol_mismatch", "Unsupported helper protocol version.");
            if (!string.IsNullOrEmpty(request.Profile))
                return RtssResponse.Fail(request, "profile_not_supported", "RTSS Game Bar controls the RTSS Global profile only.");

            try
            {
                switch (request.Command)
                {
                    case RtssCommand.Ping:
                        return RtssResponse.Ok(request);

                    case RtssCommand.GetStatus:
                        return WithStatus(request, _rtss.GetStatus());

                    case RtssCommand.StartRtss:
                    {
                        string error;
                        if (!_rtss.StartRtss(out error))
                            return RtssResponse.Fail(request, "start_failed", error ?? "RTSS failed to start.");
                        return WithStatus(request, _rtss.GetStatus());
                    }

                    case RtssCommand.StopRtss:
                    {
                        string error;
                        if (!_rtss.StopRtss(out error))
                            return RtssResponse.Fail(request, "stop_failed", error ?? "RTSS failed to close gracefully.");
                        return WithStatus(request, _rtss.GetStatus());
                    }

                    case RtssCommand.SetFrameLimit:
                        if (!request.IntValue.HasValue)
                            return RtssResponse.Fail(request, "missing_value", "SetFrameLimit requires IntValue.");
                        return WithStatus(request, _rtss.SetFrameLimit(request.IntValue.Value));

                    case RtssCommand.SetLimiterType:
                        if (!request.IntValue.HasValue)
                            return RtssResponse.Fail(request, "missing_value", "SetLimiterType requires IntValue.");
                        return WithStatus(request, _rtss.SetLimiterType((RtssLimiterType)request.IntValue.Value));

                    case RtssCommand.SetLimiterEnabled:
                        if (!request.BoolValue.HasValue)
                            return RtssResponse.Fail(request, "missing_value", "SetLimiterEnabled requires BoolValue.");
                        return WithStatus(request, _rtss.SetLimiterEnabled(request.BoolValue.Value));

                    case RtssCommand.SetOverlayVisible:
                        if (!request.BoolValue.HasValue)
                            return RtssResponse.Fail(request, "missing_value", "SetOverlayVisible requires BoolValue.");
                        return WithStatus(request, _rtss.SetOverlayVisible(request.BoolValue.Value));

                    case RtssCommand.SetOsdZoom:
                        if (!request.IntValue.HasValue)
                            return RtssResponse.Fail(request, "missing_value", "SetOsdZoom requires IntValue.");
                        return WithStatus(request, _rtss.SetOsdZoom(request.IntValue.Value));

                    case RtssCommand.SetOsdPosition:
                        if (!request.IntValue.HasValue)
                            return RtssResponse.Fail(request, "missing_value", "SetOsdPosition requires IntValue.");
                        return WithStatus(request, _rtss.SetOsdPosition((RtssOsdPosition)request.IntValue.Value));

                    case RtssCommand.InstallIntegration:
                        return IntegrationResult(request, _rtss.RunIntegrationOperation(IntegrationOperation.Install));

                    case RtssCommand.UpdateIntegration:
                        return IntegrationResult(request, _rtss.RunIntegrationOperation(IntegrationOperation.Update));

                    case RtssCommand.RemoveIntegration:
                        return IntegrationResult(request, _rtss.RunIntegrationOperation(IntegrationOperation.Remove));

                    default:
                        return RtssResponse.Fail(request, "unsupported_command", request.Command.ToString());
                }
            }
            catch (Exception ex)
            {
                Log.Error("Command " + request.Command + " failed: " + ex);
                return RtssResponse.Fail(request, "rtss_plugin_error", ex.Message);
            }
        }

        private RtssResponse IntegrationResult(RtssRequest request, IntegrationOperationResult result)
        {
            if (!result.Success)
                return RtssResponse.Fail(request, "integration_setup_failed", result.Message);

            return WithStatus(request, _rtss.GetStatus());
        }

        private static RtssResponse WithStatus(RtssRequest request, RtssStatus status)
        {
            var response = RtssResponse.Ok(request);
            response.Status = status;
            return response;
        }
    }
}
