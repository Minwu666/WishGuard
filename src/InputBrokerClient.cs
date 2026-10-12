using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
namespace WishGuard;

internal sealed class InputBrokerClient(EnforcementProfile profile) : IDisposable
{
    NamedPipeClientStream? pipe;
    bool disposed;
    public bool Connected => pipe?.IsConnected == true;
    public bool KeysProtected { get; private set; }
    public async Task<(long Entry, bool Focused, long Return)> Poll(nint cover, bool focus)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var timeout = new CancellationTokenSource(700);
        try
        {
            if (pipe == null)
            {
                pipe = new NamedPipeClientStream(".", profile.PipeName + "-Input-" + Process.GetCurrentProcess().SessionId, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(timeout.Token);
                if (!ProcessIdentity.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out int pid) ||
                    !ProcessIdentity.GetNamedPipeServerSessionId(pipe.SafePipeHandle, out uint session) || session != Process.GetCurrentProcess().SessionId ||
                    pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier))?.Value != profile.OwnerSid ||
                    !profile.TestOnly && !ProcessIdentity.Elevated(pid) ||
                    !string.Equals(ProcessIdentity.PathOf(pid), profile.EnforcerExecutable, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("窗口兼容组件无法确认。");
            }
            byte[] request = new byte[9]; BinaryPrimitives.WriteInt64LittleEndian(request, (long)cover); request[8] = focus ? (byte)1 : (byte)0;
            await pipe.WriteAsync(request, timeout.Token);
            byte[] response = new byte[17]; await pipe.ReadExactlyAsync(response, timeout.Token);
            KeysProtected = response[8] == 1;
            return (BinaryPrimitives.ReadInt64LittleEndian(response), KeysProtected, BinaryPrimitives.ReadInt64LittleEndian(response.AsSpan(9)));
        }
        catch { KeysProtected = false; pipe?.Dispose(); pipe = null; throw; }
    }
    public void Dispose() { disposed = true; KeysProtected = false; pipe?.Dispose(); pipe = null; }
}
