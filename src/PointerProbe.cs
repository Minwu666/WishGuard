#if WISHGUARD_TESTS
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
namespace WishGuard;

// Interactive, isolated input-path regression. The operator supplies real clicks;
// this fixture neither generates input nor enrolls a real game or real plan.
internal static class PointerProbe
{
    [StructLayout(LayoutKind.Sequential)] struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RawDevice { public ushort Page, Usage; public uint Flags; public nint Window; }
    [StructLayout(LayoutKind.Sequential)] struct RawHeader { public uint Type, Size; public nint Device; public nuint WParam; }
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] static extern nint WindowFromPhysicalPoint(PointNative point);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputData(nint raw, uint command, byte[]? data, ref uint size, uint headerSize);

    sealed record Counts(long Legacy, long Raw, long Async, bool RawRegistered, int RawError, long Sequence, long WrittenAt, long Window, long Foreground, bool LeftDown);
    sealed record Surface(nint Window, Rectangle InputArea);
    sealed record ForegroundEvent(long At, long Window);

    static bool WriteShared<T>(string path, T value)
    {
        string temp = path + ".tmp";
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (value is string text)
                    { using var writer = new StreamWriter(output); writer.Write(text); }
                    else JsonSerializer.Serialize(output, value);
                }
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                return true;
            }
            catch (IOException) { if (attempt < 3) Thread.Sleep(5); }
        }
        return false;
    }
    static T? ReadShared<T>(string path) where T : class
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(input);
            }
            catch (Exception e) when (e is IOException or JsonException)
            { if (attempt < 3) Thread.Sleep(5); }
        }
        return null;
    }

    public static int Demo(string root)
    {
        root = Path.GetFullPath(root);
        if (!Directory.Exists(root) || !string.Equals(Path.GetFileName(Environment.ProcessPath), "WishGuardProbeGame.exe", StringComparison.OrdinalIgnoreCase)) return 3;
        using var game = new PointerGame(Path.Combine(root, "game-state.json"));
        Application.Run(game); return 0;
    }

    sealed class PointerGame : Form
    {
        readonly string report;
        readonly System.Windows.Forms.Timer writer = new() { Interval = 75 };
        System.Threading.Timer? sampler;
        Surface? surface;
        long legacy, raw, asyncCount, sequence;
        int sampling;
        bool previousDown, rawRegistered;
        int rawError;
        Rectangle InputArea => new(100, Math.Max(100, ClientSize.Height / 2 - 55), Math.Max(100, ClientSize.Width - 200), 150);
        public PointerGame(string report)
        {
            this.report = report; Text = "隔离输入测试 · 模拟游戏";
            AutoScaleMode = AutoScaleMode.None; ClientSize = new(860, 500); MinimumSize = new(660, 420);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(29, 46, 62);
            DoubleBuffered = true; Font = new("Microsoft YaHei UI", 12);
            writer.Tick += (_, _) => Publish();
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e); UpdateSurface();
            rawRegistered = RegisterRawInputDevices([new RawDevice { Page = 1, Usage = 2, Window = Handle }], 1, (uint)Marshal.SizeOf<RawDevice>());
            if (!rawRegistered) rawError = Marshal.GetLastWin32Error();
            sampler = new System.Threading.Timer(_ => SampleAsync(), null, 0, 1);
            writer.Start(); Publish();
        }
        void UpdateSurface()
        { if (IsHandleCreated) Volatile.Write(ref surface, new(Handle, RectangleToScreen(InputArea))); }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); UpdateSurface(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateSurface(); Invalidate(); }
        bool InInputArea()
        {
            var current = Volatile.Read(ref surface);
            return current != null && Native.GetForegroundWindow() == current.Window && GetCursorPos(out var cursor) && current.InputArea.Contains(cursor.X, cursor.Y);
        }
        void SampleAsync()
        {
            if (Interlocked.Exchange(ref sampling, 1) != 0) return;
            try
            {
                bool down = GetAsyncKeyState(1) < 0;
                if (down && !previousDown && InInputArea()) Interlocked.Increment(ref asyncCount);
                previousDown = down;
            }
            finally { Volatile.Write(ref sampling, 0); }
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && InputArea.Contains(e.Location)) Interlocked.Increment(ref legacy);
            base.OnMouseDown(e);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0xFF)
            {
                uint size = 0, header = (uint)Marshal.SizeOf<RawHeader>();
                if (GetRawInputData(message.LParam, 0x10000003, null, ref size, header) != uint.MaxValue && size >= header + 8 && size < 65536)
                {
                    byte[] data = new byte[(int)size];
                    if (GetRawInputData(message.LParam, 0x10000003, data, ref size, header) != uint.MaxValue &&
                        BitConverter.ToUInt32(data, 0) == 0 && (BitConverter.ToUInt16(data, (int)header + 4) & 1) != 0 && InInputArea())
                        Interlocked.Increment(ref raw);
                }
            }
            base.WndProc(ref message);
        }
        void Publish()
        {
            WriteShared(report, new Counts(Interlocked.Read(ref legacy), Interlocked.Read(ref raw), Interlocked.Read(ref asyncCount), rawRegistered, rawError,
                ++sequence, Environment.TickCount64, (long)Handle, (long)Native.GetForegroundWindow(), GetAsyncKeyState(1) < 0));
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(46, 102, 133)); e.Graphics.FillRectangle(brush, InputArea);
            TextRenderer.DrawText(e.Graphics, "输入测试\n请在蓝色区域点击，短暂按住再松开", Font, InputArea, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, "隔离模拟窗口，不连接真实游戏或计划。", Font, new Point(26, 30), Color.White);
            TextRenderer.DrawText(e.Graphics, $"普通点击 {Interlocked.Read(ref legacy)}    Raw Input {Interlocked.Read(ref raw)}    异步状态 {Interlocked.Read(ref asyncCount)}", Font,
                new Point(26, ClientSize.Height - 58), Color.LightGoldenrodYellow);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { Volatile.Write(ref surface, null); sampler?.Dispose(); writer.Stop(); writer.Dispose(); }
            base.Dispose(disposing);
        }
    }

    public static int Run(string root, string enforcer)
    {
        root = Path.GetFullPath(root); enforcer = Path.GetFullPath(enforcer);
        if (Directory.Exists(root)) throw new IOException("Use a fresh directory.");
        if (!string.Equals(Path.GetFileName(Environment.ProcessPath), "WishGuard.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("Run this fixture through WishGuard.exe.");
        Directory.CreateDirectory(root); string games = Path.Combine(root, "game"); Directory.CreateDirectory(games);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(games, Path.GetFileName(file)));
        string fake = Path.Combine(games, "WishGuardProbeGame.exe"); File.Copy(Path.Combine(games, "WishGuard.exe"), fake);
        var profile = new EnforcementProfile { OwnerSid = EnforcementPaths.Owner, ClientDirectory = AppContext.BaseDirectory, DataDirectory = Path.Combine(root, "data"),
            EnforcerExecutable = enforcer, PipeName = "WishGuard-pointer-" + Guid.NewGuid().ToString("N"), GamePaths = [fake], TestOnly = true,
            Policy = new GuardState { Committed = true, TargetPulls = 600, UnlockMode = "permanent" } };
        foreach (string name in new[] { "WishGuard.exe", "WishGuard.dll", "WishGuard.deps.json", "WishGuard.runtimeconfig.json", "ocr-worker.ps1" })
            profile.ClientHashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))));
        string fileProfile = Path.Combine(root, "profile.json"), state = Path.Combine(root, "game-state.json"); EnforcementPaths.Save(fileProfile, profile);
        Process Start(string exe, params string[] args)
        { var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true }; foreach (string arg in args) info.ArgumentList.Add(arg); return Process.Start(info)!; }

        using var panel = new Form { Text = "鼠标输入检查 · 隔离测试", ClientSize = new(440, 210), AutoScaleMode = AutoScaleMode.None, StartPosition = FormStartPosition.Manual };
        panel.Location = new(Screen.PrimaryScreen!.WorkingArea.Left + 12, Screen.PrimaryScreen.WorkingArea.Top + 12);
        var instructions = new Label { AutoSize = false, Bounds = new(16, 14, 408, 130), Text = "准备启动隔离模拟窗口…", Font = new("Microsoft YaHei UI", 10) };
        var finish = new Button { Text = "完成本次输入检查", Bounds = new(16, 156, 408, 38), Enabled = false };
        panel.Controls.AddRange([instructions, finish]);
        using var cover = new Shield { Passive = true, ShowInTaskbar = true, ResourceLine = "隔离输入测试 · 请点击遮挡中央三次" };
        using var input = new InputBrokerClient(profile);
        using var poll = new System.Windows.Forms.Timer { Interval = 20 };
        using var observer = new System.Windows.Forms.Timer { Interval = 75 };
        using var deadline = new System.Windows.Forms.Timer { Interval = 1000 };
        Process? game = null, broker = null; nint gameWindow = 0, foregroundHook = 0;
        Counts? baseline = null, final = null;
        bool polling = false, transitioning = false, covering = false, coveredReady = false, finished = false, finishing = false, foregroundInvalid = false;
        int exit = 2, protectedPolls = 0, inputFaults = 0; string phase = "starting", error = "";
        long started = Environment.TickCount64, controlForegroundAt = 0, lastForeground = -1;
        var foregrounds = new List<ForegroundEvent>();

        void Stage(string next)
        {
            phase = next;
            var testBounds = Native.BoundsOf(gameWindow) ?? Rectangle.Empty;
            WriteShared(Path.Combine(root, "stage.json"), new { stage = phase, fakeWindow = (long)gameWindow, coverWindow = cover.IsHandleCreated ? (long)cover.Handle : 0,
                controlWindow = (long)panel.Handle, coverBounds = cover.Bounds, cover.Visible, cover.WindowState,
                centerHit = (long)WindowFromPhysicalPoint(new PointNative { X = testBounds.Left + testBounds.Width / 2, Y = testBounds.Top + testBounds.Height / 2 }),
                inputArea = new Rectangle(testBounds.Left + 100, testBounds.Top + Math.Max(100, testBounds.Height / 2 - 55), Math.Max(100, testBounds.Width - 200), 150) });
            WriteShared(Path.Combine(root, "stage"), phase);
        }
        void ObserveForeground()
        {
            if (!coveredReady || finishing) return;
            nint current = Native.GetForegroundWindow();
            if ((long)current != lastForeground) { lastForeground = (long)current; foregrounds.Add(new(Environment.TickCount64, (long)current)); }
            if (current == gameWindow)
            {
                // A visit to the control panel is valid only as the final handoff,
                // never as an interruption followed by more covered clicks.
                if (controlForegroundAt != 0) foregroundInvalid = true;
                controlForegroundAt = 0;
            }
            else if (current == panel.Handle)
            {
                // The final button activates this separate panel on mouse-down,
                // before Click fires. Accept only that short finishing handoff.
                if (controlForegroundAt == 0) controlForegroundAt = Environment.TickCount64;
                else if (Environment.TickCount64 - controlForegroundAt > 1000) foregroundInvalid = true;
            }
            else if (current != 0) foregroundInvalid = true;
        }
        Native.WinEvent foregroundCallback = (_, _, _, _, _, _, _) => ObserveForeground();

        async Task Complete(string reason)
        {
            if (finished || finishing) return;
            ObserveForeground(); finishing = true; observer.Stop(); deadline.Stop();
            await Task.Delay(250); final = ReadShared<Counts>(state);
            bool alive = false;
            try { alive = game is { HasExited: false }; } catch (InvalidOperationException) { }
            bool verified = baseline is { Raw: > 0, Async: > 0, RawRegistered: true } && final != null && coveredReady && protectedPolls > 0 && !foregroundInvalid &&
                Environment.TickCount64 - final.WrittenAt < 1500 && alive && reason == "operator-finished";
            long? legacyDelta = final == null || baseline == null ? null : final.Legacy - baseline.Legacy;
            long? rawDelta = final == null || baseline == null ? null : final.Raw - baseline.Raw;
            long? asyncDelta = final == null || baseline == null ? null : final.Async - baseline.Async;
            bool passed = verified && inputFaults == 0 && legacyDelta == 0 && rawDelta == 0 && asyncDelta == 0;
            exit = passed ? 0 : verified ? 1 : 2;
            WriteShared(Path.Combine(root, "results.json"), new { passed, verified, outcome = passed ? "passed" : verified ? "failed" : "not-verified", reason, error,
                baseline, final, legacyDelta, rawDelta, asyncDelta, coveredReady, protectedPolls, inputFaults, foregroundInvalid, foregrounds,
                note = "Covered-stage clicks are supplied and confirmed by the operator. Automatic activation of the cover invalidates this run; no input is synthesized by the fixture." });
            poll.Stop(); input.Dispose(); cover.Conceal();
            foreach (var process in new[] { game, broker }) { if (process != null) { try { if (!process.HasExited) process.Kill(false); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } } }
            if (foregroundHook != 0) { Native.UnhookWinEvent(foregroundHook); foregroundHook = 0; }
            finished = true; panel.Close();
        }
        async Task StartCoverage()
        {
            transitioning = true;
            try
            {
                // Finish the positive-control click before freezing the baseline.
                long until = Environment.TickCount64 + 2500;
                while (GetAsyncKeyState(1) < 0 && Environment.TickCount64 < until) await Task.Delay(20);
                await Task.Delay(150);
                baseline = ReadShared<Counts>(state);
                if (baseline is not { Raw: > 0, Async: > 0, RawRegistered: true } || GetAsyncKeyState(1) < 0) { baseline = null; return; }
                if (Native.BoundsOf(gameWindow) is not { } bounds) throw new IOException("模拟窗口不可见。");
                Native.SetForegroundWindow(gameWindow); cover.Place(bounds, gameWindow);
                Native.SetWindowDisplayAffinity(cover.Handle, 0); covering = true; Stage("covering");
                instructions.Text = "等待输入保护就绪。不要主动激活遮挡窗口；模拟游戏应始终保持前台。";
            }
            finally { transitioning = false; }
        }
        poll.Tick += async (_, _) =>
        {
            if (polling || finished) return; polling = true;
            try
            {
                ObserveForeground();
                var reply = await input.Poll(covering ? cover.Handle : 0, covering);
                if (finished || finishing) return;
                if (covering && Native.GetForegroundWindow() == gameWindow)
                {
                    if (reply.Focused)
                    {
                        protectedPolls++;
                        if (!coveredReady)
                        {
                            coveredReady = true; ObserveForeground(); Stage("covered"); finish.Enabled = true;
                            instructions.Text = "在遮挡中央同一位置点击至少三次，再点击下方完成按钮。\n不要激活遮挡；若模拟游戏丢失前台，本轮标记未验证。";
                        }
                    }
                    else if (coveredReady) inputFaults++;
                }
            }
            catch (Exception e) { error = e.GetType().Name + ": " + e.Message; if (coveredReady) inputFaults++; }
            finally { polling = false; }
        };
        observer.Tick += async (_, _) =>
        {
            if (finished || finishing || transitioning || covering) return;
            var counts = ReadShared<Counts>(state);
            if (counts is { Raw: > 0, Async: > 0, RawRegistered: true })
            {
                try { await StartCoverage(); } catch (Exception e) { error = e.Message; await Complete("setup-error"); }
            }
        };
        deadline.Tick += async (_, _) =>
        {
            ObserveForeground();
            if (Environment.TickCount64 - started > 180000) await Complete("timeout");
        };
        cover.Activated += (_, _) => { if (coveredReady && !finishing) foregroundInvalid = true; };
        finish.Click += async (_, _) => await Complete("operator-finished");
        panel.FormClosing += async (_, e) => { if (!finished) { e.Cancel = true; await Complete("operator-closed"); } };
        panel.Shown += async (_, _) =>
        {
            try
            {
                broker = Start(enforcer, "--input-profile", fileProfile); game = Start(fake, "--pointer-demo", root);
                long until = Environment.TickCount64 + 15000;
                while (Environment.TickCount64 < until)
                {
                    game.Refresh(); if (game.HasExited) throw new IOException("模拟窗口启动失败。");
                    if (game.MainWindowHandle != 0) { gameWindow = game.MainWindowHandle; break; }
                    await Task.Delay(50);
                }
                if (gameWindow == 0) throw new IOException("模拟窗口未创建。");
                foregroundHook = Native.WatchForeground(foregroundCallback); Native.SetForegroundWindow(gameWindow);
                instructions.Text = "阳性对照：在模拟窗口蓝色「输入测试」区域点击，可短暂按住后松开。\n只有 Raw Input 与异步状态均收到输入，才进入遮挡检查；否则完成后标记未验证。";
                finish.Enabled = true; Stage("baseline"); poll.Start(); observer.Start(); deadline.Start();
            }
            catch (Exception e) { error = e.ToString(); await Complete("setup-error"); }
        };
        try { Application.Run(panel); }
        finally
        {
            input.Dispose(); cover.Conceal();
            if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook);
            foreach (var process in new[] { game, broker }) { if (process != null) { try { if (!process.HasExited) process.Kill(false); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { } process.Dispose(); } }
            GC.KeepAlive(foregroundCallback);
        }
        return exit;
    }
}
#endif
