using System.Diagnostics;
using System.Drawing.Drawing2D;
namespace WishGuard;

public sealed class Shield : Form
{
    public bool CaptureExclusion { get; private set; }
    public string ResourceLine = "正在自动读取资源…";
    public bool Verifying;
    internal bool Passive;
    protected override bool ShowWithoutActivation => Passive;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; if (Passive) p.ExStyle |= 0x08000000; return p; }
    }
    protected override void WndProc(ref Message message)
    {
        if (Passive && message.Msg == 0x21) { message.Result = 3; return; }
        base.WndProc(ref message);
    }
    public Shield()
    {
        Text = "攒愿 · 祈愿遮挡"; FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        BackColor = Color.FromArgb(13, 23, 37); KeyPreview = true;
        KeyDown += (_, e) => { e.Handled = true; e.SuppressKeyPress = true; };
        KeyPress += (_, e) => e.Handled = true;
    }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); CaptureExclusion = Native.SetWindowDisplayAffinity(Handle, 0x11); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float s = Math.Min(Width / 1200f, Height / 750f); float cx = Width / 2f, cy = Height / 2f;
        using var glow = new LinearGradientBrush(ClientRectangle, Color.FromArgb(20, 38, 55), BackColor, 60);
        g.FillRectangle(glow, ClientRectangle);
        using var gold = new Pen(Color.FromArgb(219, 191, 138), 2 * s);
        g.DrawEllipse(gold, cx - 52 * s, cy - 195 * s, 104 * s, 104 * s);
        var star = new[] { new PointF(cx,cy-182*s),new PointF(cx+10*s,cy-153*s),new PointF(cx+37*s,cy-143*s),new PointF(cx+10*s,cy-133*s),new PointF(cx,cy-104*s),new PointF(cx-10*s,cy-133*s),new PointF(cx-37*s,cy-143*s),new PointF(cx-10*s,cy-153*s) };
        using var ink = new SolidBrush(Color.FromArgb(219, 191, 138)); g.FillPolygon(ink, star);
        void TextLine(string text, float y, float size, Color color)
        {
            using var font = new Font("Microsoft YaHei UI", size * s, FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color);
            g.DrawString(text, font, brush, new RectangleF(20, y, Width - 40, 65 * s), new StringFormat { Alignment = StringAlignment.Center });
        }
        TextLine(Verifying ? "正在确认游戏画面…" : "这一次，先留给未来。", cy - 60 * s, 38, Color.FromArgb(240, 242, 242));
        TextLine("600 抽计划  ·  达标核验后永久解除", cy + 10 * s, 18, Color.FromArgb(174, 194, 209));
        TextLine(Verifying ? "确认完成后会自动恢复，请稍候" : ResourceLine, cy + 60 * s, 16, Color.FromArgb(219, 191, 138));
        TextLine(Passive ? "按 Esc 或点击游戏右上角 × 返回" : "点击游戏右上角 × 返回，正常游玩会自动恢复", cy + 135 * s, 18, Color.FromArgb(174, 194, 209));
    }
    public void Place(Rectangle client, nint game = 0)
    {
        if (client.Width <= 0 || client.Height <= 0) return;
        if (Bounds != client)
        {
            Bounds = client;
            var region = new Region(new Rectangle(Point.Empty, client.Size));
            region.Exclude(ReturnArea.Of(new Rectangle(Point.Empty, client.Size)));
            var old = Region; Region = region; old?.Dispose();
        }
        if (!Visible) Show(); Native.Raise(Handle); if (!Passive) Native.FocusCover(Handle, game);
    }
    public void Conceal() => Hide();
    protected override void OnFormClosing(FormClosingEventArgs e)
    { if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true; base.OnFormClosing(e); }
}

public sealed class GuardController : IDisposable
{
    readonly StateStore store; readonly string publicKey;
    OcrWorker? ocr;
    readonly Shield shield = new();
    readonly ResourceConsensus consensus = new();
    readonly System.Windows.Forms.Timer timer = new() { Interval = 75 }, focus = new() { Interval = 70 };
    readonly Stopwatch uptime = Stopwatch.StartNew();
    readonly long startWall = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    long lastSave, nextResourceRead; int epoch;
    bool busy, disposed, armed, failed, returningToGame, gameplaySeen; nint latched, lastForeground, foregroundHook; int misses;
    bool shortcutWasDown;
    readonly ScreenGate gate = new();
    readonly Native.WinEvent foregroundCallback;
    readonly WishEntryShortcut entryShortcut;
    InputBrokerClient? inputBroker;
    bool inputBusy;
    long entrySerial, returnSerial;
    bool inputCountersSeen;
    long lastRecognition;
    long entryBlockedUntil;
    long startedAt;
    bool CoverRequired => gate.Blocked && (gameplaySeen || gate.WishSeen || uptime.ElapsedMilliseconds < entryBlockedUntil);
    public bool Strong { get; set; }
    public void UseInputBroker(EnforcementProfile profile) { shield.Passive = true; inputBroker = new(profile); }
#if WISHGUARD_TESTS
    internal bool CoverVisibleForTest => shield.Visible;
    internal bool WishSeenForTest => gate.WishSeen;
    internal string DebugForTest => $"gate={gate.Blocked},version={gate.Version},exclude={shield.CaptureExclusion},fg={Native.GetForegroundWindow()},game={DemoHandle},text={testText}";
    string testText = "";
    internal string? CaptureForTest;
    internal Func<bool>? ShortcutForTest;
    internal bool ShortcutRegisteredForTest => entryShortcut.Registered;
#endif
    bool protectionAvailable = true;
    public void ProtectionAvailable(bool available)
    {
        if (protectionAvailable == available) return;
        protectionAvailable = available;
        if (!available) { gate.Recheck(); UpdateFocus(); }
    }
    public nint DemoHandle { get; set; }
    public bool IgnoreRealGame { get; set; }
    public bool Running { get; private set; }
    public bool Ready { get; private set; }
    public string Phase { get; private set; } = "尚未启用";
    public string Status { get; private set; } = "启用后，只在原神运行时守护。";
    public string ResourceStatus { get; private set; } = "打开使用纠缠之缘的祈愿页面后自动读取";
    public double LastMilliseconds { get; private set; }
    public event Action? Changed;
    public bool EnforcementHealthy
        => EnforcementStatus == "ready";
    public Task<string> ConfirmEnforcementStatus()
        => ProtectionReadiness.Confirm(() => EnforcementStatus, UpdateFocus);
    public string EnforcementDiagnostic => $"running={Running};gameplay={gameplaySeen};frameAgeMs={uptime.ElapsedMilliseconds-lastRecognition};surface={Native.GetGame(DemoHandle)!=null};cover={shield.Visible}";
    public string EnforcementStatus
    {
        get
        {
            if (disposed || !armed) return "inactive";
            if (failed) return "recognition-fault";
            if (!protectionAvailable) return "connection-unavailable";
            if (inputBroker != null && !inputBroker.Connected) return "input-unavailable";
            if (foregroundHook == 0) return "window-monitor-unavailable";
            nint fg = Native.GetForegroundWindow();
            bool game = fg == DemoHandle && DemoHandle != 0 || Native.ProcessName(fg) is "YuanShen" or "GenshinImpact";
            if (!game && fg != shield.Handle) return "ready";
            // A foreground notification can arrive before a game window is shown,
            // or while it is being minimized. There is no interactive client yet.
            if (game && Native.BoundsOf(fg) is not { Width: >= 640, Height: >= 360 }) return "ready";
            if (CoverRequired)
            {
                if (!shield.Visible) return "cover-unavailable";
                if (!shield.CaptureExclusion) return "capture-unavailable";
                if (inputBroker != null) return inputBroker.KeysProtected ? "ready" : "input-pending";
                return fg == shield.Handle ? "ready" : "focus-unavailable";
            }
            // Splash/loading windows may not yet expose a capturable game surface.
            // OCR has its own bounded timeout; enforce frame freshness once play begins.
            return (Running && (!gameplaySeen || uptime.ElapsedMilliseconds - lastRecognition < 2500) || !gameplaySeen && uptime.ElapsedMilliseconds - startedAt < 2500) ? "ready" : "recognition-stale";
        }
    }
    public long Now => Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), startWall + (long)uptime.Elapsed.TotalSeconds);
    public GuardController(StateStore store, string publicKey)
    {
        this.store = store; this.publicKey = publicKey; timer.Tick += async (_, _) => await Tick();
        entryShortcut = new(OnWishEntry);
        foregroundCallback = (_, _, _, _, _, _, _) => UpdateFocus();
        foregroundHook = Native.WatchForeground(foregroundCallback);
        focus.Tick += (_, _) => { UpdateFocus(); PollInput(); };
        focus.Start();
    }
    async void PollInput()
    {
        if (disposed || inputBusy || inputBroker == null) return;
        inputBusy = true;
        try
        {
            var reply = await inputBroker.Poll(shield.Handle, Strong && armed && CoverRequired && shield.Visible);
            if (disposed) return;
            if (!inputCountersSeen) { entrySerial = reply.Entry; returnSerial = reply.Return; inputCountersSeen = true; return; }
            if (reply.Entry != entrySerial) { entrySerial = reply.Entry; OnWishEntry(); }
            if (reply.Return != returnSerial)
            {
                returnSerial = reply.Return;
                if (Strong && armed && CoverRequired) { entryBlockedUntil = 0; gate.CancelHold(); UpdateFocus(); }
            }
        }
        catch (Exception) { }
        finally { inputBusy = false; }
    }
    void UpdateFocus()
    {
        if (disposed || returningToGame) return;
        nint fg = Native.GetForegroundWindow();
        // A zero foreground HWND is a handoff, not an application switch.
        if (fg == 0) return;
        if (Strong && armed)
        {
            bool target = DemoHandle != 0 ? fg == DemoHandle : !IgnoreRealGame && Native.ProcessName(fg) is "YuanShen" or "GenshinImpact";
            entryShortcut.Enable(inputBroker == null && target && Running && protectionAvailable);
            if (target && Native.BoundsOf(fg) is { Width: >= 640, Height: >= 360 } bounds)
            {
                if (lastForeground != fg && lastForeground != shield.Handle) gate.Recheck();
                bool shortcutDown = Native.WishShortcutDown();
#if WISHGUARD_TESTS
                if (ShortcutForTest != null) shortcutDown = ShortcutForTest();
#endif
                if (shortcutDown && !shortcutWasDown) HoldWishEntry();
                shortcutWasDown = shortcutDown;
                if (CoverRequired) { shield.Verifying = !gate.WishSeen && uptime.ElapsedMilliseconds >= entryBlockedUntil; shield.Place(bounds, fg); }
            }
            else if (fg != shield.Handle)
            {
                if (lastForeground != fg) gate.Recheck();
                shield.Conceal();
            }
            lastForeground = fg;
        }
        else if (latched != 0)
        {
            if (fg == latched && Native.BoundsOf(latched) is { } b) shield.Place(b, latched);
            else if (fg != shield.Handle) shield.Conceal();
        }
    }
    void HoldWishEntry()
    {
        entryBlockedUntil = uptime.ElapsedMilliseconds + 5000;
        gate.HoldUntil(entryBlockedUntil);
        store.Log("entry_blocked", "祈愿入口已拦下");
    }
    void OnWishEntry()
    {
        if (disposed || !Strong || !armed || !Running) return;
        nint fg = Native.GetForegroundWindow();
        bool game = DemoHandle != 0 ? fg == DemoHandle : !IgnoreRealGame && Native.ProcessName(fg) is "YuanShen" or "GenshinImpact";
        if (!game || Native.BoundsOf(fg) == null) return;
        HoldWishEntry(); UpdateFocus();
    }
    void RestoreGame(nint game)
    {
        returningToGame = true;
        try { lastForeground = game; shield.Conceal(); Native.SetForegroundWindow(game); }
        finally { returningToGame = false; }
    }
    public async Task Start()
    {
        if (Running || Rules.Ended(store.State, publicKey)) return;
        armed = true; failed = false; gameplaySeen = false; startedAt = lastRecognition = uptime.ElapsedMilliseconds;
        shortcutWasDown = false; entryBlockedUntil = 0; focus.Interval = Strong ? 20 : 70; gate.Reset(); UpdateFocus();
        Phase = "正在准备"; Status = "启动本地识别引擎…"; Changed?.Invoke();
        int generation = ++epoch; var worker = new OcrWorker(); ocr?.Dispose(); ocr = worker;
        try
        {
            await worker.Start();
            if (disposed || generation != epoch) return;
            if (!worker.Language.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)) throw new IOException("请先安装 Windows 简体中文 OCR 组件。");
            Running = Ready = true; timer.Start(); Phase = "守护中"; Status = "等待原神前台画面。"; Changed?.Invoke();
        }
        catch (Exception e) { worker.Dispose(); if (generation == epoch) { store.Log("recognition_start_error", e.GetType().Name + ": " + e.Message); failed = true; Ready = Running = false; Phase = "保护不可用"; Status = "本地识别启动失败，请在设置中重试。"; Changed?.Invoke(); } throw; }
    }
    public void Stop(string reason)
    {
        epoch++; timer.Stop(); Running = Ready = armed = false; latched = 0; misses = 0; gate.Reset();
        entryShortcut.Enable(false);
        shield.Conceal(); ocr?.Dispose(); ocr = null; consensus.Reset(); Phase = "等待原神"; Status = reason; Changed?.Invoke();
    }
    public void Import(string token)
    {
        if (store.Fault || Rules.ClockRolledBack(store.State, Now)) throw new InvalidDataException("记录异常或系统时间回退，暂不能解除。");
        var g = Rules.Validate(token, publicKey, store.State, Now, true);
        string? previous = store.State.PermanentPass; store.State.PermanentPass = token; store.State.UsedPasses.Add(g.Id);
        try { store.Save(); } catch { store.State.PermanentPass = previous; store.State.UsedPasses.Remove(g.Id); throw; }
        Stop("600 抽目标已核验，守护已永久解除。"); Phase = "已永久解除";
        store.Log("permanent_unlock", "600 抽永久凭证已接受"); Changed?.Invoke();
    }
    async Task Tick()
    {
        if (busy || disposed || !Running || ocr == null) return;
        busy = true; int generation = epoch; var worker = ocr;
        try
        {
            if (Rules.Ended(store.State, publicKey)) { Stop("计划已经结束。"); return; }
            if (!store.Fault && Now - lastSave >= 30)
            { store.State.LastSeen = Math.Max(store.State.LastSeen, Now); store.Save(); lastSave = Now; }
            var game = IgnoreRealGame ? null : Native.GetGame(DemoHandle); nint fg = Native.GetForegroundWindow();
            if (game == null) { shield.Conceal(); consensus.Reset(); Phase = "等待游戏画面"; Status = "原神窗口未显示，暂不截图。"; return; }
            if (fg != game.Handle && fg != shield.Handle) { shield.Conceal(); Phase = "守护待命"; Status = "已切换到其他应用，暂不截图。"; return; }
            if (latched != 0 && latched != game.Handle) { latched = 0; misses = 0; consensus.Reset(); }
            if (Strong) UpdateFocus();
            if (latched == game.Handle || Strong && CoverRequired) shield.Place(game.Bounds, game.Handle);
            int viewVersion = gate.Version;
            var watch = Stopwatch.StartNew(); long observedAt = Now;
            using var frame = Native.CaptureFrame(game.Bounds);
            using var footer = ResourceReader.Crop(frame, Rectangle.FromLTRB(0, (int)(frame.Height * .68), frame.Width, frame.Height));
#if WISHGUARD_TESTS
            if (CaptureForTest != null && !File.Exists(CaptureForTest)) footer.Save(CaptureForTest);
#endif
            if (!Native.HasContent(footer))
            {
                if (Strong && !gameplaySeen) { lastRecognition = uptime.ElapsedMilliseconds; Phase = "等待进入游戏"; Status = "启动画面加载中。"; return; }
                if (Strong) { gate.Recheck(); UpdateFocus(); } Phase = "画面不可读"; Status = "截图近黑，无法可靠识别。请检查窗口化或无边框模式。"; misses = 0; return;
            }
            string text = await worker.Read(footer);
            if (Strong && string.IsNullOrWhiteSpace(text))
            {
                // Small light HUD text can be missed against a low-contrast background.
                using var enlarged = ResourceReader.Crop(footer, new Rectangle(Point.Empty, footer.Size), Math.Min(2, 2400d / footer.Width));
                using var prepared = ResourceReader.LightText(enlarged);
                text = await worker.Read(prepared);
            }
#if WISHGUARD_TESTS
            testText = text;
#endif
            if (disposed || generation != epoch) return;
            LastMilliseconds = watch.Elapsed.TotalMilliseconds;
            if (Strong && viewVersion != gate.Version) return;
            lastRecognition = uptime.ElapsedMilliseconds;
            fg = Native.GetForegroundWindow(); var current = Native.GetGame(DemoHandle);
            if (current?.Handle != game.Handle || current.Bounds != game.Bounds || (fg != game.Handle && fg != shield.Handle)) { shield.Conceal(); return; }
            bool wish = WishText.IsWish(text);
            if (Strong && !gameplaySeen && (wish || SceneText.HasGameHud(text)))
            { gameplaySeen = true; store.Log("gameplay_ready", "已进入游戏画面"); }
            if (Strong) gate.Observe(viewVersion, protectionAvailable && !string.IsNullOrWhiteSpace(text), wish, uptime.ElapsedMilliseconds);
            if (wish)
            {
                if (latched != game.Handle) { store.Log("blocked", "识别到祈愿按钮"); nextResourceRead = 0; }
                latched = game.Handle; misses = 0; shield.Verifying = false; shield.Place(game.Bounds, game.Handle); Phase = "祈愿已遮挡";
                Status = !shield.CaptureExclusion ? "捕获排除不可用，自动恢复可能失效。" : shield.Passive ? "按 Esc 或点击游戏右上角 × 返回，正常游玩会自动恢复。" : Native.GetForegroundWindow() != shield.Handle ?
                    "遮挡未取得焦点，键盘可能仍传入游戏；请点击遮挡。" : "使用右上角游戏原有的 × 返回，正常游玩会自动恢复。";
                Changed?.Invoke();
                // The screenshot was taken BEFORE the shield. Resource OCR cannot delay blocking.
                if (uptime.ElapsedMilliseconds >= nextResourceRead)
                {
                    nextResourceRead = uptime.ElapsedMilliseconds + 1600;
                    var result = await ResourceReader.Read(frame, worker);
                    if (disposed || generation != epoch) return;
                    ResourceStatus = result.Status;
                    var snapshot = consensus.Observe(result, observedAt);
                    if (snapshot != null && !store.Fault)
                    {
                        bool changed = store.State.Resources?.Primogems != snapshot.Primogems || store.State.Resources?.Intertwined != snapshot.Intertwined;
                        store.State.Resources = snapshot;
                        if (changed || Now - lastSave >= 10) { store.Save(); lastSave = Now; }
                        ResourceStatus = "已连续两次确认资源数量";
                        shield.ResourceLine = $"当前 {snapshot.Pulls:N0} / 600 抽  ·  原石 {snapshot.Primogems:N0}  ·  纠缠之缘 {snapshot.Intertwined:N0}";
                        shield.Invalidate();
                    }
                    else if (result.Valid) ResourceStatus = "读到资源，等待第二次确认…";
                }
            }
            else if (Strong)
            {
                if (!gameplaySeen && !CoverRequired)
                {
                    if (shield.Visible) RestoreGame(game.Handle);
                    Phase = "等待进入游戏"; Status = "启动画面加载中，祈愿识别继续运行。"; return;
                }
                if (!gate.Blocked && shield.CaptureExclusion)
                {
                    latched = 0; misses = 0; RestoreGame(game.Handle);
                    Phase = "守护中"; Status = "正常游玩中。";
                }
                else { shield.Verifying = !gate.WishSeen && uptime.ElapsedMilliseconds >= entryBlockedUntil; shield.Place(game.Bounds, game.Handle); Phase = "确认游戏画面"; Status = "请稍候，确认完成后自动恢复。"; }
            }
            else if (latched == game.Handle)
            {
                if (shield.CaptureExclusion && ++misses >= 3)
                { latched = 0; misses = 0; consensus.Reset(); shield.Conceal(); Phase = "守护中"; Status = "已返回普通游戏画面。"; }
            }
            else { Phase = "守护中"; Status = "正常游玩中，正在留意祈愿画面。"; }
        }
        catch (Exception e)
        {
            if (disposed || generation != epoch) return;
            failed = true; gate.Recheck(); UpdateFocus(); Running = Ready = false; timer.Stop(); Phase = "保护不可用"; Status = "识别故障：" + e.Message;
            store.Log("fault", e.GetType().Name);
        }
        finally { busy = false; if (!disposed) Changed?.Invoke(); }
    }
    public void Dispose()
    { disposed = true; epoch++; if (foregroundHook != 0) Native.UnhookWinEvent(foregroundHook); inputBroker?.Dispose(); entryShortcut.Dispose(); timer.Dispose(); focus.Dispose(); ocr?.Dispose(); shield.Conceal(); shield.Dispose(); }
}
