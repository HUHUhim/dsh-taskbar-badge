using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

internal static class Program
{
    private const string MutexName = @"Local\DSHTaskbarBadge";
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DSH");
    private static readonly string LogPath = Path.Combine(DataDirectory, "taskbar-badge.log");
    private static readonly string BoundsPath = Path.Combine(DataDirectory, "window-bounds.txt");
    private static readonly BoundsState SavedBounds = LoadBounds();
    private static IntPtr boundsWindow = IntPtr.Zero;
    private static DateTime boundsRestoreAt = DateTime.MinValue;
    private static int boundsRestoreTries;
    private static bool boundsRestored;
    private static BoundsState lastBounds;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "--test", StringComparison.OrdinalIgnoreCase))
        {
            int testCount = 1;
            if (args.Length > 1) int.TryParse(args[1], out testCount);
            RunTest(Math.Max(1, Math.Min(99, testCount)));
            return;
        }

        bool created;
        using (var mutex = new Mutex(true, MutexName, out created))
        {
            if (!created) return;
            RunLoop();
        }
    }

    private static void RunTest(int count)
    {
        var taskbar = CreateTaskbar();
        IntPtr hwnd = WaitForWindow(8000);
        if (hwnd == IntPtr.Zero)
        {
            Log("test: DSH window not found");
            return;
        }
        Apply(taskbar, hwnd, count);
        Thread.Sleep(8000);
        Apply(taskbar, hwnd, 0);
    }

    private static void RunLoop()
    {
        var taskbar = CreateTaskbar();
        Log("loop start");
        IntPtr lastWindow = IntPtr.Zero;
        int lastCount = -1;

        while (true)
        {
            try
            {
                IntPtr hwnd = FindMainWindow();
                if (hwnd != IntPtr.Zero)
                {
                    int count = CountForWindow(hwnd);
                    if (hwnd != lastWindow || count != lastCount)
                    {
                        SetOverlay(taskbar, hwnd, count);
                        lastWindow = hwnd;
                        lastCount = count;
                    }
                    TrackBounds(hwnd);
                }
                else if (lastWindow != IntPtr.Zero)
                {
                    lastWindow = IntPtr.Zero;
                    lastCount = -1;
                    boundsWindow = IntPtr.Zero;
                    boundsRestoreTries = 0;
                    boundsRestored = false;
                    lastBounds = null;
                }
            }
            catch (Exception error)
            {
                Log("loop: " + error.Message);
            }
            Thread.Sleep(800);
        }
    }

    private static void TrackBounds(IntPtr hwnd)
    {
        if (hwnd != boundsWindow)
        {
            boundsWindow = hwnd;
            boundsRestoreTries = 0;
            boundsRestoreAt = DateTime.UtcNow.AddMilliseconds(1200);
            boundsRestored = SavedBounds == null;
            lastBounds = null;
        }

        if (!boundsRestored)
        {
            if (DateTime.UtcNow < boundsRestoreAt) return;
            if (IsWindowVisible(hwnd) && TryRestoreBounds(hwnd, SavedBounds))
            {
                boundsRestored = true;
                lastBounds = SavedBounds;
                Log("bounds restore " + SavedBounds.Left + "," + SavedBounds.Top + " " +
                    SavedBounds.Width + "x" + SavedBounds.Height +
                    (SavedBounds.Maximized ? " maximized" : ""));
                return;
            }

            boundsRestoreTries++;
            if (boundsRestoreTries >= 12)
            {
                boundsRestored = true;
                Log("bounds restore skipped");
            }
            else
            {
                boundsRestoreAt = DateTime.UtcNow.AddMilliseconds(700);
            }
            return;
        }

        BoundsState current = ReadBounds(hwnd);
        if (current == null) return;
        if (lastBounds == null || !lastBounds.Same(current))
        {
            SaveBounds(current);
            lastBounds = current;
        }
    }

    private static bool TryRestoreBounds(IntPtr hwnd, BoundsState state)
    {
        if (state == null) return false;
        RECT rect = ClampToVisible(state);
        var placement = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)),
            showCmd = state.Maximized ? 3 : 1,
            rcNormalPosition = rect
        };
        return SetWindowPlacement(hwnd, ref placement);
    }

    private static RECT ClampToVisible(BoundsState state)
    {
        var rect = new RECT
        {
            Left = state.Left,
            Top = state.Top,
            Right = state.Left + state.Width,
            Bottom = state.Top + state.Height
        };

        IntPtr monitor = MonitorFromRect(ref rect, 2);
        var info = new MONITORINFO();
        info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
        if (!GetMonitorInfo(monitor, ref info)) return rect;

        RECT work = info.rcWork;
        bool intersects = rect.Right > work.Left && rect.Left < work.Right &&
                          rect.Bottom > work.Top && rect.Top < work.Bottom;
        if (!intersects)
        {
            int workWidth = work.Right - work.Left;
            int workHeight = work.Bottom - work.Top;
            int width = Math.Min(state.Width, workWidth);
            int height = Math.Min(state.Height, workHeight);
            rect.Left = work.Left + Math.Max(0, (workWidth - width) / 2);
            rect.Top = work.Top + Math.Max(0, (workHeight - height) / 2);
            rect.Right = rect.Left + width;
            rect.Bottom = rect.Top + height;
        }
        return rect;
    }

    private static BoundsState ReadBounds(IntPtr hwnd)
    {
        var placement = new WINDOWPLACEMENT();
        placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
        if (!GetWindowPlacement(hwnd, ref placement)) return null;

        RECT rect = placement.rcNormalPosition;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width < 520 || height < 600) return null;

        bool maximized;
        if (placement.showCmd == 2)
        {
            maximized = lastBounds != null
                ? lastBounds.Maximized
                : SavedBounds != null && SavedBounds.Maximized;
        }
        else
        {
            maximized = placement.showCmd == 3;
        }

        return new BoundsState
        {
            Left = rect.Left,
            Top = rect.Top,
            Width = width,
            Height = height,
            Maximized = maximized
        };
    }

    private static BoundsState LoadBounds()
    {
        try
        {
            if (!File.Exists(BoundsPath)) return null;
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(BoundsPath))
            {
                int separator = line.IndexOf('=');
                if (separator <= 0) continue;
                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }

            int left, top, width, height;
            if (!TryGetInt(values, "left", out left) ||
                !TryGetInt(values, "top", out top) ||
                !TryGetInt(values, "width", out width) ||
                !TryGetInt(values, "height", out height)) return null;
            if (width < 520 || height < 600 || width > 20000 || height > 20000) return null;

            string maximizedText;
            bool maximized = values.TryGetValue("maximized", out maximizedText) && maximizedText == "1";
            return new BoundsState
            {
                Left = left,
                Top = top,
                Width = width,
                Height = height,
                Maximized = maximized
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetInt(Dictionary<string, string> values, string key, out int value)
    {
        value = 0;
        string text;
        return values.TryGetValue(key, out text) && int.TryParse(text, out value);
    }

    private static void SaveBounds(BoundsState state)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string text =
                "left=" + state.Left + "\n" +
                "top=" + state.Top + "\n" +
                "width=" + state.Width + "\n" +
                "height=" + state.Height + "\n" +
                "maximized=" + (state.Maximized ? "1" : "0") + "\n";
            string temporary = BoundsPath + ".tmp";
            File.WriteAllText(temporary, text);
            if (File.Exists(BoundsPath)) File.Delete(BoundsPath);
            File.Move(temporary, BoundsPath);
        }
        catch { }
    }

    private sealed class BoundsState
    {
        public int Left;
        public int Top;
        public int Width;
        public int Height;
        public bool Maximized;

        public bool Same(BoundsState other)
        {
            return other != null &&
                   Left == other.Left &&
                   Top == other.Top &&
                   Width == other.Width &&
                   Height == other.Height &&
                   Maximized == other.Maximized;
        }
    }

    private static ITaskbarList3 CreateTaskbar()
    {
        Guid clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
        Guid iid = new Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF");
        IntPtr pointer;
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out pointer);
        if (hr != 0)
        {
            Log("CoCreateInstance failed: 0x" + hr.ToString("X8"));
            throw new COMException("CoCreateInstance failed: 0x" + hr.ToString("X8"), hr);
        }
        try
        {
            var taskbar = (ITaskbarList3)Marshal.GetObjectForIUnknown(pointer);
            taskbar.HrInit();
            return taskbar;
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    private static IntPtr WaitForWindow(int timeoutMs)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            IntPtr hwnd = FindMainWindow();
            if (hwnd != IntPtr.Zero) return hwnd;
            Thread.Sleep(200);
        }
        return IntPtr.Zero;
    }

    private static IntPtr FindMainWindow()
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = 0;

        EnumWindows(delegate(IntPtr hwnd, IntPtr lparam)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            Process process;
            try { process = Process.GetProcessById((int)pid); }
            catch { return true; }
            if (!string.Equals(process.ProcessName, "DeepSeek Harness", StringComparison.OrdinalIgnoreCase)) return true;

            var title = new StringBuilder(512);
            GetWindowText(hwnd, title, title.Capacity);
            string text = title.ToString();
            if (text.IndexOf("DeepSeek Harness", StringComparison.OrdinalIgnoreCase) < 0) return true;
            if (text.StartsWith("Developer Tools", StringComparison.OrdinalIgnoreCase)) return true;

            RECT rect;
            GetWindowRect(hwnd, out rect);
            long area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
            if (area >= bestArea)
            {
                bestArea = area;
                best = hwnd;
            }
            return true;
        }, IntPtr.Zero);

        return best;
    }

    private static readonly Regex TitlePrefix = new Regex(@"^\[(\d+)\]\s", RegexOptions.Compiled);

    private static int CountForWindow(IntPtr hwnd)
    {
        string title = GetWindowTitle(hwnd);
        Match match = TitlePrefix.Match(title);
        if (match.Success)
        {
            int count;
            if (int.TryParse(match.Groups[1].Value, out count)) return count;
        }
        return CountCompleted(hwnd);
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var title = new StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);
        return title.ToString();
    }

    private static int CountCompleted(IntPtr hwnd)
    {
        AutomationElement root;
        try { root = AutomationElement.FromHandle(hwnd); }
        catch { return 0; }

        var condition = new PropertyCondition(
            AutomationElement.ControlTypeProperty,
            ControlType.TreeItem);

        AutomationElementCollection items;
        try { items = root.FindAll(TreeScope.Descendants, condition); }
        catch { return 0; }

        int count = 0;
        foreach (AutomationElement item in items)
        {
            string name;
            try { name = item.Current.Name; }
            catch { continue; }
            if (string.IsNullOrEmpty(name)) continue;

            if (name.StartsWith("已完成", StringComparison.Ordinal) ||
                name.StartsWith("Completed", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Done", StringComparison.OrdinalIgnoreCase))
            {
                count++;
            }
        }
        return count;
    }

    private static void Apply(ITaskbarList3 taskbar, IntPtr hwnd, int count)
    {
        SetOverlay(taskbar, hwnd, count);
    }

    private static void SetOverlay(ITaskbarList3 taskbar, IntPtr hwnd, int count)
    {
        IntPtr icon = count > 0 ? CreateBadgeIcon(count) : IntPtr.Zero;
        string description = count > 0 ? count + " 个会话已完成" : "";
        int hr;
        try { hr = taskbar.SetOverlayIcon(hwnd, icon, description); }
        catch (Exception error)
        {
            Log("SetOverlayIcon threw: " + error.Message);
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            return;
        }

        if (hr != 0)
        {
            Log("SetOverlayIcon failed: 0x" + hr.ToString("X8"));
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            return;
        }

        Log("overlay=" + count);
        if (icon != IntPtr.Zero && lastIcon != IntPtr.Zero) DestroyIcon(lastIcon);
        lastIcon = icon;
    }

    private static IntPtr lastIcon = IntPtr.Zero;

    private static IntPtr CreateBadgeIcon(int count)
    {
        string text = count > 99 ? "99+" : count.ToString();
        using (var bitmap = new Bitmap(24, 24))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

                using (var background = new SolidBrush(Color.FromArgb(255, 47, 107, 255)))
                using (var border = new Pen(Color.FromArgb(255, 255, 255), 1.0f))
                {
                    graphics.FillEllipse(background, 0.5f, 0.5f, 23f, 23f);
                    graphics.DrawEllipse(border, 0.5f, 0.5f, 23f, 23f);
                }

                float size = text.Length == 1 ? 14f : text.Length == 2 ? 10f : 7f;
                using (var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                })
                {
                    graphics.DrawString(text, font, Brushes.White, new RectangleF(0, 0, 24, 24), format);
                }
            }
            return bitmap.GetHicon();
        }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
            File.AppendAllText(LogPath, DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lparam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lparam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr icon);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr outer,
        uint classContext,
        ref Guid iid,
        out IntPtr instance);

    [ComImport]
    [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarList { }

    [ComImport]
    [Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr hwndTab, IntPtr hwndMdi);
        void UnregisterTab(IntPtr hwndTab);
        void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
        void SetTabActive(IntPtr hwndTab, IntPtr hwndMdi, uint reserved);
        void ThumbBarAddButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarUpdateButtons(IntPtr hwnd, uint count, IntPtr buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr imageList);
        [PreserveSig]
        int SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tooltip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }
}
