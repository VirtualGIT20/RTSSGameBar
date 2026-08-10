using System;
using System.IO;
using System.Text;

namespace RTSSGameBar.Helper.Platform
{
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RTSSGameBar");
        private static readonly string FilePath = Path.Combine(DirectoryPath, "helper.log");
        private static readonly string PreviousFilePath = Path.Combine(DirectoryPath, "helper.previous.log");
        private const long RotateAfterBytes = 2L * 1024L * 1024L;
        private static bool _prepared;

        public static string FileName => FilePath;

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message) => Write("ERROR", message);

        private static void PrepareLogFile()
        {
            if (_prepared)
                return;
            _prepared = true;

            Directory.CreateDirectory(DirectoryPath);
            try
            {
                var info = new FileInfo(FilePath);
                if (!info.Exists || info.Length < RotateAfterBytes)
                    return;

                try { File.Delete(PreviousFilePath); }
                catch { }
                File.Move(FilePath, PreviousFilePath);
            }
            catch
            {
                // Rotation is best-effort; normal logging can continue in the current file.
            }
        }

        private static void Write(string level, string message)
        {
            try
            {
                lock (Sync)
                {
                    PrepareLogFile();
                    File.AppendAllText(
                        FilePath,
                        $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}",
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Logging must never take the helper down.
            }
        }
    }
}
