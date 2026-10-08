namespace WishGuard;

public static class UiRegression
{
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
