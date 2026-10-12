using System.Runtime.InteropServices;
namespace WishGuard;

// A Windows shortcut owned only while the game is foreground. No injected input
// or game hooks; other routes to wishing continue to use visual recognition.
internal sealed class WishEntryShortcut(Action entered) : NativeWindow, IDisposable
{
    const int Id = 0x5747;
    public bool Registered { get; private set; }
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint window, int id);
    public void Enable(bool enabled)
    {
        if (enabled == Registered) return;
        if (enabled)
        {
            if (Handle == 0) CreateHandle(new CreateParams { Parent = new nint(-3), Caption = "WishGuard shortcut" });
            Registered = RegisterHotKey(Handle, Id, 0x4000, 0x72);
        }
        else { UnregisterHotKey(Handle, Id); Registered = false; }
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam == Id) { entered(); return; }
        base.WndProc(ref message);
    }
    public void Dispose() { Enable(false); if (Handle != 0) DestroyHandle(); }
}
