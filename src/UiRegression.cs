namespace WishGuard;

public static class UiRegression
{
    public static void VerifyResourcePreview()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var area = Screen.PrimaryScreen!.WorkingArea;
                var bounds = new Rectangle(area.Left + 20, area.Top + 20, 640, 360);
                var frameSize = new Size(1280, 720);
                var header = ResourceReader.HeaderBounds(frameSize);
                var background = Color.FromArgb(231, 17, 211);
                var firstColor = Color.FromArgb(31, 224, 77);
                var nextColor = Color.FromArgb(17, 207, 237);
                using var cover = new Shield { Passive = true };

                Bitmap Frame(Color color)
                {
                    var bitmap = new Bitmap(frameSize.Width, frameSize.Height);
                    using var graphics = Graphics.FromImage(bitmap);
                    graphics.Clear(background);
                    using var brush = new SolidBrush(color);
                    graphics.FillRectangle(brush, header);
                    return bitmap;
                }
                Bitmap Render()
                {
                    var bitmap = new Bitmap(cover.ClientSize.Width, cover.ClientSize.Height);
                    cover.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    return bitmap;
                }
                Point ResourcePoint(double fraction = .5) => new(
                    (int)((header.Left + header.Width * fraction) * cover.ClientSize.Width / frameSize.Width),
                    (int)((header.Top + header.Height * .5) * cover.ClientSize.Height / frameSize.Height));
                bool Near(Color actual, Color expected) => Math.Abs(actual.R - expected.R) <= 3 &&
                    Math.Abs(actual.G - expected.G) <= 3 && Math.Abs(actual.B - expected.B) <= 3;
                void VerifyPreview(Bitmap rendered, Bitmap original, Color color)
                {
                    foreach (double fraction in new[] { .2, .5, .7 })
                    {
                        var point = ResourcePoint(fraction);
                        if (!Near(rendered.GetPixel(point.X, point.Y), color))
                            throw new InvalidOperationException("The resource preview must render at its mapped game position.");
                    }
                    foreach (var point in new[] { new Point(40, 80), new Point(cover.Width / 4, cover.Height * 3 / 4), new Point(20, cover.Height - 20) })
                    {
                        var pixel = rendered.GetPixel(point.X, point.Y);
                        if (Near(pixel, background) || pixel.ToArgb() != original.GetPixel(point.X, point.Y).ToArgb())
                            throw new InvalidOperationException("The preview must not copy other parts of the game frame.");
                    }
                    var center = ResourcePoint();
                    var returnArea = ReturnArea.Of(cover.ClientRectangle);
                    if (cover.Region == null || !cover.Region.IsVisible(center) ||
                        cover.Region.IsVisible(returnArea.Left + returnArea.Width / 2, returnArea.Top + returnArea.Height / 2))
                        throw new InvalidOperationException("The resource preview must remain covered while the return area stays available.");
                }

                cover.Place(bounds);
                using var initial = Render();
                using (var frame = Frame(firstColor)) cover.ShowResourcePreview(frame);
                using (var rendered = Render()) VerifyPreview(rendered, initial, firstColor);
                using (var frame = Frame(nextColor)) cover.ShowResourcePreview(frame);
                using (var rendered = Render()) VerifyPreview(rendered, initial, nextColor);

                bounds.Size = new Size(680, 380);
                cover.Place(bounds);
                using var resized = Render();
                var resizedPoint = ResourcePoint();
                if (Near(resized.GetPixel(resizedPoint.X, resizedPoint.Y), nextColor))
                    throw new InvalidOperationException("Resizing the shield must clear the old resource preview.");
                using (var frame = Frame(nextColor)) cover.ShowResourcePreview(frame);
                using (var rendered = Render()) VerifyPreview(rendered, resized, nextColor);
                cover.Conceal();
                cover.Place(bounds);
                using var reopened = Render();
                if (reopened.GetPixel(resizedPoint.X, resizedPoint.Y).ToArgb() != resized.GetPixel(resizedPoint.X, resizedPoint.Y).ToArgb())
                    throw new InvalidOperationException("Reopening the shield must not show stale resources.");
                cover.Conceal();
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10))) throw new TimeoutException("Resource preview regression timed out.");
        if (failure != null) throw new InvalidOperationException("Resource preview regression failed.", failure);
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
