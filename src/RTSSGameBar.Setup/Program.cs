using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace RTSSGameBar.Setup
{
    internal static class Program
    {
        private const int ExitSuccess = 0;
        private const int ExitRtssStillRunning = 20;
        private const int ExitRtssNotFound = 21;
        private const int ExitBundledPluginMissing = 22;
        private const int ExitFileOperationFailed = 23;
        private const uint WmClose = 0x0010;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [STAThread]
        private static int Main(string[] args)
        {
            int exitCode;
            try
            {
                exitCode = Run(args);
            }
            catch (Exception ex)
            {
                Log("Unhandled setup error: " + ex);
                exitCode = ExitFileOperationFailed;
            }

            // WER from 19.4/19.5 consistently showed apphelp.dll failing after the last successful
            // setup log entry, during normal process teardown. Returning from Main calls ExitProcess,
            // which runs DLL_PROCESS_DETACH handlers. This one-shot process has already closed all of
            // its own handles, so terminate directly to bypass the faulty detach path.
            Log("Setup work finished. terminating process directly with exit code " + exitCode + ".");
            if (!TerminateProcess(GetCurrentProcess(), unchecked((uint)exitCode)))
                Log("TerminateProcess failed with Win32 error " + Marshal.GetLastWin32Error() + "; falling back to normal return.");
            return exitCode;
        }

        private static int Run(string[] args)
        {
            var action = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : string.Empty;
            Log("Setup started. action=" + action + " elevated=" + IsElevated());

            if (action != "install" && action != "update" && action != "remove")
            {
                Log("Unsupported action: " + action);
                return ExitFileOperationFailed;
            }

            var rtssDirectory = LocateRtss();
            if (string.IsNullOrWhiteSpace(rtssDirectory) || !File.Exists(Path.Combine(rtssDirectory, "RTSS.exe")))
            {
                Log("RTSS installation not found.");
                return ExitRtssNotFound;
            }

            var target = Path.Combine(rtssDirectory, "Plugins", "Client", "RTSSGameBarPlugin.dll");
            var source = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RTSSGameBarPlugin.dll");
            if (action != "remove" && !File.Exists(source))
            {
                Log("Bundled plugin missing: " + source);
                return ExitBundledPluginMissing;
            }

            var deadline = DateTime.UtcNow.AddSeconds(8);
            Exception lastFileError = null;
            var attempt = 0;

            while (DateTime.UtcNow < deadline)
            {
                attempt++;

                // The normal helper closes RTSS before UAC, but MSI Afterburner can respawn RTSS
                // while the consent UI is on screen. Re-check in the elevated phase, close it
                // gracefully first, and force only the raced/respawned instance if necessary.
                EnsureRtssStoppedForMaintenance();

                try
                {
                    var targetDirectory = Path.GetDirectoryName(target);
                    if (string.IsNullOrWhiteSpace(targetDirectory))
                        return ExitFileOperationFailed;

                    Directory.CreateDirectory(targetDirectory);
                    if (action == "remove")
                    {
                        if (File.Exists(target))
                            File.Delete(target);
                        if (File.Exists(target))
                            throw new IOException("The integration plugin still exists after delete.");
                        Log("Removed integration plugin: " + target);
                    }
                    else
                    {
                        File.Copy(source, target, true);
                        Log("Copied integration plugin: " + source + " -> " + target);
                    }

                    Log("Setup action completed successfully. RTSS restart is delegated to the normal helper.");
                    return ExitSuccess;
                }
                catch (IOException ex)
                {
                    lastFileError = ex;
                    Log("File operation attempt " + attempt + " raced RTSS/another process: " + ex.Message);
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastFileError = ex;
                    Log("File operation attempt " + attempt + " was denied: " + ex.Message);
                }

                Thread.Sleep(90);
            }

            Log("File operation failed after retries: " + (lastFileError == null ? "unknown error" : lastFileError.ToString()));
            return ExitFileOperationFailed;
        }

        private static void EnsureRtssStoppedForMaintenance()
        {
            var processes = GetRtssProcesses();
            if (processes.Length == 0)
                return;

            try
            {
                Log("RTSS is running inside the elevated maintenance phase; requesting close for " + processes.Length + " process(es).");
                foreach (var process in processes)
                    RequestRtssWindowClose(process.Id);
            }
            finally
            {
                DisposeProcesses(processes);
            }

            var gracefulDeadline = DateTime.UtcNow.AddMilliseconds(650);
            while (DateTime.UtcNow < gracefulDeadline)
            {
                if (!IsRtssRunning())
                    return;
                Thread.Sleep(50);
            }

            processes = GetRtssProcesses();
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        Log("RTSS remained/rerespawned during maintenance; terminating pid=" + process.Id + ".");
                        process.Kill();
                        process.WaitForExit(900);
                    }
                    catch (Exception ex)
                    {
                        Log("Could not terminate RTSS pid=" + process.Id + ": " + ex.Message);
                    }
                }
            }
            finally
            {
                DisposeProcesses(processes);
            }

            Thread.Sleep(35);
        }

        private static Process[] GetRtssProcesses()
        {
            try { return Process.GetProcessesByName("RTSS"); }
            catch (Exception ex)
            {
                Log("Could not enumerate RTSS processes: " + ex.Message);
                return new Process[0];
            }
        }

        private static bool IsRtssRunning()
        {
            var processes = GetRtssProcesses();
            try { return processes.Length > 0; }
            finally { DisposeProcesses(processes); }
        }

        private static void DisposeProcesses(Process[] processes)
        {
            foreach (var process in processes)
            {
                try { process.Dispose(); }
                catch { }
            }
        }

        private static void RequestRtssWindowClose(int processId)
        {
            try { EnumWindows(CloseRtssWindowProc, new IntPtr(processId)); }
            catch (Exception ex) { Log("RTSS WM_CLOSE enumeration failed for pid=" + processId + ": " + ex.Message); }
        }

        private static bool CloseRtssWindowProc(IntPtr hWnd, IntPtr lParam)
        {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            if (owner == unchecked((uint)lParam.ToInt64()))
                PostMessage(hWnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            return true;
        }

        private static string LocateRtss()
        {
            return ReadInstallDir(RegistryView.Registry32)
                ?? ReadInstallDir(RegistryView.Registry64)
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server");
        }

        private static string ReadInstallDir(RegistryView view)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Unwinder\RTSS", false))
                    return (key?.GetValue("InstallDir") as string)?.TrimEnd('\\');
            }
            catch { return null; }
        }

        private static bool IsElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                {
                    var principal = new System.Security.Principal.WindowsPrincipal(identity);
                    return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        private static void Log(string message)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RTSSGameBar");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "setup.log"), DateTimeOffset.Now.ToString("O") + " [INFO] " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
