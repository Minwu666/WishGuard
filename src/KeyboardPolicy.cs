namespace WishGuard;

internal static class KeyboardPolicy
{
    // Escape is the game's own return action. Preserve desktop navigation and
    // modifier releases; never synthesize keys or remember typed text.
    internal static bool Allow(uint key, bool desktopChord, bool keyDown = true) => !keyDown || desktopChord || key is
        0x1B or 0x5B or 0x5C or 0x10 or 0x11 or 0x12 or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
}
