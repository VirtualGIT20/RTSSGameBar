using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using RTSSGameBar.Helper.Integration;
using RTSSGameBar.Helper.Platform;
using RTSSGameBar.Helper.RtssPlugin;
using RTSSGameBar.Protocol;

namespace RTSSGameBar.Helper.Rtss
{
    internal sealed class RtssController
    {
        private const uint WmClose = 0x0010;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private readonly object _sync = new object();
        private readonly RtssPluginClient _plugin = new RtssPluginClient();
        private readonly IntegrationManager _integration = new IntegrationManager();
        private RtssInstallation _installation;
        private string _lastStatusLogKey;
        private string _lastActiveProfileHint = string.Empty;

        public RtssStatus GetStatus(string profile)
        {
            lock (_sync)
            {
                RefreshInstallation();
                var installed = _installation?.IsValid == true;
                var running = IsRunning();
                var pluginInstalled = _integration.IsPluginInstalled(_installation);
                var updateAvailable = pluginInstalled && _integration.IsUpdateAvailable(_installation);

                var status = new RtssStatus
                {
                    Installed = installed,
                    Running = running,
                    PluginInstalled = pluginInstalled,
                    PluginUpdateAvailable = updateAvailable,
                    BundledPluginVersion = ProtocolConstants.BundledPluginVersion
                };

                if (!installed)
                {
                    status.IntegrationState = RtssIntegrationState.RtssNotInstalled;
                    status.Detail = "RTSS installation not found.";
                    LogStatusTransition(status);
                    return status;
                }

                if (!pluginInstalled)
                {
                    status.IntegrationState = RtssIntegrationState.NotInstalled;
                    status.Detail = "RTSS is installed. Install the RTSS Game Bar integration to enable control.";
                    LogStatusTransition(status);
                    return status;
                }

                if (updateAvailable)
                {
                    status.IntegrationState = RtssIntegrationState.UpdateRequired;
                    status.Detail = "The installed RTSS Game Bar integration does not match this widget version. Update it before using RTSS controls.";
                    LogStatusTransition(status);
                    return status;
                }

                if (!running)
                {
                    status.IntegrationState = RtssIntegrationState.RtssStopped;
                    status.Detail = "RTSS and the integration plugin are installed. Start RTSS to connect.";
                    // A stopped RTSS has no hooked application, so a hint from an earlier RTSS
                    // session must not survive into the next one.
                    _lastActiveProfileHint = string.Empty;
                    LogStatusTransition(status);
                    return status;
                }

                try
                {
                    var pluginReadClock = Stopwatch.StartNew();
                    // The foreground executable is resolved only for this on-demand status read.
                    // There is no helper-side timer or background watcher.
                    var hint = ForegroundProcessResolver.TryGetLikelyGameExecutable();
                    var state = _plugin.ReadState(profile, hint);
                    pluginReadClock.Stop();
                    // 400 ms is a quarter of the 1500 ms read budget; below that a read is
                    // normal RTSS latency rather than something worth a warning.
                    if (pluginReadClock.ElapsedMilliseconds >= 400)
                        Log.Warn("Slow RTSS plugin state read: " + pluginReadClock.ElapsedMilliseconds + "ms.");
                    ApplyPluginState(status, state);
                    if (!string.IsNullOrEmpty(hint))
                    {
                        // A resolved executable maps to an empty hint when RTSS has no matching
                        // profile. That is meaningful: the running application has no RTSS profile.
                        _lastActiveProfileHint = state.ActiveProfileHint ?? string.Empty;
                    }
                    else
                    {
                        // The game window could not be resolved (for example a full-screen
                        // exclusive game). Keep the last known hint instead of reporting
                        // "no game profile" from a temporary detection gap.
                        status.ActiveProfileHint = _lastActiveProfileHint;
                    }
                    status.PluginConnected = true;
                    status.IntegrationState = RtssIntegrationState.Connected;
                    status.Detail = "Connected to RTSS through the client plugin. Runtime helper remains non-elevated.";
                }
                catch (RtssPluginProtocolException ex)
                {
                    status.PluginConnected = false;
                    status.IntegrationState = RtssIntegrationState.Incompatible;
                    status.Detail = ex.Message;
                }
                catch (RtssPluginUnavailableException)
                {
                    status.PluginConnected = false;
                    status.IntegrationState = RtssIntegrationState.Disabled;
                    status.Detail = "The plugin file is installed but is not active in RTSS. Enable RTSSGameBarPlugin.dll in RTSS Properties > Plugins, then retry.";
                }
                catch (Exception ex)
                {
                    status.PluginConnected = false;
                    status.IntegrationState = RtssIntegrationState.Error;
                    status.Detail = "RTSS integration error: " + ex.Message;
                }

                LogStatusTransition(status);
                return status;
            }
        }

        public bool StartRtss(out string error)
        {
            lock (_sync)
            {
                error = null;
                RefreshInstallation();
                if (_installation?.IsValid != true)
                {
                    error = "RTSS installation was not found.";
                    return false;
                }

                if (IsRunning())
                    return true;

                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = _installation.ExecutablePath,
                        WorkingDirectory = _installation.Directory,
                        UseShellExecute = true
                    });
                    Log.Info("Requested RTSS start: " + _installation.ExecutablePath);
                    // The new RTSS session has no hooked application yet, so drop any hint that
                    // belongs to the previous session even before the next status read.
                    _lastActiveProfileHint = string.Empty;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Log.Error("Failed to start RTSS: " + ex);
                    return false;
                }
            }
        }

        public bool StopRtss(out string error)
        {
            lock (_sync)
            {
                error = null;
                if (!IsRunning())
                    return true;

                Exception pluginCloseError = null;
                try
                {
                    _plugin.CloseRtss();
                    Log.Info("Requested RTSS close through the integration plugin.");
                }
                catch (Exception ex)
                {
                    // A clean install has no client plugin yet, and UpdateRequired can make the
                    // plugin protocol unavailable. Fall back to the same graceful WM_CLOSE path
                    // without requiring elevation or force-terminating RTSS.
                    pluginCloseError = ex;
                    Log.Warn("Plugin close was unavailable; trying RTSS window close fallback: " + ex.Message);
                }

                RequestRtssWindowClose();
                var until = DateTime.UtcNow.AddSeconds(6);
                while (DateTime.UtcNow < until)
                {
                    if (!IsRunning())
                    {
                        Log.Info("RTSS closed gracefully.");
                        return true;
                    }
                    Thread.Sleep(150);
                }

                error = pluginCloseError == null
                    ? "RTSS received the close request but is still running. It was not force-terminated."
                    : "RTSS could not be closed gracefully. Plugin close error: " + pluginCloseError.Message;
                Log.Warn(error);
                return false;
            }
        }

        public RtssStatus SetFrameLimit(int fps, string profile)
        {
            lock (_sync)
            {
                EnsureValueRange(fps, 0, 1000, nameof(fps));
                var state = _plugin.SetFrameLimit(fps, profile);
                Log.Info("Plugin SetFrameLimit profile=" + Describe(profile) + " requested=" + fps + " readBack=" + state.FrameLimit + ".");
                return BuildConnectedStatus(state);
            }
        }

        public RtssStatus SetLimiterType(RtssLimiterType limiterType, string profile)
        {
            lock (_sync)
            {
                if (limiterType < RtssLimiterType.Async || limiterType > RtssLimiterType.NvidiaReflex)
                    throw new ArgumentOutOfRangeException(nameof(limiterType));
                var state = _plugin.SetLimiterType(limiterType, profile);
                Log.Info("Plugin SetLimiterType profile=" + Describe(profile) + " requested=" + limiterType + " readBack=" + state.SyncLimiter + ".");
                return BuildConnectedStatus(state);
            }
        }

        public RtssStatus SetLimiterEnabled(bool enabled)
        {
            lock (_sync)
            {
                var state = _plugin.SetLimiterEnabled(enabled);
                Log.Info("Plugin SetLimiterEnabled requested=" + enabled + " readBack=" + state.LimiterEnabled + ".");
                return BuildConnectedStatus(state);
            }
        }

        public RtssStatus SetOverlayVisible(bool visible)
        {
            lock (_sync)
            {
                var state = _plugin.SetOverlayVisible(visible);
                Log.Info("Plugin SetOverlayVisible requested=" + visible + " readBack=" + state.OverlayVisible + ".");
                return BuildConnectedStatus(state);
            }
        }

        public RtssStatus SetOsdZoom(int zoom, string profile)
        {
            lock (_sync)
            {
                EnsureValueRange(zoom, 1, 8, nameof(zoom));
                var state = _plugin.SetOsdZoom(zoom, profile);
                Log.Info("Plugin SetOsdZoom profile=" + Describe(profile) + " requested=" + zoom + " readBack=" + state.OsdZoom + ".");
                return BuildConnectedStatus(state);
            }
        }

        public RtssStatus SetOsdPosition(RtssOsdPosition position, string profile)
        {
            lock (_sync)
            {
                if (position < RtssOsdPosition.TopLeft || position > RtssOsdPosition.MiddleRight)
                    throw new ArgumentOutOfRangeException(nameof(position));
                var state = _plugin.SetOsdPosition(position, profile);
                Log.Info("Plugin SetOsdPosition profile=" + Describe(profile) + " requested=" + position + " readBack=" +
                    (state.OsdPosition.HasValue ? state.OsdPosition.Value.ToString() : "Custom") + ".");
                return BuildConnectedStatus(state);
            }
        }

        public IntegrationOperationResult RunIntegrationOperation(IntegrationOperation operation)
        {
            lock (_sync)
            {
                var result = _integration.Run(operation);
                Log.Info("Integration operation " + operation + " success=" + result.Success + " message=" + result.Message);
                return result;
            }
        }

        private RtssStatus BuildConnectedStatus(RtssPluginState state)
        {
            RefreshInstallation();
            var status = new RtssStatus
            {
                Installed = _installation?.IsValid == true,
                Running = IsRunning(),
                PluginInstalled = _integration.IsPluginInstalled(_installation),
                PluginUpdateAvailable = false,
                PluginConnected = true,
                IntegrationState = RtssIntegrationState.Connected,
                BundledPluginVersion = ProtocolConstants.BundledPluginVersion,
                Detail = "Connected through RTSSGameBarPlugin.dll."
            };
            ApplyPluginState(status, state);
            // The plugin cannot re-resolve the foreground hint during a write, so a write
            // response keeps the hint from the last on-demand status read.
            status.ActiveProfileHint = _lastActiveProfileHint;
            return status;
        }

        private static void ApplyPluginState(RtssStatus status, RtssPluginState state)
        {
            status.FrameLimit = state.FrameLimit;
            status.OsdZoom = state.OsdZoom;
            status.OsdPosition = state.OsdPosition;
            if (state.SyncLimiter >= 0 && state.SyncLimiter <= 3)
                status.LimiterType = (RtssLimiterType)state.SyncLimiter;
            status.LimiterEnabled = state.LimiterEnabled;
            status.OverlayVisible = state.OverlayVisible;
            status.PluginVersion = state.PluginVersion;
            status.Profile = state.Profile;
            status.ActiveProfileHint = state.ActiveProfileHint;
            status.ProfileMissing = state.ProfileMissing;
            status.AvailableProfiles = state.AvailableProfiles?.ToArray() ?? new string[0];
        }

        private void RefreshInstallation()
        {
            _installation = RtssInstallationLocator.Locate();
        }

        private void LogStatusTransition(RtssStatus status)
        {
            var key = status.IntegrationState + "|" + status.Running + "|" + status.PluginInstalled + "|" + status.PluginConnected + "|" + status.PluginUpdateAvailable;
            if (string.Equals(key, _lastStatusLogKey, StringComparison.Ordinal))
                return;
            _lastStatusLogKey = key;
            Log.Info("RTSS status changed: installed=" + status.Installed + " running=" + status.Running + " integration=" + status.IntegrationState + " pluginInstalled=" + status.PluginInstalled + " pluginConnected=" + status.PluginConnected + " update=" + status.PluginUpdateAvailable + ".");
        }


        private static void RequestRtssWindowClose()
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("RTSS");
            }
            catch
            {
                return;
            }

            foreach (var process in processes)
            {
                try
                {
                    EnumWindows(CloseRtssWindowProc, new IntPtr(process.Id));
                }
                catch (Exception ex)
                {
                    Log.Warn("RTSS window close fallback failed for pid=" + process.Id + ": " + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static bool CloseRtssWindowProc(IntPtr hWnd, IntPtr lParam)
        {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            if (owner == unchecked((uint)lParam.ToInt64()))
                PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            return true;
        }

        private static bool IsRunning()
        {
            Process[] processes = null;
            try
            {
                processes = Process.GetProcessesByName("RTSS");
                return processes.Length > 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                if (processes != null)
                {
                    foreach (var process in processes)
                    {
                        try { process.Dispose(); }
                        catch { }
                    }
                }
            }
        }

        private static void EnsureValueRange(int value, int min, int max, string name)
        {
            if (value < min || value > max)
                throw new ArgumentOutOfRangeException(name, "Value must be between " + min + " and " + max + ".");
        }

        private static string Describe(string profile)
        {
            return string.IsNullOrEmpty(profile) ? "Global" : profile;
        }
    }
}
