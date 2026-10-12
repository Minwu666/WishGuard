using System.Text.RegularExpressions;
namespace WishGuard;

internal static class SceneText
{
    // Used only to recognize the end of startup; text and identifiers are never logged.
    public static bool HasGameHud(string text)
    {
        string compact = Regex.Replace(text, @"\s+", "");
        return Regex.IsMatch(compact, @"(?:U[I1l]D|引D)[:：]?\d{6,12}", RegexOptions.IgnoreCase);
    }
}
