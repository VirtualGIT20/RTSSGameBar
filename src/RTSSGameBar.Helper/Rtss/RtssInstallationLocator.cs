using System;
using System.IO;
using Microsoft.Win32;

namespace RTSSGameBar.Helper.Rtss
{
    internal sealed class RtssInstallation
    {
        public string Directory { get; set; }
        public string ExecutablePath => Path.Combine(Directory ?? string.Empty, "RTSS.exe");
        public bool IsValid => !string.IsNullOrWhiteSpace(Directory)
                               && File.Exists(ExecutablePath);
    }

    internal static class RtssInstallationLocator
    {
        public static RtssInstallation Locate()
        {
            var registryPath = ReadInstallDir(RegistryView.Registry32)
                               ?? ReadInstallDir(RegistryView.Registry64);

            if (!string.IsNullOrWhiteSpace(registryPath))
            {
                var fromRegistry = new RtssInstallation { Directory = registryPath.TrimEnd('\\') };
                if (fromRegistry.IsValid)
                    return fromRegistry;
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(programFilesX86))
            {
                var conventional = new RtssInstallation
                {
                    Directory = Path.Combine(programFilesX86, "RivaTuner Statistics Server")
                };
                if (conventional.IsValid)
                    return conventional;
            }

            return new RtssInstallation { Directory = registryPath };
        }

        private static string ReadInstallDir(RegistryView view)
        {
            try
            {
                using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                using (var key = baseKey.OpenSubKey(@"SOFTWARE\Unwinder\RTSS", false))
                {
                    return key?.GetValue("InstallDir") as string;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
