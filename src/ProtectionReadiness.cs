namespace WishGuard;

internal static class ProtectionReadiness
{
    // Windows can report a foreground change before visibility/focus settles.
    // Repair and confirm that handoff without extending the supervisor's lease.
    internal static async Task<string> Confirm(Func<string> read, Action restore, Func<Task>? wait = null)
    {
        restore();
        string status = read();
        for (int attempt = 0; status is "focus-unavailable" or "cover-unavailable" or "input-pending" && attempt < 5; attempt++)
        {
            await (wait?.Invoke() ?? Task.Delay(80));
            restore();
            status = read();
        }
        return status;
    }
}
