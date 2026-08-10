using System;
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

        public PipeServer(RtssController rtss)
        {
            _rtss = rtss ?? throw new ArgumentNullException(nameof(rtss));
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Log.Info("IPC server starting on pipe " + ProtocolConstants.PipeName + ". Protocol v" + ProtocolConstants.Version + "; plugin-only backend.");

            while (!cancellationToken.IsCancellationRequested)
            {
                using (var pipe = CreatePipe())
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                        Log.Info("IPC client connected (persistent session).");
                        await HandleConnectionAsync(pipe, cancellationToken).ConfigureAwait(false);
                        Log.Info("IPC client disconnected.");
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.Error("IPC connection failed: " + ex);
                    }
                }
            }
        }

        private static NamedPipeServerStream CreatePipe()
        {
            var security = PipeSecurityFactory.Create();
            return new NamedPipeServerStream(
                ProtocolConstants.PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                security);
        }

        private async Task HandleConnectionAsync(Stream stream, CancellationToken cancellationToken)
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
                    try
                    {
                        request = ProtocolJson.Deserialize<RtssRequest>(line);
                        response = Dispatch(request);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("Bad IPC request: " + ex.Message);
                        response = RtssResponse.Fail(request, "bad_request", ex.Message);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    await writer.WriteLineAsync(ProtocolJson.Serialize(response)).ConfigureAwait(false);
                }
            }
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
