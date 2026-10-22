using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Playnite.Gameroom
{
    public class ProcessSnapshot
    {
        public int Id { get; set; }
        public string Path { get; set; }
        public DateTime StartTime { get; set; }
        /// <summary>Process owns a visible top level window.</summary>
        public bool HasWindow { get; set; }
        /// <summary>Process owns the foreground window.</summary>
        public bool IsForeground { get; set; }
        /// <summary>Process owns a window covering a whole monitor (exclusive or borderless fullscreen).</summary>
        public bool IsFullscreen { get; set; }

        public override string ToString()
        {
            return $"{Id} {Path}";
        }
    }

    /// <summary>
    /// Lists processes of the current user session with their executable paths.
    /// Uses only PROCESS_QUERY_LIMITED_INFORMATION, which works for 64-bit processes from our 32-bit app
    /// and doesn't require elevation for processes of the same user.
    /// </summary>
    public class ProcessScanner
    {
        private readonly Dictionary<(int, long), string> pathCache = new Dictionary<(int, long), string>();
        private readonly int currentSessionId = Process.GetCurrentProcess().SessionId;
        private readonly int currentProcessId = Process.GetCurrentProcess().Id;

        public List<ProcessSnapshot> Scan()
        {
            var windows = GetWindowInfo();
            var result = new List<ProcessSnapshot>();
            var seenKeys = new HashSet<(int, long)>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == currentProcessId || process.Id <= 4 || process.SessionId != currentSessionId)
                        {
                            continue;
                        }

                        var snapshot = GetSnapshot(process.Id, seenKeys);
                        if (snapshot == null)
                        {
                            continue;
                        }

                        if (windows.TryGetValue(snapshot.Id, out var win))
                        {
                            snapshot.HasWindow = true;
                            snapshot.IsForeground = win.foreground;
                            snapshot.IsFullscreen = win.fullscreen;
                        }

                        result.Add(snapshot);
                    }
                    catch
                    {
                        // Process exited during the scan or is protected.
                    }
                }
            }

            // Drop cache entries of processes that are gone.
            if (pathCache.Count > seenKeys.Count * 2)
            {
                var stale = new List<(int, long)>();
                foreach (var key in pathCache.Keys)
                {
                    if (!seenKeys.Contains(key))
                    {
                        stale.Add(key);
                    }
                }

                stale.ForEach(a => pathCache.Remove(a));
            }

            return result;
        }

        private ProcessSnapshot GetSnapshot(int processId, HashSet<(int, long)> seenKeys)
        {
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
                {
                    return null;
                }

                var key = (processId, creation);
                seenKeys.Add(key);
                if (!pathCache.TryGetValue(key, out var path))
                {
                    var builder = new StringBuilder(1024);
                    var size = (uint)builder.Capacity;
                    path = QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString() : null;
                    pathCache[key] = path;
                }

                if (string.IsNullOrEmpty(path))
                {
                    return null;
                }

                return new ProcessSnapshot
                {
                    Id = processId,
                    Path = path,
                    StartTime = DateTime.FromFileTime(creation)
                };
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        public static bool IsProcessRunning(int processId, DateTime startTime)
        {
            var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                if (!GetExitCodeProcess(handle, out var exitCode) || exitCode != STILL_ACTIVE)
                {
                    return false;
                }

                return GetProcessTimes(handle, out var creation, out _, out _, out _) &&
                    Math.Abs((DateTime.FromFileTime(creation) - startTime).TotalSeconds) < 2;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static Dictionary<int, (bool foreground, bool fullscreen)> GetWindowInfo()
        {
            var result = new Dictionary<int, (bool foreground, bool fullscreen)>();
            var foregroundWindow = GetForegroundWindow();
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
                {
                    return true;
                }

                var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                {
                    return true;
                }

                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid == 0)
                {
                    return true;
                }

                var isForeground = hwnd == foregroundWindow;
                var isFullscreen = IsFullscreenWindow(hwnd);
                if (result.TryGetValue((int)pid, out var existing))
                {
                    result[(int)pid] = (existing.foreground || isForeground, existing.fullscreen || isFullscreen);
                }
                else
                {
                    result[(int)pid] = (isForeground, isFullscreen);
                }

                return true;
            }, IntPtr.Zero);

            return result;
        }

        private static bool IsFullscreenWindow(IntPtr hwnd)
        {
            if (!GetWindowRect(hwnd, out var rect))
            {
                return false;
            }

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONULL);
            if (monitor == IntPtr.Zero)
            {
                return false;
            }

            var info = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(monitor, ref info))
            {
                return false;
            }

            var mon = info.rcMonitor;
            return rect.Left <= mon.Left && rect.Top <= mon.Top && rect.Right >= mon.Right && rect.Bottom >= mon.Bottom;
        }

        #region Native

        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint STILL_ACTIVE = 259;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const uint MONITOR_DEFAULTTONULL = 0;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr handle, out long creationTime, out long exitTime, out long kernelTime, out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        #endregion Native
    }
}
