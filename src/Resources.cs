using System.Text;
using System.Text.RegularExpressions;
namespace WishGuard;

public sealed record ResourceDetection(long? Primogems, long? Intertwined, string Status)
{ public bool Valid => Primogems != null && Intertwined != null; }
public static class ResourceReader
{
    public static bool LimitedBanner(string text)
    {
        string s = Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"\s", "");
        return s.Contains("角色活动祈愿") || s.Contains("武器活动祈愿") || s.Contains("集录祈愿");
    }
    public static Bitmap Crop(Bitmap image, Rectangle rect, double zoom = 1)
    {
        rect.Intersect(new Rectangle(0, 0, image.Width, image.Height));
        var result = new Bitmap(Math.Max(1, (int)(rect.Width * zoom)), Math.Max(1, (int)(rect.Height * zoom)));
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(image, new Rectangle(0, 0, result.Width, result.Height), rect, GraphicsUnit.Pixel);
        return result;
    }
    public static bool PinkFateIcon(Bitmap header, OcrWord count)
    {
        var rect = Rectangle.FromLTRB((int)(count.X - count.Height * 2.9), (int)(count.Y - count.Height * .4),
            (int)(count.X - count.Height * .25), (int)(count.Y + count.Height * 1.5));
        rect.Intersect(new Rectangle(0, 0, header.Width, header.Height));
        int pink = 0, total = 0;
        for (int y = rect.Top; y < rect.Bottom; y += 2)
        for (int x = rect.Left; x < rect.Right; x += 2)
        {
            var c = header.GetPixel(x, y); total++;
            if (c.R > 95 && c.B > 110 && c.R > c.G * 1.12 && c.B > c.G * 1.14) pink++;
        }
        return total > 40 && (double)pink / total > .018;
    }
    public static Bitmap LightText(Bitmap source, int padding = 50)
    {
        var result = new Bitmap(source.Width + padding * 2, source.Height + padding * 2);
        var src = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var dst = result.LockBits(new Rectangle(0, 0, result.Width, result.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var input = new byte[source.Width * 4]; var row = new byte[result.Width * 4];
            for (int y = 0; y < result.Height; y++)
            {
                Array.Fill(row, (byte)255);
                if (y >= padding && y < padding + source.Height)
                {
                    System.Runtime.InteropServices.Marshal.Copy(src.Scan0 + (y - padding) * src.Stride, input, 0, input.Length);
                    for (int x = 0; x < source.Width; x++)
                    {
                        int i = x * 4, low = Math.Min(input[i], Math.Min(input[i+1], input[i+2])), high = Math.Max(input[i], Math.Max(input[i+1], input[i+2]));
                        if (low > 190 && high - low < 85) { int j = (x + padding) * 4; row[j] = row[j+1] = row[j+2] = 0; }
                    }
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, dst.Scan0 + y * dst.Stride, row.Length);
            }
        }
        finally { source.UnlockBits(src); result.UnlockBits(dst); }
        return result;
    }
    public static ResourceDetection ParseHeader(OcrResult data, Bitmap header, bool limited)
    {
        if (!limited) return new(null, null, "当前卡池币种未确认；保留上次识别结果");
        var numbers = data.Words.Where(w => Regex.IsMatch(w.Text.Normalize(NormalizationForm.FormKC), @"^[0-9]{1,9}$") && w.Height >= 10)
            .OrderBy(w => w.X).ToList();
        if (numbers.Count >= 3)
        {
            double typical = numbers.Select(w => w.Height).Order().ElementAt(numbers.Count / 2);
            // The circular plus button can OCR as 0, but it is much taller than the digits.
            numbers = numbers.Where(w => w.Height >= typical * .75 && w.Height <= typical * 1.3).ToList();
        }
        if (numbers.Count != 2) return new(null, null, "资源数字不清楚，等待下一帧");
        var a = numbers[0]; var b = numbers[1];
        if (Math.Abs(a.Y - b.Y) > Math.Max(a.Height, b.Height) * .7 || b.X - a.X < header.Width * .16)
            return new(null, null, "资源布局暂时无法确认");
        // The explicit limited-banner label determines the currency. HDR and display
        // color transforms can shift the fate icon away from pink, so color alone
        // must not veto two aligned amounts in the known resource positions.
        if (a.X < header.Width * .12 || a.X > header.Width * .63 || b.X < header.Width * .64 || b.X > header.Width * .96)
            return new(null, null, "资源位置暂时无法确认");
        if (!long.TryParse(a.Text.Normalize(NormalizationForm.FormKC), out long stones) || !long.TryParse(b.Text.Normalize(NormalizationForm.FormKC), out long fates))
            return new(null, null, "资源数字不清楚");
        try { Rules.Pulls(stones, fates); } catch { return new(null, null, "资源读数异常，等待重新识别"); }
        return new(stones, fates, "本机自动识别");
    }
    public static async Task<ResourceDetection> Read(Bitmap frame, OcrWorker worker)
    {
        int h = frame.Height, w = frame.Width;
        using var header = Crop(frame, Rectangle.FromLTRB(w - (int)(h * .43), 0, w - (int)(h * .105), (int)(h * .095)), 3);
        using var banner = Crop(frame, Rectangle.FromLTRB((int)(w * .10), (int)(h * .12), (int)(w * .47), (int)(h * .43)), 3);
        using var digits = LightText(header); using var label = LightText(banner);
        var raw = await worker.ReadResult(digits);
        var headerResult = raw with { Words = raw.Words.Select(x => x with { X = x.X - 50, Y = x.Y - 50 }).ToArray() };
        string bannerText = await worker.Read(label);
        bool limited = LimitedBanner(bannerText);
        var result = ParseHeader(headerResult, header, limited);
        if (result.Valid || !limited) return result;
        // An isolated zero can disappear when OCR segments the entire header.
        // Read the two number areas separately, excluding the circular plus button.
        var segmented = new List<OcrWord>();
        foreach (var range in new[] { (.12, .52), (.65, 1.0) })
        {
            var area = Rectangle.FromLTRB((int)(header.Width * range.Item1), 0, (int)(header.Width * range.Item2), header.Height);
            using var part = Crop(header, area); using var prepared = LightText(part);
            var read = await worker.ReadResult(prepared);
            var numeric = read.Words.Select(w => w with { Text = w.Text is "O" or "o" or "〇" ? "0" : w.Text })
                .Where(w => Regex.IsMatch(w.Text, @"^[0-9]{1,9}$") && w.Height >= 10).ToArray();
            if (numeric.Length != 1) return result;
            segmented.Add(numeric[0] with { X = numeric[0].X - 50 + area.X, Y = numeric[0].Y - 50 });
        }
        return ParseHeader(new("", segmented.ToArray()), header, limited);
    }
}
public sealed class ResourceConsensus
{
    ResourceDetection? previous; int matches;
    public ResourceSnapshot? Observe(ResourceDetection r, long now)
    {
        if (!r.Valid) { previous = null; matches = 0; return null; }
        if (previous != null && previous.Primogems == r.Primogems && previous.Intertwined == r.Intertwined) matches++;
        else { matches = 1; previous = r; }
        return matches >= 2 ? new(r.Primogems!.Value, r.Intertwined!.Value, now) : null;
    }
    public void Reset() { previous = null; matches = 0; }
}
