using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RTSSGameBar.Helper.Rtss
{
    /// <summary>
    /// Best-effort resolution of the executable name that most likely owns the game window.
    /// This is intentionally called only from an on-demand status read (widget refresh), never
    /// from a background thread or timer owned by the helper.
    ///
    /// RTSS itself matches profiles while it hooks the 3D application's presentation calls, so
    /// this helper cannot observe the exact same event. The result is therefore only a hint:
    /// the widget shows which profile *probably* matches the running game and never rewrites a
    /// profile based on it.
    /// </summary>
    internal static class ForegroundProcessResolver
    {
        private const int DwmwaCloaked = 14;
        private const int GwlExStyle = -20;
        private const long WsExToolWindow = 0x00000080L;

        private static readonly HashSet<string> ExcludedProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "GameBar",
            "GameBarFT",
            "GameBarFTServer",
            "XboxGameBar",
            "XboxPcApp",
            "XboxPcAppFT",
            "Microsoft.GamingApp",
            "GamingServices",
            "GamingServicesNet",
            "explorer",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "SearchHost",
            "SearchApp",
            "TextInputHost",
            "ApplicationFrameHost",
            "LockApp",
            "dwm",
            "RTSS",
            "RTSSHooksLoader",
            "RTSSGameBar.Helper",
            "RTSSGameBar.Widget",
            "RTSSGameBar.Setup",
            "Taskmgr"
        };

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        /// <summary>
        /// Returns the executable file name (for example "game.exe") of the best candidate for the
        /// running game, or null when it cannot be determined. Never throws.
        /// </summary>
        public static string TryGetLikelyGameExecutable()
        {
            try
            {
                var foreground = GetForegroundWindow();
                var foregroundName = GetProcessName(foreground);
                if (!string.IsNullOrEmpty(foregroundName) && !IsExcluded(foregroundName))
                    return foregroundName;

                return FindLargestVisibleWindowProcess();
            }
            catch
            {
                return null;
            }
        }

        private static string FindLargestVisibleWindowProcess()
        {
            var candidates = new List<KeyValuePair<long, IntPtr>>();

            try
            {
                EnumWindows((hWnd, lParam) =>
                {
                    if (!IsCandidateWindow(hWnd))
                        return true;

                    Rect rect;
                    if (!GetWindowRect(hWnd, out rect))
                        return true;

                    var width = Math.Max(0, rect.Right - rect.Left);
                    var height = Math.Max(0, rect.Bottom - rect.Top);
                    if (width <= 0 || height <= 0)
                        return true;

                    candidates.Add(new KeyValuePair<long, IntPtr>((long)width * height, hWnd));
                    return true;
                }, IntPtr.Zero);
            }
            catch
            {
                return null;
            }

            candidates.Sort((left, right) => right.Key.CompareTo(left.Key));
            foreach (var candidate in candidates)
            {
                var name = GetProcessName(candidate.Value);
                if (!string.IsNullOrEmpty(name) && !IsExcluded(name))
                    return name;
            }

            return null;
        }

        private static bool IsCandidateWindow(IntPtr hWnd)
        {
            try
            {
                if (!IsWindowVisible(hWnd))
                    return false;

                int cloaked;
                if (DwmGetWindowAttribute(hWnd, DwmwaCloaked, out cloaked, sizeof(int)) == 0 && cloaked != 0)
                    return false;

                var extendedStyle = GetWindowLong(hWnd, GwlExStyle);
                if ((extendedStyle & unchecked((int)WsExToolWindow)) != 0)
                    return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetProcessName(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
                return null;

            try
            {
                uint processId;
                GetWindowThreadProcessId(hWnd, out processId);
                if (processId == 0)
                    return null;

                using (var process = Process.GetProcessById(unchecked((int)processId)))
                {
                    var name = process.ProcessName;
                    return string.IsNullOrWhiteSpace(name) ? null : name + ".exe";
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool IsExcluded(string processName)
        {
            var baseName = processName;
            if (baseName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                baseName = baseName.Substring(0, baseName.Length - 4);
            return ExcludedProcessNames.Contains(baseName);
        }
    }
}
