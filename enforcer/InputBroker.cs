using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WishGuard;
namespace WishGuard.Enforcer;

// Narrow desktop helper: no file writes, executable launches, generated input
// or game memory access. Keyboard filtering is scoped to the verified cover and
// enrolled foreground game; the game retains its own Escape and close actions.
internal static class InputBroker
{
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out int process);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);

    public static async Task<int> Run(string file, EnforcementProfile profile)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        using var identity = WindowsIdentity.GetCurrent();
        int session = Process.GetCurrentProcess().SessionId;
        if (identity.User?.Value != profile.OwnerSid || session == 0) return 3;
        if (!profile.TestOnly && (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator) ||
            !string.Equals(file, EnforcementPaths.Profile(profile.OwnerSid), StringComparison.OrdinalIgnoreCase))) return 3;
        string name = profile.PipeName + "-Input-" + session;
        using var single = new Mutex(true, "Local\\" + name, out bool first); if (!first) return 0;
        var acl = new PipeSecurity(); acl.SetOwner(identity.User); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(identity.User, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        foreach (string sid in new[] { "S-1-5-18", "S-1-5-32-544" })
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(sid), PipeAccessRights.FullControl, AccessControlType.Allow));
        using var keys = new KeyboardGate();
#if WISHGUARD_TESTS
        keys.TraceEnabled = profile.TestOnly;
        long traceWritten = -1;
#endif
        if (!await keys.Started.WaitAsync(TimeSpan.FromSeconds(3))) return 4;
        // Read only the protected authority file; the client's writable mirror
        // cannot authorize input. Keep a monotonic floor within this process.
        var clock = Stopwatch.StartNew();
        long startWall = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), highWaterNow = startWall;
        long Now() => highWaterNow = Math.Max(highWaterNow,
            Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), startWall + clock.ElapsedMilliseconds / 1000));
        EnforcementProfile authority = profile;
        bool Completed() { authority = EnforcementPaths.Read(file); return authority.Completed; }
        while (!Completed())
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, acl);
                using var connect = new CancellationTokenSource(1000);
                await pipe.WaitForConnectionAsync(connect.Token);
                if (!ProcessIdentity.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out int client) || !ProcessIdentity.TrustedClient(client, profile)) continue;
                using var clientProcess = Process.GetProcessById(client);
                if (clientProcess.SessionId != session) continue;
                long checkedAt = Environment.TickCount64;
                while (pipe.IsConnected)
                {
                    using var timeout = new CancellationTokenSource(1000);
                    byte[] request = new byte[9]; await pipe.ReadExactlyAsync(request, timeout.Token);
                    if (Environment.TickCount64 - checkedAt > 1000) { if (Completed()) return 0; checkedAt = Environment.TickCount64; }
                    nint cover = (nint)BinaryPrimitives.ReadInt64LittleEndian(request), fg = GetForegroundWindow();
                    GetWindowThreadProcessId(fg, out int game);
                    bool target = ProcessIdentity.IsTarget(game, profile);
                    bool protectedKeys = false;
                    uint coverThread = GetWindowThreadProcessId(cover, out int owner);
                    if (request[8] == 1 && owner == client && coverThread != 0 && IsWindowVisible(cover))
                    {
                        protectedKeys = target && keys.Ready && !Rules.TemporaryActive(authority.Policy, profile.PublicKey, Now());
                    }
                    keys.Configure(target ? fg : 0, game, cover, client, protectedKeys);
                    byte[] response = new byte[17]; BinaryPrimitives.WriteInt64LittleEndian(response, keys.Entry); response[8] = protectedKeys ? (byte)1 : (byte)0;
                    BinaryPrimitives.WriteInt64LittleEndian(response.AsSpan(9), keys.Return);
                    await pipe.WriteAsync(response, timeout.Token);
#if WISHGUARD_TESTS
                    if (profile.TestOnly && keys.PointerTraceVersion != traceWritten)
                    {
                        var trace = keys.ReadPointerTrace();
                        if (WritePointerTrace(file, trace.Version, trace.Events)) traceWritten = trace.Version;
                    }
#endif
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or InvalidOperationException) { }
            finally { keys.Clear(); }
        }
        return 0;
    }
#if WISHGUARD_TESTS
    static bool WritePointerTrace(string profileFile, long version, KeyboardGate.PointerTraceEvent[] events)
    {
        string path = Path.Combine(Path.GetDirectoryName(profileFile)!, "pointer-trace.json"), temp = path + ".tmp";
        try
        {
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                JsonSerializer.Serialize(output, new { version, events });
            if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
#endif
}
