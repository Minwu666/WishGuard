using System.Diagnostics;
using System.Drawing.Drawing2D;
namespace WishGuard;

public sealed class Shield : Form
{
    public bool CaptureExclusion { get; private set; }
    public string ResourceLine = "正在自动读取资源…";
    public Shield()
    {
        Text = "攒愿 · 祈愿遮挡"; FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false; TopMost = true; DoubleBuffered = true;
        BackColor = Color.FromArgb(13, 23, 37); KeyPreview = true;
        KeyDown += (_, e) => { e.Handled = true; e.SuppressKeyPress = true; };
        KeyPress += (_, e) => e.Handled = true;
    }
    protected override void OnHandleCreated(EventArgs e)
    { base.OnHandleCreated(e); CaptureExclusion = Native.SetWindowDisplayAffinity(Handle, 0x11); }
    protected override void OnPaint(PaintEventArgs e)
    {
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
        TextLine("这一次，先留给未来。", cy - 60 * s, 38, Color.FromArgb(240, 242, 242));
        TextLine("600 抽计划  ·  达标核验后永久解除", cy + 10 * s, 18, Color.FromArgb(174, 194, 209));
        TextLine(ResourceLine, cy + 60 * s, 16, Color.FromArgb(219, 191, 138));
        TextLine("点击游戏右上角 × 返回，正常游玩会自动恢复", cy + 135 * s, 18, Color.FromArgb(174, 194, 209));
    }
    public void Place(Rectangle client)
    {
        if (Bounds != client)
        {
            Bounds = client;
            int holeW = Math.Max(65, (int)(client.Width * .07)), holeH = Math.Max(65, (int)(client.Height * .14));
            var region = new Region(new Rectangle(Point.Empty, client.Size));
            region.Exclude(new Rectangle(client.Width - holeW, 0, holeW, holeH));
            var old = Region; Region = region; old?.Dispose();
        }
        if (!Visible) Show(); Native.Raise(Handle); Native.SetForegroundWindow(Handle);
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
    readonly System.Windows.Forms.Timer timer = new() { Interval = 180 }, focus = new() { Interval = 70 };
    readonly Stopwatch uptime = Stopwatch.StartNew();
    readonly long startWall = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    long lastSave, nextResourceRead; int epoch;
    bool busy, disposed; nint latched; int misses;
    public nint DemoHandle { get; set; }
    public bool IgnoreRealGame { get; set; }
    public bool Running { get; private set; }
    public bool Ready { get; private set; }
    public string Phase { get; private set; } = "尚未启用";
    public string Status { get; private set; } = "启用后，只在原神运行时守护。";
    public string ResourceStatus { get; private set; } = "打开使用纠缠之缘的祈愿页面后自动读取";
    public double LastMilliseconds { get; private set; }
    public event Action? Changed;
    public long Now => Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), startWall + (long)uptime.Elapsed.TotalSeconds);
    public GuardController(StateStore store, string publicKey)
    {
        this.store = store; this.publicKey = publicKey; timer.Tick += async (_, _) => await Tick();
        focus.Tick += (_, _) =>
        {
            if (disposed || latched == 0) return;
            nint fg = Native.GetForegroundWindow();
            if (fg == latched && Native.BoundsOf(latched) is { } b) shield.Place(b);
            else if (fg != shield.Handle) shield.Conceal();
        };
        focus.Start();
    }
    public async Task Start()
    {
        if (Running || Rules.Ended(store.State, publicKey)) return;
        Phase = "正在准备"; Status = "启动本地识别引擎…"; Changed?.Invoke();
        int generation = ++epoch; var worker = new OcrWorker(); ocr?.Dispose(); ocr = worker;
        try
        {
            await worker.Start();
            if (disposed || generation != epoch) return;
            if (!worker.Language.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase)) throw new IOException("请先安装 Windows 简体中文 OCR 组件。");
            Running = Ready = true; timer.Start(); Phase = "守护中"; Status = "等待原神前台画面。"; Changed?.Invoke();
        }
        catch { worker.Dispose(); if (generation == epoch) { Ready = Running = false; Phase = "保护不可用"; Status = "本地识别启动失败，请在设置中重试。"; Changed?.Invoke(); } throw; }
    }
    public void Stop(string reason)
    {
        epoch++; timer.Stop(); Running = Ready = false; latched = 0; misses = 0;
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
            if (game == null) { shield.Conceal(); latched = 0; consensus.Reset(); Phase = "等待游戏画面"; Status = "原神窗口未显示，暂不截图。"; return; }
            if (fg != game.Handle && fg != shield.Handle) { shield.Conceal(); Phase = "守护待命"; Status = "已切换到其他应用，暂不截图。"; return; }
            if (latched != 0 && latched != game.Handle) { latched = 0; misses = 0; consensus.Reset(); }
            if (latched == game.Handle) shield.Place(game.Bounds);
            var watch = Stopwatch.StartNew(); long observedAt = Now;
            using var frame = Native.CaptureFrame(game.Bounds);
            using var footer = ResourceReader.Crop(frame, Rectangle.FromLTRB(0, (int)(frame.Height * .68), frame.Width, frame.Height));
            if (!Native.HasContent(footer)) { Phase = "画面不可读"; Status = "截图近黑，无法可靠识别。请检查窗口化或无边框模式。"; misses = 0; return; }
            string text = await worker.Read(footer);
            if (disposed || generation != epoch) return;
            LastMilliseconds = watch.Elapsed.TotalMilliseconds;
            fg = Native.GetForegroundWindow(); var current = Native.GetGame(DemoHandle);
            if (current?.Handle != game.Handle || current.Bounds != game.Bounds || (fg != game.Handle && fg != shield.Handle)) { shield.Conceal(); return; }
            if (WishText.IsWish(text))
            {
                if (latched != game.Handle) { store.Log("blocked", "识别到祈愿按钮"); nextResourceRead = 0; }
                latched = game.Handle; misses = 0; shield.Place(game.Bounds); Phase = "祈愿已遮挡";
                Status = !shield.CaptureExclusion ? "捕获排除不可用，自动恢复可能失效。" : Native.GetForegroundWindow() != shield.Handle ?
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
            Running = Ready = false; timer.Stop(); Phase = "保护不可用"; Status = "识别故障：" + e.Message;
            store.Log("fault", e.GetType().Name);
        }
        finally { busy = false; if (!disposed) Changed?.Invoke(); }
    }
    public void Dispose()
    { disposed = true; epoch++; timer.Dispose(); focus.Dispose(); ocr?.Dispose(); shield.Conceal(); shield.Dispose(); }
}
