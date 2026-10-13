using System.Drawing.Drawing2D;
namespace WishGuard;
public sealed class DemoGame : Form
{
#if WISHGUARD_TESTS
    internal void ShowWishForTest(bool value) { if (value && !wish) WishPaintedAtForTest = 0; wish = value; transitionForTest = false; Arrange(); }
    internal bool WishForTest => wish;
    internal int AttemptsForTest => attempts;
    internal long WishPaintedAtForTest { get; private set; }
    internal int HomeEntriesForTest { get; private set; }
    internal int MouseEntriesForTest { get; private set; }
    internal int EscapeReturnsForTest { get; private set; }
    internal int MouseReturnsForTest { get; private set; }
    internal string OrdinaryFooterForTest = "";
    internal bool HeaderDetailsForTest = true;
    bool transitionForTest;
    internal void ShowWishTransitionForTest(bool value)
    { transitionForTest = value; wish = false; loadingForTest = false; Arrange(); }
    // These methods exercise only this synthetic form's own handlers. They do
    // not send keys, post mouse messages, or signal WishGuard's entry shortcut.
    internal bool OpenFromHomeForTest() => HandleTestKey(Keys.Home);
    internal bool ReturnFromEscapeForTest() => HandleTestKey(Keys.Escape);
    internal void OpenFromMouseForTest() { MouseEntriesForTest++; open.PerformClick(); }
    internal void ReturnFromMouseForTest() { MouseReturnsForTest++; close.PerformClick(); }
    bool HandleTestKey(Keys keyData)
    {
        if (keyData == Keys.Home && !wish && !loadingForTest && !transitionForTest)
        { HomeEntriesForTest++; open.PerformClick(); return wish; }
        if (keyData == Keys.Escape && wish) { EscapeReturnsForTest++; close.PerformClick(); return !wish; }
        if (keyData == Keys.Enter && wish) { attempts++; Invalidate(); return true; }
        return false;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (HandleTestKey(keyData)) return true;
        return base.ProcessCmdKey(ref msg, keyData);
    }
    bool loadingForTest;
    internal void ShowLoadingForTest(bool value) { loadingForTest = value; Arrange(); }
#endif
    bool wish; readonly Button open = new(), close = new(); int attempts;
#if WISHGUARD_TESTS
    sealed class SignalButton : Button
    {
        internal Action? Painted;
        protected override void WndProc(ref Message message)
        { bool paint = message.Msg == 0x0F; base.WndProc(ref message); if (paint && Visible) Painted?.Invoke(); }
    }
    readonly SignalButton one = new(), ten = new();
#else
    readonly Button one = new(), ten = new();
#endif
    public DemoGame()
    {
        Text = "攒愿测试场 · 模拟窗口"; ClientSize = new Size(1280, 800); MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(103, 138, 170); DoubleBuffered = true;
        Font = new Font("Microsoft YaHei UI", 15); open.Text = "打开模拟祈愿"; close.Text = "×"; one.Text = "祈愿1次"; ten.Text = "祈愿10次";
#if WISHGUARD_TESTS
        one.Painted = MarkWishPaint; ten.Painted = MarkWishPaint;
#endif
        open.Click += (_, _) => {
#if WISHGUARD_TESTS
            if (!wish) WishPaintedAtForTest = 0;
#endif
            wish = true; Arrange(); }; close.Click += (_, _) => { wish = false; Arrange(); };
        one.Click += (_, _) => { attempts++; Invalidate(); }; ten.Click += (_, _) => { attempts++; Invalidate(); };
        Controls.AddRange([open, close, one, ten]); Resize += (_, _) => Arrange(); Arrange();
    }
    void Arrange()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        open.SetBounds(60, h / 2, 240, 54); open.Visible = !wish;
#if WISHGUARD_TESTS
        if (loadingForTest || transitionForTest) open.Visible = false;
#endif
        close.SetBounds(w - 75, 12, 58, 52); close.Visible = wish;
        one.SetBounds(w - 505, h - 82, 230, 56); ten.SetBounds(w - 255, h - 82, 230, 56); one.Visible = ten.Visible = wish;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = ClientSize.Width, h = ClientSize.Height;
#if WISHGUARD_TESTS
        if (loadingForTest)
        {
            ScenePanel.DrawText(g, "准备加载数据 45.29%", 350, h - 90, 26, Color.White);
            return;
        }
#endif
        ScenePanel.DrawText(g, wish ? "模拟祈愿界面" : "普通游戏画面 · 模拟", 55, 125, 34, Color.White, true);
        ScenePanel.DrawText(g, $"这里只是测试画面，不会花费原石。\n\n模拟抽卡按钮收到点击：{attempts} 次", 58, h * .39f, 22, Color.White);
        ScenePanel.DrawText(g, "UID 000000000", 24, h - 30, 16, Color.White);
#if WISHGUARD_TESTS
        if (!wish && (transitionForTest || OrdinaryFooterForTest.Length != 0))
            ScenePanel.DrawText(g, transitionForTest ? "正在加载祈愿页面…" : OrdinaryFooterForTest,
                w * .52f, h - 125, 22, Color.White);
        if (wish && !HeaderDetailsForTest) return;
#endif
        if (!wish) return;
        ScenePanel.Round(g, new(w - h*.37f, h*.025f, h*.17f, h*.043f), Color.FromArgb(57,77,100), 15);
        ScenePanel.Round(g, new(w - h*.185f, h*.025f, h*.083f, h*.043f), Color.FromArgb(57,77,100), 15);
        ScenePanel.DrawText(g, "23680", w-h*.335f, h*.028f, h*.027f, Color.White, true);
        ScenePanel.DrawText(g, "58", w-h*.143f, h*.028f, h*.027f, Color.White, true);
        using var pink = new SolidBrush(Color.FromArgb(229,128,235)); g.FillEllipse(pink,w-h*.179f,h*.03f,h*.03f,h*.03f);
        ScenePanel.Round(g, new(w*.16f,h*.22f,w*.25f,h*.049f),Color.FromArgb(50,144,149),3);
        ScenePanel.DrawText(g,"角色活动祈愿",w*.17f,h*.227f,h*.027f,Color.White,true);
    }
#if WISHGUARD_TESTS
    void MarkWishPaint()
    {
        if (wish && !loadingForTest && WishPaintedAtForTest == 0)
            WishPaintedAtForTest = System.Diagnostics.Stopwatch.GetTimestamp();
    }
#endif
}
