using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
namespace WishGuard;

public static class Lifecycle
{
    const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static string MutexName(string data, bool watch = false) => "Local\\WishGuard-" + (watch ? "watch-" : "") +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(data).ToLowerInvariant())));
    public static bool Active(string data, bool watch)
    {
        if (!Mutex.TryOpenExisting(MutexName(data, watch), out var mutex)) return false;
        mutex.Dispose(); return true;
    }
    public static void Launch(string data, bool watch, bool isolated = false, string? testProcess = null)
    {
        if (Active(data, watch)) return;
        var p = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
        p.ArgumentList.Add(watch ? "--watch" : "--session"); p.ArgumentList.Add("--data"); p.ArgumentList.Add(data);
        if (isolated) p.ArgumentList.Add("--isolated");
        if (testProcess != null) { p.ArgumentList.Add("--test-process"); p.ArgumentList.Add(testProcess); }
        using var child = Process.Start(p);
    }
    public static void Enable(string data, bool isolated, string? testProcess = null)
    {
        if (!isolated)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunPath);
            key.SetValue("WishGuard", $"\"{Application.ExecutablePath}\" --watch --data \"{data}\"");
        }
        Launch(data, true, isolated, testProcess);
    }
    public static void Disable(string data, bool isolated)
    {
        if (isolated) return;
        using var key = Registry.CurrentUser.OpenSubKey(RunPath, true);
        string? value = key?.GetValue("WishGuard") as string;
        if (value != null && (value.Contains($"\"{data}\"", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith($"\"{Application.ExecutablePath}\"", StringComparison.OrdinalIgnoreCase))) key!.DeleteValue("WishGuard", false);
    }
}
public sealed class WatcherContext : ApplicationContext
{
    readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    readonly string data, publicKey;
    readonly bool isolated;
    readonly string? testProcess;
    readonly NotifyIcon tray;
    DateTime retry = DateTime.MinValue;
    public WatcherContext(string data, string publicKey, bool isolated, string? testProcess)
    {
        this.data = data; this.publicKey = publicKey; this.isolated = isolated; this.testProcess = testProcess;
        var menu = new ContextMenuStrip(); menu.Items.Add("打开攒愿计划", null, (_, _) => Lifecycle.Launch(data, false, isolated, testProcess));
        tray = new NotifyIcon { Icon = SystemIcons.Shield, Text = "攒愿 · 等待原神启动（不截图）", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Lifecycle.Launch(data, false, isolated, testProcess);
        timer.Tick += (_, _) => Tick(); timer.Start();
    }
    void Tick()
    {
        try
        {
            var store = new StateStore(data);
            if (!store.Fault && (Rules.Ended(store.State, publicKey) || !store.State.Committed || !store.State.StartWithWindows))
            {
                Lifecycle.Disable(data, isolated); store.Log("watcher_stopped", "计划已结束或未启用联动"); ExitThread(); return;
            }
            bool game = Native.IsGameRunning(testProcess);
            tray.Visible = !Lifecycle.Active(data, false);
            tray.Text = game ? "攒愿 · 原神运行中" : "攒愿 · 等待原神启动（不截图）";
            if (game && DateTime.UtcNow >= retry && !Lifecycle.Active(data, false))
            { retry = DateTime.UtcNow.AddSeconds(15); Lifecycle.Launch(data, false, isolated, testProcess); }
        }
        catch { retry = DateTime.UtcNow.AddSeconds(15); }
    }
    protected override void ExitThreadCore()
    { timer.Stop(); timer.Dispose(); tray.Visible = false; tray.Dispose(); base.ExitThreadCore(); }
}
