using System.Drawing;
namespace WishGuard;

internal static class ReturnArea
{
    internal static Rectangle Of(Rectangle bounds)
    {
        int width = Math.Max(65, (int)(bounds.Width * .07)), height = Math.Max(65, (int)(bounds.Height * .14));
        return new(bounds.Right - width, bounds.Top, width, height);
    }
}
