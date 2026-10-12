namespace WishGuard;

public static class UiRegression
{
    public static void VerifyShieldInitialPlacement()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var area = Screen.PrimaryScreen!.WorkingArea;
                using var game = new Form { StartPosition = FormStartPosition.Manual, Location = new(area.Left + 73, area.Top + 95), ClientSize = new(640, 360), ShowInTaskbar = false };
                using var cover = new Shield { Passive = true };
                game.Show();
                var expected = Native.BoundsOf(game.Handle) ?? throw new IOException("Test surface is unavailable.");
                cover.Place(expected, game.Handle);
                if (cover.Bounds != expected || Native.BoundsOf(cover.Handle) != expected)
                    throw new InvalidOperationException("The first shown cover must align with the game client.");
                cover.Conceal();
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Initial shield placement timed out.");
        if (failure != null) throw new InvalidOperationException("Initial shield placement failed.", failure);
    }
    public static void VerifyFontLifetime(string data)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(new StateStore(Path.Combine(data, "ui-fonts")), "",
                    new RuntimeOptions(false, true, false, true, false, null));
                form.VerifyLayoutFontLifetime();
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("UI font regression timed out.");
        if (failure != null) throw new InvalidOperationException("UI fonts must remain valid through repeated layout.", failure);
    }
}
