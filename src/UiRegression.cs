namespace WishGuard;

public static class UiRegression
{
    public static void VerifyResourceVisibility()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var area = Screen.PrimaryScreen!.WorkingArea;
                var bounds = new Rectangle(area.Left + 20, area.Top + 20, 640, 360);
                using var cover = new Shield { Passive = true, Bounds = bounds };
                var trackingLimit = SystemInformation.MaxWindowTrackSize;
                using var pixel = new Bitmap(1, 1);
                using var graphics = Graphics.FromImage(pixel);
                void VerifyRegion()
                {
                    // A synthetic resolution beyond this desktop's tracking limit
                    // tests the generated region geometry, not native window sizing.
                    bool nativeSizeSupported = bounds.Width <= trackingLimit.Width && bounds.Height <= trackingLimit.Height;
                    if (nativeSizeSupported && cover.Bounds != bounds || cover.Region == null)
                        throw new InvalidOperationException($"The shield region must be aligned: expected {bounds}, actual {cover.Bounds}, missing region {cover.Region == null}.");
                    var header = ResourceReader.HeaderBounds(bounds.Size);
                    var resources = Rectangle.FromLTRB(header.Left, 0, bounds.Width, header.Bottom);
                    var returnArea = ReturnArea.Of(new Rectangle(Point.Empty, bounds.Size));
                    if (header.Width <= 0 || header.Height <= 0 || cover.Region.IsVisible(resources) || cover.Region.IsVisible(returnArea))
                        throw new InvalidOperationException("The original top resources and return area must remain outside the shield.");
                    if (!cover.Region.IsVisible(header.Left + 1, header.Bottom + 1))
                        throw new InvalidOperationException("The opening must stop below the narrow resource strip.");
                    var footer = Rectangle.FromLTRB(0, (int)(bounds.Height * .68), bounds.Width, bounds.Height);
                    using var uncoveredFooter = new Region(footer);
                    uncoveredFooter.Exclude(cover.Region);
                    if (!uncoveredFooter.IsEmpty(graphics))
                        throw new InvalidOperationException("The entire lower wish area must remain covered.");
                }
                // The first requested bounds already equal the unshown form's bounds.
                cover.Place(bounds);
                VerifyRegion();
                foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080), new Size(2560, 1440), new Size(2560, 1600), new Size(3440, 1440), new Size(640, 360) })
                {
                    bounds.Size = size;
                    cover.Place(bounds);
                    VerifyRegion();
                    bounds.Offset(11, 9);
                    cover.Place(bounds);
                    VerifyRegion();
                    cover.Conceal();
                    cover.Place(bounds);
                    VerifyRegion();
                }
                cover.Conceal();
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Resource visibility regression timed out.");
        if (failure != null) throw new InvalidOperationException("Resource visibility regression failed: " + failure.Message, failure);
    }
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
