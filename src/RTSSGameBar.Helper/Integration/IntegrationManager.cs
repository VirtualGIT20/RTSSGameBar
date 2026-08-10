using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using RTSSGameBar.Helper.Platform;
using RTSSGameBar.Helper.Rtss;

namespace RTSSGameBar.Helper.Integration
{
    internal enum IntegrationOperation
    {
        Install,
        Update,
        Remove
    }

    internal sealed class IntegrationOperationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    internal sealed class IntegrationManager
    {
        private const int SetupTimeoutMs = 90000;
        private const uint SeeMaskNoCloseProcess = 0x00000040;
        private const uint SeeMaskNoAsync = 0x00000100;
        private const uint WaitObject0 = 0x00000000;
        private const uint WaitTimeout = 0x00000102;
        private const int SwShowNormal = 1;
        private const int ErrorCancelled = 1223;

        private string _bundledHashPath;
        private long _bundledHashLength = -1;
        private DateTime _bundledHashWriteUtc;
        private string _bundledHash;
        private string _installedHashPath;
        private long _installedHashLength = -1;
        private DateTime _installedHashWriteUtc;
        private string _installedHash;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellExecuteInfo
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpVerb;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpFile;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpDirectory;
            public int nShow;
            public IntPtr hInstApp;
            public IntPtr lpIDList;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpClass;
            public IntPtr hkeyClass;
            public uint dwHotKey;
            public IntPtr hIconOrMonitor;
            public IntPtr hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        public string BundledPluginPath
        {
            get
            {
                var helperDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var packageRoot = Directory.GetParent(helperDirectory)?.FullName;
                return packageRoot == null ? null : Path.Combine(packageRoot, "Integration", "RTSSGameBarPlugin.dll");
            }
        }

        public string SetupPath
        {
            get
            {
                var helperDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var packageRoot = Directory.GetParent(helperDirectory)?.FullName;
                return packageRoot == null ? null : Path.Combine(packageRoot, "Integration", "RTSSGameBar.Setup.exe");
            }
        }

        public bool IsPluginInstalled(RtssInstallation installation)
        {
            return installation?.IsValid == true && File.Exists(GetInstalledPluginPath(installation));
        }

        public bool IsUpdateAvailable(RtssInstallation installation)
        {
            if (!IsPluginInstalled(installation))
                return false;

            var bundled = BundledPluginPath;
            if (string.IsNullOrWhiteSpace(bundled) || !File.Exists(bundled))
                return false;

            try
            {
                var installed = GetInstalledPluginPath(installation);
                return !string.Equals(
                    HashFileCached(bundled, true),
                    HashFileCached(installed, false),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not compare RTSS integration hashes: " + ex.Message);
                return false;
            }
        }

        public IntegrationOperationResult Run(IntegrationOperation operation)
        {
            var setup = SetupPath;
            if (string.IsNullOrWhiteSpace(setup) || !File.Exists(setup))
                return Fail("Integration setup executable is missing from the installed package.");

            if (operation != IntegrationOperation.Remove)
            {
                var plugin = BundledPluginPath;
                if (string.IsNullOrWhiteSpace(plugin) || !File.Exists(plugin))
                    return Fail("Bundled RTSSGameBarPlugin.dll is missing from the installed package.");
            }

            var action = operation == IntegrationOperation.Install ? "install"
                : operation == IntegrationOperation.Update ? "update"
                : "remove";

            IntPtr processHandle = IntPtr.Zero;
            try
            {
                // The widget intentionally remains visible while this request is running. Capture
                // Game Bar's current foreground window immediately before ShellExecuteEx and pass
                // it as the owner of all Shell/UAC UI instead of minimizing Game Bar and racing the
                // foreground transition.
                var owner = GetForegroundWindow();
                Log.Info("Launching elevated RTSS integration setup action=" + action + " ownerHwnd=0x" + owner.ToInt64().ToString("X") + ".");

                var info = new ShellExecuteInfo
                {
                    cbSize = Marshal.SizeOf(typeof(ShellExecuteInfo)),
                    fMask = SeeMaskNoCloseProcess | SeeMaskNoAsync,
                    hwnd = owner,
                    lpVerb = "runas",
                    lpFile = setup,
                    lpParameters = action,
                    lpDirectory = Path.GetDirectoryName(setup),
                    nShow = SwShowNormal
                };

                if (!ShellExecuteEx(ref info))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == ErrorCancelled)
                        return Fail("The UAC prompt was cancelled.");
                    throw new Win32Exception(error, "ShellExecuteEx failed to start the elevated integration setup.");
                }

                processHandle = info.hProcess;
                if (processHandle == IntPtr.Zero)
                    return Fail("Windows started the integration action but did not return a process handle.");

                var wait = WaitForSingleObject(processHandle, SetupTimeoutMs);
                if (wait == WaitTimeout)
                    return Fail("Integration setup timed out. No process was terminated; check RTSS and try again.");
                if (wait != WaitObject0)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Waiting for the integration setup failed.");

                uint rawExitCode;
                if (!GetExitCodeProcess(processHandle, out rawExitCode))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the integration setup exit code.");

                var exitCode = unchecked((int)rawExitCode);
                Log.Info("Elevated integration setup exited with code " + exitCode + ".");
                switch (exitCode)
                {
                    case 0:
                        return new IntegrationOperationResult { Success = true, Message = "Integration " + action + " completed. RTSS restart is handled by the widget/helper." };
                    case 20:
                        return Fail("RTSS is still running and could not be closed gracefully. Exit RTSS from the tray and try again.");
                    case 21:
                        return Fail("RTSS installation was not found by the elevated setup.");
                    case 22:
                        return Fail("The bundled RTSS plugin file was not found by the elevated setup.");
                    case 23:
                        return Fail("The integration file operation failed.");
                    default:
                    {
                        var fileStateChanged = ExpectedFileStateIsSatisfied(operation);
                        if (fileStateChanged)
                        {
                            Log.Warn("Integration setup terminated abnormally with code " + exitCode
                                + ", but the requested plugin file state was verified. Treating the operation as completed and restoring RTSS state.");
                            return new IntegrationOperationResult
                            {
                                Success = true,
                                Message = "Integration file operation completed and was verified despite an abnormal setup teardown."
                            };
                        }

                        Log.Error("Integration setup terminated abnormally with code " + exitCode + ". Requested file state satisfied=false.");
                        return Fail("Integration setup terminated abnormally with exit code " + exitCode + ".");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Integration setup launch failed: " + ex);
                return Fail(ex.Message);
            }
            finally
            {
                _installedHashPath = null;
                _installedHash = null;
                _installedHashLength = -1;
                _installedHashWriteUtc = default(DateTime);
                if (processHandle != IntPtr.Zero)
                    CloseHandle(processHandle);
            }
        }

        private bool ExpectedFileStateIsSatisfied(IntegrationOperation operation)
        {
            var installation = RtssInstallationLocator.Locate();
            if (installation?.IsValid != true)
                return false;

            var installed = GetInstalledPluginPath(installation);
            if (operation == IntegrationOperation.Remove)
                return !File.Exists(installed);

            var bundled = BundledPluginPath;
            if (string.IsNullOrWhiteSpace(bundled) || !File.Exists(bundled) || !File.Exists(installed))
                return false;

            try
            {
                return string.Equals(HashFile(bundled), HashFile(installed), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static string GetInstalledPluginPath(RtssInstallation installation)
        {
            return Path.Combine(installation.Directory, "Plugins", "Client", "RTSSGameBarPlugin.dll");
        }

        private string HashFileCached(string path, bool bundled)
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new FileNotFoundException("Plugin file was not found.", path);

            if (bundled)
            {
                if (string.Equals(_bundledHashPath, path, StringComparison.OrdinalIgnoreCase)
                    && _bundledHashLength == info.Length
                    && _bundledHashWriteUtc == info.LastWriteTimeUtc
                    && !string.IsNullOrEmpty(_bundledHash))
                    return _bundledHash;

                _bundledHashPath = path;
                _bundledHashLength = info.Length;
                _bundledHashWriteUtc = info.LastWriteTimeUtc;
                _bundledHash = HashFile(path);
                return _bundledHash;
            }

            if (string.Equals(_installedHashPath, path, StringComparison.OrdinalIgnoreCase)
                && _installedHashLength == info.Length
                && _installedHashWriteUtc == info.LastWriteTimeUtc
                && !string.IsNullOrEmpty(_installedHash))
                return _installedHash;

            _installedHashPath = path;
            _installedHashLength = info.Length;
            _installedHashWriteUtc = info.LastWriteTimeUtc;
            _installedHash = HashFile(path);
            return _installedHash;
        }

        private static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }

        private static IntegrationOperationResult Fail(string message)
        {
            return new IntegrationOperationResult { Success = false, Message = message };
        }
    }
}
