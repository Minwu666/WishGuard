using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Security.Principal;
namespace WishGuard;

public sealed class EnforcementClient : IDisposable
{
    readonly EnforcementProfile profile;
    readonly SemaphoreSlim mutex = new(1);
    bool disposed;
    public bool Completed => profile.Completed;
    public EnforcementClient(EnforcementProfile profile) => this.profile = profile;
    public async Task<ProtectionReply> Send(string command, bool healthy = false, string? token = null)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await mutex.WaitAsync(timeout.Token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            using var pipe = new NamedPipeClientStream(".", profile.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            if (!ProcessIdentity.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out int pid)) throw new IOException("保护状态无法确认。");
            bool testService = profile.TestOnly && !string.IsNullOrEmpty(profile.ServiceName);
            if (profile.TestOnly && !testService)
            {
                var identity = ProcessIdentity.Read(pid);
                if (identity == null || identity.Value.Sid != profile.OwnerSid ||
                    !string.Equals(identity.Value.Path, profile.EnforcerExecutable, StringComparison.OrdinalIgnoreCase)) throw new IOException("保护状态无法确认。");
            }
            else if (!ProcessIdentity.GetNamedPipeServerSessionId(pipe.SafePipeHandle, out uint session) || session != 0 ||
                pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value != "S-1-5-18")
                throw new IOException(profile.TestOnly ? "Test SCM pipe session or owner cannot be verified." : "保护状态无法确认。");
            // A normal user cannot query a LocalSystem service executable.
            // Use the same session-zero / SYSTEM pipe-owner checks as production;
            // the isolated fixture also verifies the SCM process path externally.
            if (testService && !profile.ServiceName.StartsWith("WishGuard-Test-", StringComparison.Ordinal))
                throw new IOException("Test SCM service name cannot be verified.");
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ProtectionRequest(command, healthy, token)).AsMemory(), timeout.Token);
            string? line = await reader.ReadLineAsync(timeout.Token);
            if (line == null || line.Length > 20000) throw new IOException("保护状态无法确认。");
            var reply = JsonSerializer.Deserialize<ProtectionReply>(line) ?? throw new IOException("保护状态无法确认。");
            if (!reply.Ok) throw new IOException(reply.Message);
            profile.Completed = reply.Completed;
            return reply;
        }
        finally { mutex.Release(); }
    }
    public void Dispose() => disposed = true;
}
