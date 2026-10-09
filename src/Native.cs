using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WishGuard;

public sealed record GameWindow(nint Handle, Rectangle Bounds);
public static class Native
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    delegate bool EnumProc(nint hwnd, nint param);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, nint param);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(nint hwnd, ref POINT point);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, uint id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder fileName, ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] public static extern bool SetWindowDisplayAffinity(nint handle, uint affinity);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    public static void Raise(nint hwnd) => SetWindowPos(hwnd, new nint(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
    public static Rectangle? BoundsOf(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || !GetClientRect(hwnd, out var r)) return null;
        var p = new POINT();
        return ClientToScreen(hwnd, ref p) ? new Rectangle(p.X, p.Y, r.Right, r.Bottom) : null;
    }
    public static string ProcessName(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint id);
        nint process = OpenProcess(0x1000, false, id); // Query limited metadata only. Never VM_READ/VM_WRITE.
        if (process == 0) return "";
        try
        {
            uint size = 1024; var name = new StringBuilder((int)size);
            return QueryFullProcessImageName(process, 0, name, ref size) ? Path.GetFileNameWithoutExtension(name.ToString()) : "";
        }
        finally { CloseHandle(process); }
    }
    public static GameWindow? GetGame(nint demoHandle = 0)
    {
        GameWindow? found = null; nint foreground = GetForegroundWindow();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || IsIconic(h)) return true;
            bool isGame = demoHandle != 0 ? h == demoHandle : ProcessName(h) is "YuanShen" or "GenshinImpact";
            if (!isGame || !GetClientRect(h, out var r)) return true;
            var p = new POINT(); if (!ClientToScreen(h, ref p)) return true;
            if (r.Right < 640 || r.Bottom < 360) return true;
            var candidate = new GameWindow(h, new Rectangle(p.X, p.Y, r.Right, r.Bottom));
            if (found == null || h == foreground) found = candidate;
            return true;
        }, 0);
        return found;
    }
    public static Bitmap CaptureFooter(Rectangle client)
    {
        // Only the visible lower 32% of the foreground game's client area, in memory.
        int height = Math.Max(1, (int)(client.Height * .32));
        using var frame = new Bitmap(client.Width, height);
        using (var g = Graphics.FromImage(frame)) g.CopyFromScreen(client.X, client.Bottom - height, 0, 0, frame.Size, CopyPixelOperation.SourceCopy);
        int width = Math.Clamp(client.Width, 1100, 1600);
        var result = new Bitmap(width, Math.Max(1, height * width / client.Width));
        using var scaled = Graphics.FromImage(result);
        scaled.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        scaled.DrawImage(frame, 0, 0, result.Width, result.Height);
        return result;
    }
    public static bool IsGameRunning(string? testProcess = null)
    {
        foreach (var name in testProcess != null ? new[] { testProcess } : new[] { "YuanShen", "GenshinImpact" })
        {
            var matches = Process.GetProcessesByName(name);
            // The process-name snapshot is enough for lifecycle detection. HasExited
            // can require additional access to an elevated game process and fail.
            bool any = matches.Length > 0;
            foreach (var p in matches) p.Dispose();
            if (any) return true;
        }
        return false;
    }
    public static Bitmap CaptureFrame(Rectangle client)
    {
        using var frame = new Bitmap(client.Width, client.Height);
        using (var g = Graphics.FromImage(frame)) g.CopyFromScreen(client.Location, Point.Empty, client.Size, CopyPixelOperation.SourceCopy);
        int width = Math.Min(1920, client.Width);
        return ResourceReader.Crop(frame, new Rectangle(Point.Empty, frame.Size), (double)width / client.Width);
    }
    public static bool HasContent(Bitmap b)
    {
        double sum = 0, square = 0; int count = 0;
        for (int y = 0; y < b.Height; y += 12)
        for (int x = 0; x < b.Width; x += 12)
        { var c = b.GetPixel(x, y); double n = (c.R + c.G + c.B) / 3.0; sum += n; square += n * n; count++; }
        // A plain bright scene is valid. Only reject near-black/empty captures.
        return count > 0 && (sum / count > 8 || square / count - Math.Pow(sum / count, 2) > 24);
    }
}
