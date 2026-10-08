using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;
namespace WishGuard;

public sealed record RuntimeOptions(bool Demo, bool Isolated, bool Session, bool NoWatch, bool AutoStart, string? TestProcess);
public sealed class MainForm : Form
{
    readonly StateStore store; readonly string publicKey; readonly RuntimeOptions options;
    readonly GuardController guard; readonly ScenePanel scene = new();
    readonly List<(Control Control, Rectangle Bounds, int Tab)> items = new();
    readonly Button home, settings, help, primary, import, askRecovery, cancelRecovery, finishRecovery, retry;
    readonly CheckBox bindGame = new();
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer pulse = new() { Interval = 1000 };
    int tab; bool canExit, starting, attempted, seenGame, lifecycleBusy, released; long lostAt;
    string? message; DemoGame? demoGame;
    readonly Dictionary<int, Font> controlFonts = new();
    bool layoutInProgress;
    float ScaleFactor => Math.Min(scene.Width / 1060f, scene.Height / 760f);
    bool Ended => Rules.Ended(store.State, publicKey);
    public MainForm(StateStore store, string publicKey, RuntimeOptions options)
    {
        this.store = store; this.publicKey = publicKey; this.options = options;
        guard = new(store, publicKey) { IgnoreRealGame = options.Isolated && options.TestProcess != null }; Text = options.Demo ? "攒愿 · 模拟演示" : "攒愿 · 600 抽计划";
        BackColor = Color.FromArgb(14, 23, 36); Font = new Font("Microsoft YaHei UI", 10);
        var area = Screen.PrimaryScreen!.WorkingArea;
        ClientSize = new Size(Math.Min(1440, area.Width - 100), Math.Min(1040, area.Height - 100));
        MinimumSize = new Size(960, 700); StartPosition = FormStartPosition.CenterScreen;
        scene.Dock = DockStyle.Fill; scene.Render = PaintScene; Controls.Add(scene);
        home = AddButton("攒抽进度", new(22, 146, 158, 44), -1, () => Switch(0));
        settings = AddButton("守护设置", new(22, 204, 158, 44), -1, () => Switch(1));
        help = AddButton("识别与帮助", new(22, 262, 158, 44), -1, () => Switch(2));
        primary = AddButton("开启 600 抽守护", new(252, 595, 245, 46), 0, async () => { if (!store.State.Committed) await Enroll(); else ExportRequest(); }, true);
        import = AddButton("导入永久解除凭证", new(518, 595, 239, 46), 0, ImportPass);
        askRecovery = AddButton("申请 24 小时冷静期", new(252, 420, 235, 44), 1, RequestRecovery);
        cancelRecovery = AddButton("撤销申请，继续守护", new(510, 420, 254, 44), 1, CancelRecovery);
        finishRecovery = AddButton("冷静期已结束，停止守护", new(252, 481, 285, 44), 1, FinishRecovery, true);
        retry = AddButton("重新启动识别", new(252, 538, 220, 44), 2, async () => { attempted = false; await CheckGame(); });
        AddButton("打开使用说明", new(496, 538, 210, 44), 2, () => Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "使用说明.md")) { UseShellExecute = true }));
        if (options.Demo) AddButton("模拟祈愿窗口", new(252, 709, 220, 38), 0, OpenDemo);
        bindGame.Text = "跟随原神启动与退出"; bindGame.Checked = store.State.StartWithWindows; bindGame.ForeColor = ScenePanel.White;
        bindGame.BackColor = ScenePanel.Card; scene.Controls.Add(bindGame); items.Add((bindGame, new(252, 194, 450, 38), 1));
        var menu = new ContextMenuStrip(); menu.Items.Add("打开攒愿", null, (_, _) => ShowMain());
        if (options.Demo) menu.Items.Add("退出演示", null, (_, _) => { canExit = true; Close(); });
        tray = new NotifyIcon { Icon = SystemIcons.Shield, Text = "攒愿 · 600 抽计划", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => ShowMain(); scene.Resize += (_, _) => LayoutControls();
        guard.Changed += RefreshScene; pulse.Tick += async (_, _) => { await CheckGame(); RefreshScene(); }; pulse.Start();
        Shown += async (_, _) =>
        {
            if (options.Demo) OpenDemo();
            if (store.Migrated) message = "旧计划已升级为 600 抽永久解除，旧的限时凭证已作废。";
            if (options.AutoStart && !store.State.Committed && !Ended) await Enroll();
            else
            {
                if (store.State.Committed && !Ended) EnsureWatcher();
                if (Ended) DisableWatcher();
                await CheckGame();
            }
            RefreshScene();
            if (options.Session && !Ended) Hide();
        };
        LayoutControls(); RefreshScene();
        if (options.Session) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }
    }
    Button AddButton(string text, Rectangle bounds, int view, Action action, bool accent = false)
    {
        var b = new Button { Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
            BackColor = accent ? ScenePanel.Gold : ScenePanel.Card, ForeColor = accent ? ScenePanel.Navy : ScenePanel.White,
            AccessibleName = text, TabStop = true };
        b.FlatAppearance.BorderSize = 0; b.Click += (_, _) => action(); scene.Controls.Add(b); items.Add((b, bounds, view)); return b;
    }
    void LayoutControls()
    {
        if (scene.Width <= 0 || scene.Height <= 0 || IsDisposed || released || layoutInProgress) return;
        float s = ScaleFactor;
        int fontKey = Math.Max(1, (int)Math.Round(14 * s * 2));
        if (!controlFonts.TryGetValue(fontKey, out var font))
        {
            font = new Font("Microsoft YaHei UI", fontKey / 2f, FontStyle.Regular, GraphicsUnit.Pixel);
            controlFonts.Add(fontKey, font);
        }
        // WinForms can retain an equal-valued Font rather than our new instance.
        // Keep every assigned font alive until all child controls are disposed.
        layoutInProgress = true; scene.SuspendLayout();
        try
        {
            foreach (var (c, r, view) in items)
            {
                c.SetBounds((int)(r.X * s), (int)(r.Y * s), (int)(r.Width * s), (int)(r.Height * s));
                c.Font = font; c.Visible = view == -1 || view == tab;
            }
        }
        finally { scene.ResumeLayout(false); layoutInProgress = false; }
        scene.Invalidate();
    }
    void Switch(int view) { tab = view; LayoutControls(); RefreshScene(); }
    internal void VerifyLayoutFontLifetime()
    {
        using var bitmap = new Bitmap(32, 32); using var graphics = Graphics.FromImage(bitmap);
        for (int i = 0; i < 90; i++)
        {
            // Repeating the same size matters: WinForms can keep equal-valued fonts.
            if (i % 6 == 0) ClientSize = i % 12 == 0 ? new Size(1200, 860) : new Size(1440, 1040);
            Switch(i % 3);
            foreach (var (control, _, _) in items)
            {
                if (control.Font.GetHeight(graphics) <= 0 || control.Font.SizeInPoints <= 0)
                    throw new InvalidOperationException("Invalid font after repeated layout.");
            }
        }
    }
    void ShowMain() { ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
    void OpenDemo()
    {
        if (!options.Demo) return;
        if (demoGame == null || demoGame.IsDisposed) { demoGame = new DemoGame(); demoGame.Show(); guard.DemoHandle = demoGame.Handle; }
        else { demoGame.Show(); demoGame.Activate(); }
    }
    void EnsureWatcher()
    {
        if (options.NoWatch || options.Demo || !store.State.StartWithWindows || Ended) return;
        try { Lifecycle.Enable(store.DirectoryPath, options.Isolated, options.TestProcess); }
        catch (Exception e) { message = "自动启动配置失败：" + e.Message; }
    }
    void DisableWatcher()
    {
        try { Lifecycle.Disable(store.DirectoryPath, options.Isolated || options.Demo); }
        catch (Exception e) { message = "计划已结束，启动项清理失败；启动器将依据结束记录自行退出。"; store.Log("startup_cleanup_error", e.GetType().Name); }
    }
    async Task Enroll()
    {
        if (starting || Ended || store.Fault) return;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) { message = "需要 Windows 10 2004 或更新版本。"; RefreshScene(); return; }
        starting = true;
        try
        {
            store.State.Committed = true; store.State.StartWithWindows = bindGame.Checked;
            store.State.LastSeen = Math.Max(store.State.LastSeen, guard.Now); store.Save(); EnsureWatcher();
            attempted = false; await CheckGame();
        }
        catch (Exception e) { message = e.Message; }
        finally { starting = false; RefreshScene(); }
    }
    async Task CheckGame()
    {
        if (lifecycleBusy || IsDisposed || !store.State.Committed || Ended) return;
        lifecycleBusy = true;
        try
        {
            bool game = options.Demo ? demoGame is { IsDisposed: false } : Native.IsGameRunning(options.TestProcess);
            if (game)
            {
                seenGame = true; lostAt = 0;
                if (!attempted)
                {
                    attempted = true;
                    try { await guard.Start(); } catch (Exception e) { message = e.Message; }
                }
            }
            else
            {
                if (guard.Running) guard.Stop("原神已退出，本地识别已经停止。");
                attempted = false;
                if (seenGame && !options.Demo)
                {
                    if (lostAt == 0) lostAt = guard.Now;
                    if (guard.Now - lostAt >= 6)
                    {
                        canExit = true; ReleaseResources(); Dispose();
                        store.Log("session_closed", "原神退出，主界面和 OCR 已关闭"); Application.ExitThread();
                    }
                }
            }
        }
        finally { lifecycleBusy = false; }
    }
    void RefreshScene()
    {
        if (IsDisposed) return;
        primary.Text = Ended ? "计划已结束" : !store.State.Committed ? "开启 600 抽守护" : "导出 600 抽核验申请";
        primary.AccessibleName = primary.Text;
        primary.Enabled = !Ended && !store.Fault && !starting && (!store.State.Committed || store.State.Resources?.Pulls >= 600);
        import.Enabled = store.State.Committed && !Ended && !store.Fault;
        bindGame.Enabled = !store.State.Committed && !Ended;
        askRecovery.Enabled = store.State.Committed && !Ended && !store.Fault && store.State.RecoveryAt == null;
        cancelRecovery.Enabled = store.State.Committed && !Ended && !store.Fault && store.State.RecoveryAt != null;
        finishRecovery.Enabled = !Ended && !store.Fault && Rules.CanFinishRecovery(store.State, guard.Now);
        retry.Enabled = !Ended && store.State.Committed && !guard.Running;
        home.BackColor = tab == 0 ? ScenePanel.Card : ScenePanel.Navy;
        settings.BackColor = tab == 1 ? ScenePanel.Card : ScenePanel.Navy;
        help.BackColor = tab == 2 ? ScenePanel.Card : ScenePanel.Navy;
        scene.AccessibleName = $"攒愿，600 抽计划，{store.State.Resources?.Pulls.ToString() ?? "资源尚未识别"}，{guard.Phase}";
        tray.Text = Ended ? "攒愿 · 计划已结束" : "攒愿 · " + guard.Phase;
        scene.Invalidate();
    }
    void ExportRequest()
    {
        if (store.State.Resources is not { } r || r.Pulls < 600 || Ended || store.Fault) return;
        using var dialog = new SaveFileDialog { Filter = "核验申请 (*.json)|*.json", FileName = "攒愿-600抽永久解除申请.json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var s = store.State;
            var request = new ApprovalRequest("wishguard-v2", s.InstallId, s.PolicyId, s.Nonce, 600, "permanent", r.Primogems, r.Intertwined,
                r.Pulls, r.ObservedAt, guard.Now, "本地 OCR 读数，尚未由签发者核验。请同时提供当前资源截图，可遮住 UID。");
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(request, new JsonSerializerOptions { WriteIndented = true }));
            message = "申请已导出。将申请和当前资源截图交给你配置的可信核验人，核验后导入永久解除凭证。"; RefreshScene();
        }
        catch (Exception e) { message = e.Message; RefreshScene(); }
    }
    void ImportPass()
    {
        using var dialog = new OpenFileDialog { Filter = "永久解除凭证 (*.wishpass)|*.wishpass" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 16000) throw new InvalidDataException("文件不是有效凭证。");
            guard.Import(File.ReadAllText(dialog.FileName)); DisableWatcher();
            message = "600 抽计划完成。守护已永久解除，之后打开原神也不会再启动。";
        }
        catch (Exception e) { message = "未解除：" + e.Message; }
        RefreshScene();
    }
    void RequestRecovery()
    {
        try { Rules.RequestRecovery(store.State, guard.Now); store.Save(); message = "已开始 24 小时冷静期；期间仍继续守护。"; }
        catch (Exception e) { message = e.Message; } RefreshScene();
    }
    void CancelRecovery()
    {
        try { Rules.CancelRecovery(store.State); store.Save(); message = "已撤销提前结束申请，继续攒到 600 抽。"; }
        catch (Exception e) { message = e.Message; } RefreshScene();
    }
    void FinishRecovery()
    {
        if (!Rules.CanFinishRecovery(store.State, guard.Now)) return;
        try { store.State.RecoveryFinished = true; store.Save(); guard.Stop("冷静期结束，守护已停止。"); DisableWatcher(); message = "已结束守护和自动启动。"; }
        catch (Exception e) { message = e.Message; } RefreshScene();
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!canExit && !options.Demo && store.State.Committed && !Ended && e.CloseReason == CloseReason.UserClosing && Native.IsGameRunning(options.TestProcess))
        { e.Cancel = true; Hide(); tray.ShowBalloonTip(1500, "攒愿继续守护", "从托盘可重新打开。原神退出后，主程序会退出。", ToolTipIcon.Info); return; }
        ReleaseResources(); base.OnFormClosing(e);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) ReleaseResources();
        base.Dispose(disposing);
        if (disposing)
        {
            foreach (var font in controlFonts.Values) font.Dispose();
            controlFonts.Clear();
        }
    }
    void ReleaseResources()
    {
        if (released) return; released = true;
        pulse.Stop(); pulse.Dispose(); guard.Dispose(); tray.Visible = false; tray.Dispose(); demoGame?.Dispose();
    }
    void PaintScene(Graphics g)
    {
        if (scene.Width <= 0 || scene.Height <= 0) return;
        float s = ScaleFactor; g.ScaleTransform(s, s); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var bg = new SolidBrush(ScenePanel.Navy); g.FillRectangle(bg, 0, 0, 204, scene.Height / s);
        ScenePanel.Star(g, 42, 49, 16); ScenePanel.DrawText(g, "攒愿", 71, 30, 26, ScenePanel.White, true);
        ScenePanel.DrawText(g, "W I S H  G U A R D", 26, 86, 10, ScenePanel.Muted);
        ScenePanel.DrawText(g, "600 抽 · 永久解除", 25, 647, 13, ScenePanel.Gold);
        ScenePanel.DrawText(g, options.Demo ? "模拟演示 / 不影响原神" : "本机识别 / 无需登录", 25, 674, 11, ScenePanel.Muted);
        string title = tab == 0 ? "把期待，留给值得的相遇。" : tab == 1 ? "按自己的节奏，继续。" : "看得见的守护状态。";
        ScenePanel.DrawText(g, title, 234, 34, 29, ScenePanel.White, true);
        string phase = Ended ? "计划已结束" : !store.State.Committed ? "准备开始" : guard.Phase;
        ScenePanel.Round(g, new(851, 35, 175, 37), ScenePanel.Card, 18);
        ScenePanel.DrawText(g, "●  " + phase, 867, 45, 12, guard.Phase == "保护不可用" ? ScenePanel.Warn : ScenePanel.Gold);
        ScenePanel.DrawText(g, message ?? (store.Fault ? store.FaultMessage : Ended ? "自动启动和识别已经停止。" : guard.Status), 236, 87, 13,
            store.Fault || guard.Phase == "保护不可用" ? ScenePanel.Warn : ScenePanel.Muted, false, 780, 42);
        if (tab == 0) PaintHome(g); else if (tab == 1) PaintSettings(g); else PaintHelp(g);
    }
    void PaintHome(Graphics g)
    {
        var r = store.State.Resources;
        ScenePanel.Round(g, new(234, 144, 792, 241), ScenePanel.Card, 20);
        ScenePanel.DrawText(g, "可用于限定祈愿", 262, 169, 14, ScenePanel.Muted);
        ScenePanel.DrawText(g, r?.Pulls.ToString("N0") ?? "—", 257, 204, 66, ScenePanel.White, true);
        ScenePanel.DrawText(g, "/ 600 抽", 454, 252, 20, ScenePanel.Muted);
        ScenePanel.Star(g, 896, 252, 36);
        using (var pen = new Pen(Color.FromArgb(66, 77, 86), 1)) { g.DrawEllipse(pen, 833, 189, 126, 126); g.DrawEllipse(pen, 820, 176, 152, 152); }
        ScenePanel.Round(g, new(264, 313, 489, 7), Color.FromArgb(43, 57, 72), 3);
        if (r?.Pulls > 0) ScenePanel.Round(g, new(264, 313, Math.Max(7, 489 * Math.Min(1, r.Pulls / 600f)), 7), ScenePanel.Gold, 3);
        ScenePanel.DrawText(g, r == null ? "打开限定祈愿页，资源会自动更新" : r.Pulls >= 600 ? "已达到目标，可以申请永久解除" : $"还差 {600 - r.Pulls:N0} 抽，一点点积累也算数。", 264, 337, 14, ScenePanel.Gold);
        ScenePanel.Round(g, new(234, 405, 382, 136), ScenePanel.Card, 17); ScenePanel.Round(g, new(636, 405, 390, 136), ScenePanel.Card, 17);
        ScenePanel.DrawText(g, "原石", 261, 427, 14, ScenePanel.Muted); ScenePanel.DrawText(g, r?.Primogems.ToString("N0") ?? "—", 259, 458, 35, ScenePanel.White, true);
        ScenePanel.DrawText(g, r == null ? "等待识别" : $"可折合 {r.Primogems / 160:N0} 抽 · 余 {r.Primogems % 160} 原石", 261, 509, 12, ScenePanel.Muted);
        ScenePanel.DrawText(g, "纠缠之缘", 663, 427, 14, ScenePanel.Muted); ScenePanel.DrawText(g, r?.Intertwined.ToString("N0") ?? "—", 661, 458, 35, ScenePanel.White, true);
        ScenePanel.DrawText(g, "只计限定祈愿资源", 663, 509, 12, ScenePanel.Muted);
        ScenePanel.DrawText(g, r == null ? guard.ResourceStatus : $"上次识别 {DateTimeOffset.FromUnixTimeSeconds(r.ObservedAt).ToOffset(TimeSpan.FromHours(8)):MM-dd HH:mm:ss}  ·  {guard.ResourceStatus}", 252, 557, 12, ScenePanel.Muted, false, 750, 29);
        ScenePanel.DrawText(g, !store.State.Committed ? "启用即采用 600 抽永久解除计划；仅支持简体中文、键鼠和窗口化／无边框。" : "达到 600 抽后由可信核验人核验；凭证生效后永久解除，并移除自动启动。", 252, 657, 12, ScenePanel.Muted, false, 740, 50);
    }
    void PaintSettings(Graphics g)
    {
        ScenePanel.Round(g, new(234, 144, 792, 123), ScenePanel.Card, 17);
        ScenePanel.DrawText(g, "游戏联动", 252, 162, 17, ScenePanel.White, true);
        ScenePanel.DrawText(g, "游戏关闭时，只留下轻量启动器等待；不截图、不运行 OCR。", 252, 240, 12, ScenePanel.Muted);
        ScenePanel.Round(g, new(234, 286, 792, 273), ScenePanel.Card, 17);
        ScenePanel.DrawText(g, "想提前结束？给自己一天。", 252, 309, 21, ScenePanel.White, true);
        string remaining = "尚未申请提前结束。";
        if (store.State.RecoveryAt is { } deadline)
        {
            var span = TimeSpan.FromSeconds(Math.Max(0, deadline - guard.Now));
            remaining = $"剩余 {(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}  ·  冷静期内继续守护";
        }
        if (Ended) remaining = "计划已经结束。";
        ScenePanel.DrawText(g, remaining, 252, 353, 19, ScenePanel.Gold);
        ScenePanel.DrawText(g, "改变主意可以随时撤销申请；再次申请时重新计算完整的 24 小时。", 252, 391, 12, ScenePanel.Muted);
        ScenePanel.DrawText(g, "关闭游戏或重启电脑，都不会清空已经保存的冷静期。", 252, 591, 13, ScenePanel.Muted);
        ScenePanel.DrawText(g, "600 抽达标核验属于永久解除，不需要再等待这段冷静期。", 252, 624, 13, ScenePanel.Muted);
    }
    void PaintHelp(Graphics g)
    {
        ScenePanel.Round(g, new(234, 144, 792, 490), ScenePanel.Card, 18);
        ScenePanel.DrawText(g, "本地视觉识别", 252, 168, 23, ScenePanel.White, true);
        ScenePanel.DrawText(g, "01   进入使用纠缠之缘的祈愿页面，资源自动读取。\n\n02   遮挡先出现，资源统计随后完成；连续两次读数一致才更新。\n\n03   点击游戏右上角原有的 × 返回，遮挡自动撤下。\n\n04   数字或币种不清楚时保留旧值，并显示上次识别时间。", 252, 217, 16, ScenePanel.Muted, false, 720, 224);
        ScenePanel.DrawText(g, $"识别状态：{guard.Phase}  ·  最近一帧 {guard.LastMilliseconds:0} ms", 252, 457, 14, ScenePanel.Gold);
        ScenePanel.DrawText(g, guard.ResourceStatus, 252, 490, 13, ScenePanel.Muted);
        ScenePanel.DrawText(g, "不读取游戏内存、不修改客户端、不发送游戏按键；仍可能漏拦，不能承诺零封号风险。", 252, 666, 12, ScenePanel.Muted, false, 740, 42);
    }
}

public sealed class ScenePanel : Panel
{
    public static Color Navy = Color.FromArgb(12, 21, 33), Card = Color.FromArgb(25, 39, 56), White = Color.FromArgb(235, 241, 245),
        Muted = Color.FromArgb(148, 166, 184), Gold = Color.FromArgb(220, 191, 137), Warn = Color.FromArgb(240, 158, 112);
    public Action<Graphics>? Render;
    public ScenePanel() { DoubleBuffered = true; BackColor = Color.FromArgb(17, 28, 43); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); Render?.Invoke(e.Graphics); }
    public static void DrawText(Graphics g, string t, float x, float y, float size, Color color, bool bold = false, float width = 800, float height = 90)
    {
        using var f = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var b = new SolidBrush(color); using var fmt = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(t, f, b, new RectangleF(x, y, width, height), fmt);
    }
    public static void Round(Graphics g, RectangleF r, Color color, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height)); using var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right-d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right-d, r.Bottom-d, d, d, 0, 90); p.AddArc(r.X, r.Bottom-d, d, d, 90, 90); p.CloseFigure();
        using var brush = new SolidBrush(color); g.FillPath(brush, p);
    }
    public static void Star(Graphics g, float x, float y, float r)
    {
        using var b = new SolidBrush(Gold);
        g.FillPolygon(b, new[] {new PointF(x,y-r),new PointF(x+r*.28f,y-r*.28f),new PointF(x+r,y),new PointF(x+r*.28f,y+r*.28f),new PointF(x,y+r),new PointF(x-r*.28f,y+r*.28f),new PointF(x-r,y),new PointF(x-r*.28f,y-r*.28f)});
    }
}
