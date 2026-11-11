using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using VajraShelf.Models;
using Screen = System.Windows.Forms.Screen;

namespace VajraShelf.Services;

public sealed class DesktopService
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint GaRoot = 2;
    private const int ExtendedFrameBounds = 9;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    public Rect GetWorkArea(int monitorIndex) => ToDips(GetMonitorArea(monitorIndex));

    public Rect Place(double width, double height, AppSettings settings)
    {
        settings ??= new AppSettings();
        return CalculatePlacement(ToDips(GetMonitorArea(settings.MonitorIndex)), width, height, settings.TopOffset);
    }

    public Rect PlaceShelf(bool expandedHost, AppSettings settings)
    {
        settings ??= new AppSettings();
        return ShelfGeometry.HostPlacement(ToDips(GetMonitorArea(settings.MonitorIndex)),
            settings.Scale, settings.TopOffset, expandedHost);
    }

    public bool TryPlaceShelfWindow(IntPtr shelfHandle, bool expandedHost, AppSettings settings)
    {
        settings ??= new AppSettings();
        MonitorArea monitor = GetMonitorArea(settings.MonitorIndex);
        Rect placement = ShelfGeometry.HostPlacement(ToDips(monitor), settings.Scale,
            settings.TopOffset, expandedHost);
        return TryPlaceWindowCore(shelfHandle, placement, monitor);
    }

    public bool TryPlaceWindow(IntPtr handle, double width, double height, AppSettings settings)
    {
        settings ??= new AppSettings();
        MonitorArea monitor = GetMonitorArea(settings.MonitorIndex);
        return TryPlaceWindowCore(handle,
            CalculatePlacement(ToDips(monitor), width, height, settings.TopOffset), monitor);
    }

    private static bool TryPlaceWindowCore(IntPtr handle, Rect placement, MonitorArea monitor)
    {
        if (handle == IntPtr.Zero) return false;
        try
        {
            if (!IsWindow(handle) || GetWindowThreadProcessId(handle, out uint processId) == 0 ||
                processId != (uint)Environment.ProcessId) return false;

            int pixelWidth = Math.Max(1, Pixel(placement.Width * monitor.ScaleX));
            if (pixelWidth > 1 && (pixelWidth & 1) != 0) pixelWidth--;
            int pixelHeight = Math.Max(1, Pixel(placement.Height * monitor.ScaleY));
            int centerX = Pixel((placement.X + placement.Width / 2) * monitor.ScaleX);
            int x = centerX - pixelWidth / 2;
            int y = Pixel(placement.Y * monitor.ScaleY);
            uint flags = SwpNoZOrder | SwpNoActivate | SwpNoOwnerZOrder;
            if (MatchesWindowRect(handle, x, y, pixelWidth, pixelHeight)) return true;
            if (!SetWindowPos(handle, IntPtr.Zero, x, y, pixelWidth, pixelHeight, flags)) return false;
            return MatchesWindowRect(handle, x, y, pixelWidth, pixelHeight) ||
                SetWindowPos(handle, IntPtr.Zero, x, y, pixelWidth, pixelHeight, flags);
        }
        catch (Exception) { return false; }
    }

    private static bool MatchesWindowRect(IntPtr handle, int x, int y, int width, int height) =>
        GetWindowRect(handle, out NativeRect actual) && actual.Left == x && actual.Top == y &&
        (long)actual.Right - actual.Left == width && (long)actual.Bottom - actual.Top == height;

    public bool IsFullscreenApp(IntPtr shelfHandle)
    {
        try
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(foreground, GaRoot);
            if (root != IntPtr.Zero) foreground = root;
            IntPtr shelfRoot = shelfHandle == IntPtr.Zero ? IntPtr.Zero : GetAncestor(shelfHandle, GaRoot);
            if (foreground == shelfHandle || foreground == shelfRoot ||
                foreground == GetDesktopWindow() || foreground == GetShellWindow() ||
                !IsWindowVisible(foreground) || IsIconic(foreground)) return false;
            if (GetWindowThreadProcessId(foreground, out uint processId) == 0 ||
                processId == (uint)Environment.ProcessId) return false;

            StringBuilder className = new(256);
            GetClassName(foreground, className, className.Capacity);
            if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return false;

            IntPtr monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
            MonitorInfo info = new() { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return false;
            NativeRect bounds;
            if (DwmGetWindowAttribute(foreground, ExtendedFrameBounds, out bounds,
                    Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(foreground, out bounds)) return false;
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top) return false;
            NativeRect display = info.Monitor;
            const int tolerance = 1;
            return (long)bounds.Left <= (long)display.Left + tolerance &&
                   (long)bounds.Top <= (long)display.Top + tolerance &&
                   (long)bounds.Right >= (long)display.Right - tolerance &&
                   (long)bounds.Bottom >= (long)display.Bottom - tolerance;
        }
        catch (Exception) { return false; }
    }

    private static Rect CalculatePlacement(Rect workArea, double width, double height, double topOffset)
    {
        width = Math.Clamp(double.IsFinite(width) && width > 0 ? width : 1, 1, Math.Max(1, workArea.Width));
        height = Math.Clamp(double.IsFinite(height) && height > 0 ? height : 1, 1, Math.Max(1, workArea.Height));
        topOffset = double.IsFinite(topOffset) ? Math.Clamp(topOffset, 0, 200) : 12;
        topOffset = Math.Min(topOffset, Math.Max(0, workArea.Height - height));
        return new Rect(workArea.Left + (workArea.Width - width) / 2,
            workArea.Top + topOffset, width, height);
    }

    private static Rect ToDips(MonitorArea monitor) => new(
        monitor.WorkArea.Left / monitor.ScaleX,
        monitor.WorkArea.Top / monitor.ScaleY,
        ((double)monitor.WorkArea.Right - monitor.WorkArea.Left) / monitor.ScaleX,
        ((double)monitor.WorkArea.Bottom - monitor.WorkArea.Top) / monitor.ScaleY);

    private static MonitorArea GetMonitorArea(int monitorIndex)
    {
        try
        {
            Screen[] screens = Screen.AllScreens;
            Screen? screen = monitorIndex >= 0 && monitorIndex < screens.Length ? screens[monitorIndex] : Screen.PrimaryScreen;
            if (screen == null && screens.Length > 0) screen = screens[0];
            if (screen != null)
            {
                Rectangle bounds = screen.Bounds;
                NativePoint center = new() { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
                IntPtr handle = MonitorFromPoint(center, MonitorDefaultToNearest);
                MonitorInfo info = new() { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
                NativeRect work = GetMonitorInfo(handle, ref info) ? info.WorkArea : FromRectangle(screen.WorkingArea);
                (double scaleX, double scaleY) = GetMonitorScale(handle);
                if (work.Right > work.Left && work.Bottom > work.Top)
                    return new MonitorArea(work, scaleX, scaleY);
            }
        }
        catch (Exception) { }

        try
        {
            Rect work = SystemParameters.WorkArea;
            double scale = GetSystemScale();
            if (!work.IsEmpty && work.Width > 0 && work.Height > 0)
                return new MonitorArea(new NativeRect
                {
                    Left = Pixel(work.Left * scale), Top = Pixel(work.Top * scale),
                    Right = Pixel(work.Right * scale), Bottom = Pixel(work.Bottom * scale)
                }, scale, scale);
        }
        catch (Exception) { }
        return new MonitorArea(new NativeRect { Right = 1920, Bottom = 1080 }, 1, 1);
    }

    private static (double X, double Y) GetMonitorScale(IntPtr monitor)
    {
        try
        {
            if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, 0, out uint x, out uint y) == 0 && x > 0 && y > 0)
                return (x / 96.0, y / 96.0);
        }
        catch (Exception) { }
        double scale = GetSystemScale();
        return (scale, scale);
    }

    private static double GetSystemScale()
    {
        try { uint dpi = GetDpiForSystem(); return dpi > 0 ? dpi / 96.0 : 1; }
        catch (Exception) { return 1; }
    }

    private static int Pixel(double value) => (int)Math.Clamp(
        Math.Round(value, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
    private static NativeRect FromRectangle(Rectangle rect) => new()
        { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
    private readonly record struct MonitorArea(NativeRect WorkArea, double ScaleX, double ScaleY);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public uint Size; public NativeRect Monitor; public NativeRect WorkArea; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int count);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out NativeRect value, int size);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
