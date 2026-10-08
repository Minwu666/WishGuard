using System.Drawing.Drawing2D;
namespace WishGuard;
public sealed class DemoGame : Form
{
    bool wish; readonly Button open = new(), close = new(), one = new(), ten = new(); int attempts;
    public DemoGame()
    {
        Text = "攒愿测试场 · 模拟窗口"; ClientSize = new Size(1280, 800); MinimumSize = new Size(960, 600);
        StartPosition = FormStartPosition.CenterScreen; BackColor = Color.FromArgb(103, 138, 170); DoubleBuffered = true;
        Font = new Font("Microsoft YaHei UI", 15); open.Text = "打开模拟祈愿"; close.Text = "×"; one.Text = "祈愿1次"; ten.Text = "祈愿10次";
        open.Click += (_, _) => { wish = true; Arrange(); }; close.Click += (_, _) => { wish = false; Arrange(); };
        one.Click += (_, _) => { attempts++; Invalidate(); }; ten.Click += (_, _) => { attempts++; Invalidate(); };
        Controls.AddRange([open, close, one, ten]); Resize += (_, _) => Arrange(); Arrange();
    }
    void Arrange()
    {
        int w = ClientSize.Width, h = ClientSize.Height;
        open.SetBounds(60, h / 2, 240, 54); open.Visible = !wish;
        close.SetBounds(w - 75, 12, 58, 52); close.Visible = wish;
        one.SetBounds(w - 505, h - 82, 230, 56); ten.SetBounds(w - 255, h - 82, 230, 56); one.Visible = ten.Visible = wish;
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        int w = ClientSize.Width, h = ClientSize.Height;
        ScenePanel.DrawText(g, wish ? "模拟祈愿界面" : "普通游戏画面 · 模拟", 55, 125, 34, Color.White, true);
        ScenePanel.DrawText(g, $"这里只是测试画面，不会花费原石。\n\n模拟抽卡按钮收到点击：{attempts} 次", 58, h * .39f, 22, Color.White);
        if (!wish) return;
        ScenePanel.Round(g, new(w - h*.37f, h*.025f, h*.17f, h*.043f), Color.FromArgb(57,77,100), 15);
        ScenePanel.Round(g, new(w - h*.185f, h*.025f, h*.083f, h*.043f), Color.FromArgb(57,77,100), 15);
        ScenePanel.DrawText(g, "23680", w-h*.335f, h*.028f, h*.027f, Color.White, true);
        ScenePanel.DrawText(g, "58", w-h*.143f, h*.028f, h*.027f, Color.White, true);
        using var pink = new SolidBrush(Color.FromArgb(229,128,235)); g.FillEllipse(pink,w-h*.179f,h*.03f,h*.03f,h*.03f);
        ScenePanel.Round(g, new(w*.16f,h*.22f,w*.25f,h*.049f),Color.FromArgb(50,144,149),3);
        ScenePanel.DrawText(g,"角色活动祈愿",w*.17f,h*.227f,h*.027f,Color.White,true);
    }
}
