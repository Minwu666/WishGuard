namespace WishGuard;
public sealed class ScreenGate
{
    public bool Blocked { get; private set; } = true;
    public bool WishSeen { get; private set; }
    public int Version { get; private set; }
    int cleanFrames;
    long holdUntil;
    public void Recheck() { Version++; Blocked = true; cleanFrames = 0; }
    public void Reset() { WishSeen = false; holdUntil = 0; Recheck(); }
    public void HoldUntil(long until) { Recheck(); holdUntil = Math.Max(holdUntil, until); }
    public void CancelHold() { holdUntil = 0; Recheck(); }
    public void Observe(int version, bool readable, bool wish, long now = long.MaxValue)
    {
        if (version != Version) return;
        if (!readable) { Recheck(); return; }
        if (wish) { WishSeen = true; Blocked = true; cleanFrames = 0; holdUntil = 0; return; }
        if (now < holdUntil) return;
        if (++cleanFrames >= (WishSeen ? 3 : 2))
        {
            // Invalidate detections captured before this confirmed return.
            if (Blocked || WishSeen) Version++;
            Blocked = false; WishSeen = false;
        }
    }
}
