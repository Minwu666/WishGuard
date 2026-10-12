using System.Runtime.InteropServices;
using WishGuard;
namespace WishGuard.Enforcer;

// Runs in our own process on a dedicated message-loop thread. No DLL is loaded
// into the game, no input is generated, and no typed text is stored or logged.
internal sealed class KeyboardGate : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] struct KeyData { public uint Key, Scan, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] struct MouseData { public Point Position; public uint Data, Flags, Time; public nuint Extra; }
    delegate nint Hook(int code, nuint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookEx(int id, Hook callback, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
#if WISHGUARD_TESTS
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
#endif
    [DllImport("user32.dll")] static extern nint WindowFromPhysicalPoint(Point point);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread, uint message, nuint wparam, nint lparam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandle(string? module);
    sealed record Scope(nint Game, int GamePid, nint Cover, int ClientPid, bool Block, long Expires);
    Scope? scope;
    readonly Hook callback;
    readonly Hook mouseCallback;
    readonly Thread thread;
    readonly TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    nint hook, mouseHook;
    uint threadId;
    long pulse, serial, returnSerial;
    bool entryHeld, returnHeld;
#if WISHGUARD_TESTS
    internal readonly record struct PointerTraceEvent(long Tick, uint Message, Point Position, long ScopeGame, long ScopeCover,
        long Foreground, bool Block, bool Expired, bool CoverVisible, bool CoverIsIconic, long Hit, bool Blocked);
    readonly object traceSync = new();
    readonly Queue<PointerTraceEvent> pointerTrace = new(64);
    long pointerTraceVersion;
    internal volatile bool TraceEnabled;
    internal long PointerTraceVersion => Interlocked.Read(ref pointerTraceVersion);
    internal (long Version, PointerTraceEvent[] Events) ReadPointerTrace()
    { lock (traceSync) return (pointerTraceVersion, pointerTrace.ToArray()); }
    void TracePointer(PointerTraceEvent value)
    {
        lock (traceSync)
        {
            if (pointerTrace.Count == 64) pointerTrace.Dequeue();
            pointerTrace.Enqueue(value); Interlocked.Increment(ref pointerTraceVersion);
        }
    }
#endif
    public Task<bool> Started => started.Task;
    public long Entry => Interlocked.Read(ref serial);
    public long Return => Interlocked.Read(ref returnSerial);
    public bool Ready => hook != 0 && mouseHook != 0 && Environment.TickCount64 - Interlocked.Read(ref pulse) < 500;
    public KeyboardGate()
    {
        callback = OnKey;
        mouseCallback = OnMouse;
        thread = new Thread(Run) { IsBackground = true, Name = "WishGuard keyboard compatibility" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    public void Configure(nint game, int gamePid, nint cover, int client, bool block)
        => Volatile.Write(ref scope, new(game, gamePid, cover, client, block, Environment.TickCount64 + 700));
    public void Clear() => Volatile.Write(ref scope, null);
    void Run()
    {
        try
        {
            threadId = GetCurrentThreadId();
            using var context = new ApplicationContext();
            using var timer = new System.Windows.Forms.Timer { Interval = 50 };
            timer.Tick += (_, _) => Interlocked.Exchange(ref pulse, Environment.TickCount64);
            timer.Start();
            hook = SetWindowsHookEx(13, callback, GetModuleHandle(null), 0);
            mouseHook = SetWindowsHookEx(14, mouseCallback, GetModuleHandle(null), 0);
            Interlocked.Exchange(ref pulse, Environment.TickCount64);
            started.TrySetResult(hook != 0 && mouseHook != 0);
            if (hook != 0 && mouseHook != 0) Application.Run(context);
        }
        finally
        {
            started.TrySetResult(false);
            if (hook != 0) UnhookWindowsHookEx(hook);
            if (mouseHook != 0) UnhookWindowsHookEx(mouseHook);
            hook = mouseHook = 0; Clear();
        }
    }
    nint OnKey(int code, nuint message, nint data)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, data);
        var key = Marshal.PtrToStructure<KeyData>(data);
        bool down = message is 0x100 or 0x104;
        if (key.Key == 0x72 && !down) entryHeld = false;
        bool freshReturn = key.Key == 0x1B && down && !returnHeld;
        if (key.Key == 0x1B) returnHeld = down;
        var current = Volatile.Read(ref scope);
        // Identity/path validation happens outside this latency-sensitive callback.
        // Recheck the actual HWND/PID here so switching applications is immediate.
        if (current != null && current.Game != 0 && Environment.TickCount64 <= current.Expires &&
            GetForegroundWindow() == current.Game && GetWindowThreadProcessId(current.Game, out int game) != 0 && game == current.GamePid)
        {
            if (key.Key == 0x72 && down && !entryHeld) { entryHeld = true; Interlocked.Increment(ref serial); }
            if (current.Block && IsWindowVisible(current.Cover) &&
                GetWindowThreadProcessId(current.Cover, out int owner) != 0 && owner == current.ClientPid)
            {
                if (freshReturn) Interlocked.Increment(ref returnSerial);
                bool desktopChord = (key.Flags & 0x20) != 0 || GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0;
                if (!KeyboardPolicy.Allow(key.Key, desktopChord, down)) return 1;
            }
        }
        return CallNextHookEx(hook, code, message, data);
    }
    nint OnMouse(int code, nuint message, nint data)
    {
        if (code < 0 || message is not (0x201 or 0x204 or 0x207 or 0x20B or 0x20A or 0x20E))
            return CallNextHookEx(mouseHook, code, message, data);
        var current = Volatile.Read(ref scope);
        bool blocked = false;
#if WISHGUARD_TESTS
        MouseData observedPointer = default;
        bool pointerRead = false;
        nint observedHit = 0;
#endif
        if (current is { Block: true } && current.Game != 0 && Environment.TickCount64 <= current.Expires &&
            GetForegroundWindow() == current.Game && GetWindowThreadProcessId(current.Game, out int game) != 0 && game == current.GamePid &&
            IsWindowVisible(current.Cover) && GetWindowThreadProcessId(current.Cover, out int owner) != 0 && owner == current.ClientPid)
        {
            var pointer = Marshal.PtrToStructure<MouseData>(data);
            nint hit = WindowFromPhysicalPoint(pointer.Position);
#if WISHGUARD_TESTS
            observedPointer = pointer; pointerRead = true; observedHit = hit;
#endif
            // Observe the game's own return click without consuming it. The
            // client still has to confirm a normal scene before concealing.
            if (message == 0x201 && hit == current.Game) Interlocked.Increment(ref returnSerial);
            // Physical hit testing respects the actual return hole, overlapping
            // windows and per-monitor DPI without coordinate guesses.
            if (message is 0x201 or 0x204 or 0x207 or 0x20B or 0x20A or 0x20E &&
                hit == current.Cover) blocked = true;
        }
#if WISHGUARD_TESTS
        if (TraceEnabled && current != null && current.Game != 0)
        {
            nint foreground = GetForegroundWindow();
            if (foreground == current.Game)
            {
                var pointer = pointerRead ? observedPointer : Marshal.PtrToStructure<MouseData>(data);
                nint hit = pointerRead ? observedHit : WindowFromPhysicalPoint(pointer.Position);
                long tick = Environment.TickCount64;
                TracePointer(new(tick, (uint)message, pointer.Position, (long)current.Game, (long)current.Cover, (long)foreground,
                    current.Block, tick > current.Expires, IsWindowVisible(current.Cover), IsIconic(current.Cover), (long)hit, blocked));
            }
        }
#endif
        return blocked ? 1 : CallNextHookEx(mouseHook, code, message, data);
    }
    public void Dispose()
    {
        Clear();
        if (threadId != 0) PostThreadMessage(threadId, 0x12, 0, 0);
        thread.Join(1500);
    }
}
