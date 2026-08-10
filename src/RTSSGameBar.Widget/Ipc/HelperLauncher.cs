using System;
using System.IO;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;

namespace RTSSGameBar.Widget.Ipc
{
    internal sealed class HelperLaunchResult
    {
        public bool Success { get; set; }
        public string Stage { get; set; }
        public string Message { get; set; }

        public static HelperLaunchResult Ok(string stage, string message = null)
        {
            return new HelperLaunchResult { Success = true, Stage = stage, Message = message };
        }

        public static HelperLaunchResult Fail(string stage, string message)
        {
            return new HelperLaunchResult { Success = false, Stage = stage, Message = message };
        }

        public override string ToString()
        {
            if (string.IsNullOrWhiteSpace(Message))
                return Stage ?? string.Empty;
            return (Stage ?? "Helper") + ": " + Message;
        }
    }

    internal sealed class HelperLauncher
    {
        private readonly PipeClient _client;

        public HelperLauncher(PipeClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public async Task<HelperLaunchResult> EnsureRunningAsync()
        {
            // Warm path: do not spend most of a second proving that a helper which is
            // normally absent on cold start is absent. A live local pipe answers quickly.
            var initialPing = await TryPingAsync(120);
            if (initialPing.Success)
                return HelperLaunchResult.Ok("Connected", "warm");

            if (!ApiInformation.IsApiContractPresent("Windows.ApplicationModel.FullTrustAppContract", 1, 0))
            {
                return HelperLaunchResult.Fail(
                    "FullTrust API",
                    "Windows.ApplicationModel.FullTrustAppContract v1 is unavailable on this system.");
            }

            try
            {
                await Package.Current.InstalledLocation.GetFileAsync(@"Helper\RTSSGameBar.Helper.exe");
            }
            catch (Exception ex)
            {
                return HelperLaunchResult.Fail("Package", FormatException(ex));
            }

            try
            {
                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
            }
            catch (Exception ex)
            {
                return HelperLaunchResult.Fail("LaunchFullTrustProcess", FormatException(ex));
            }

            // Poll with short attempts so a normal startup is observed quickly while still
            // allowing a slow VM up to roughly eight seconds to finish launching .NET Framework.
            Exception lastPingError = null;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(200);
                var ping = await TryPingAsync(200);
                lastPingError = ping.Error;
                if (ping.Success)
                    return HelperLaunchResult.Ok("Connected", "cold");
            }

            var message = lastPingError == null
                ? "The full-trust activation returned, but the helper did not answer the named pipe within 8 seconds."
                : "The helper did not answer the named pipe within 8 seconds. Last IPC error: " + FormatException(lastPingError);

            return HelperLaunchResult.Fail("IPC", message);
        }

        private async Task<PingResult> TryPingAsync(int timeoutMs)
        {
            try
            {
                var response = await _client.PingAsync(timeoutMs);
                if (response?.Success == true)
                    return new PingResult { Success = true };

                return new PingResult
                {
                    Success = false,
                    Error = new IOException(response?.ErrorMessage ?? response?.ErrorCode ?? "Helper ping returned an unsuccessful response.")
                };
            }
            catch (Exception ex)
            {
                return new PingResult { Success = false, Error = ex };
            }
        }

        private static string FormatException(Exception ex)
        {
            if (ex == null)
                return "Unknown error.";

            return ex.GetType().Name + " (0x" + ex.HResult.ToString("X8") + "): " + ex.Message;
        }

        private sealed class PingResult
        {
            public bool Success { get; set; }
            public Exception Error { get; set; }
        }
    }
}
