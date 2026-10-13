using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

namespace WishGuard;

// A small visible-screen sample for the independent wish-page detector. It
// does not inspect game memory, observe input, or keep captured images.
public static class FastWishVision
{
    const int CaptureWidthLimit = 1200;
    const int PreparedWidthLimit = 2300;
    const int PreparedHeightLimit = 1200;
    const int Padding = 24;

    public static Rectangle Bounds(Size fullFrame)
    {
        if (fullFrame.Width <= 0 || fullFrame.Height <= 0) return Rectangle.Empty;
        // Keep both wish buttons, but omit the bottommost identifier strip.
        int left = fullFrame.Width / 2;
        int top = Math.Clamp((int)Math.Floor(fullFrame.Height * .78), 0, fullFrame.Height - 1);
        int bottom = Math.Clamp((int)Math.Floor(fullFrame.Height * .975), top + 1, fullFrame.Height);
        return Rectangle.FromLTRB(left, top, fullFrame.Width, bottom);
    }

    public static Bitmap Capture(Rectangle gameClient)
    {
        var area = Bounds(gameClient.Size);
        if (area.IsEmpty) throw new ArgumentOutOfRangeException(nameof(gameClient));
        using var raw = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(raw))
            graphics.CopyFromScreen(checked(gameClient.X + area.X), checked(gameClient.Y + area.Y),
                0, 0, raw.Size, CopyPixelOperation.SourceCopy);
        return Limit(raw);
    }

    static Bitmap Limit(Bitmap area)
        => ResourceReader.Crop(area, new Rectangle(Point.Empty, area.Size),
            Math.Min(1d, (double)CaptureWidthLimit / area.Width));

    public static Bitmap Prepare(Bitmap area)
    {
        // Preserve both light and dark lettering. Scale toward a 240-pixel-high
        // sample rather than thresholding light pixels, which loses dark text
        // on the pale wish buttons. A modest border helps OCR segmentation.
        double scale = Math.Clamp(240d / area.Height, 1.25, 3);
        scale = Math.Min(scale, Math.Min((double)PreparedWidthLimit / area.Width,
            (double)PreparedHeightLimit / area.Height));
        int width = Math.Max(1, (int)Math.Round(area.Width * scale));
        int height = Math.Max(1, (int)Math.Round(area.Height * scale));
        var result = new Bitmap(width + Padding * 2, height + Padding * 2, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(result);
            graphics.Clear(Color.White);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(area, new Rectangle(Padding, Padding, width, height),
                new Rectangle(Point.Empty, area.Size), GraphicsUnit.Pixel);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    // Explicit offline diagnostic only. Reports measurements and a verdict,
    // never OCR text, resource amounts, identifiers, image data, or file paths.
    public static async Task<int> Analyze(string imagePath, string reportPath)
    {
        using var frame = new Bitmap(imagePath);
        using var cropped = ResourceReader.Crop(frame, Bounds(frame.Size));
        using var area = Limit(cropped);
        using var prepared = Prepare(area);
        using var worker = new OcrWorker();
        await worker.Start();
        var elapsed = Stopwatch.StartNew();
        bool wish = WishText.IsWish(await worker.Read(prepared));
        elapsed.Stop();
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            wish,
            ocrMilliseconds = Math.Round(elapsed.Elapsed.TotalMilliseconds, 2),
            frameWidth = frame.Width,
            frameHeight = frame.Height,
            areaWidth = area.Width,
            areaHeight = area.Height,
            preparedWidth = prepared.Width,
            preparedHeight = prepared.Height
        }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
