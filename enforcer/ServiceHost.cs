using System.ComponentModel;
using System.Runtime.InteropServices;
using WishGuard;

namespace WishGuard.Enforcer;

// A regular, visible SCM service. No protected-process flags, driver, injected
// code, or interference with Windows management tools.
internal static class ServiceHost
{
    const uint OwnProcess = 0x10, Stopped = 1, StartPending = 2, StopPending = 3, Running = 4;
    const uint Stop = 1, Interrogate = 4, Shutdown = 5;
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void ServiceMain(uint count, nint arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate uint ControlHandler(uint control, uint eventType, nint eventData, nint context);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct ServiceEntry
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? Name;
        [MarshalAs(UnmanagedType.FunctionPtr)] public ServiceMain? Main;
    }
    [StructLayout(LayoutKind.Sequential)] struct Status
    {
        public uint Type, State, Accepted, Win32Exit, SpecificExit, Checkpoint, WaitHint;
    }
    [StructLayout(LayoutKind.Sequential)] struct Config
    {
        public uint Type, Start, Error;
        public nint BinaryPath, LoadGroup;
        public uint Tag;
        public nint Dependencies, Account, DisplayName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool StartServiceCtrlDispatcherW([In] ServiceEntry[] table);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint RegisterServiceCtrlHandlerExW(string name, ControlHandler handler, nint context);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetServiceStatus(nint handle, ref Status status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenServiceW(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseServiceHandle(nint handle);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool QueryServiceConfigW(nint service, nint config, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool ChangeServiceConfigW(nint service, uint type, uint start, uint error, string? binaryPath,
        string? loadGroup, nint tag, string? dependencies, string? account, string? password, string? displayName);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] static extern bool DeleteService(nint service);

    static readonly object sync = new();
    static readonly ServiceMain main = OnMain;
    static readonly ControlHandler controls = OnControl;
    static string name = "";
    static Func<CancellationToken, Task<int>>? body;
    static CancellationTokenSource? stop;
    static nint statusHandle;
    static Status status;
    static volatile bool stopRequested;
    static int exit = 1;

    internal static int Run(string serviceName, Func<CancellationToken, Task<int>> run)
    {
        name = serviceName; body = run;
        using var cancellation = new CancellationTokenSource(); stop = cancellation;
        try
        {
            var table = new[] { new ServiceEntry { Name = name, Main = main }, new ServiceEntry() };
            if (!StartServiceCtrlDispatcherW(table)) return 1;
            return exit;
        }
        finally { lock (sync) { stop = null; body = null; } }
    }

    static void OnMain(uint count, nint arguments)
    {
        try
        {
            statusHandle = RegisterServiceCtrlHandlerExW(name, controls, 0);
            if (statusHandle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            Publish(StartPending);
            Publish(Running);
            int result;
            try { result = body!(stop!.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (stopRequested) { result = 0; }
            exit = stopRequested ? 0 : result;
        }
        catch (Exception) { exit = 1; }
        finally { if (statusHandle != 0) Publish(Stopped, exit); }
    }

    static uint OnControl(uint control, uint eventType, nint eventData, nint context)
    {
        try
        {
            lock (sync)
            {
                if (control == Interrogate) { SetServiceStatus(statusHandle, ref status); return 0; }
                if (control is not (Stop or Shutdown)) return 120; // ERROR_CALL_NOT_IMPLEMENTED.
                if (status.State is Stopped or StopPending) return 0;
                stopRequested = true;
                Publish(StopPending);
                stop?.Cancel();
                return 0;
            }
        }
        catch (Exception) { return 1; }
    }

    static void Publish(uint state, int result = 0)
    {
        lock (sync)
        {
            status = new Status
            {
                Type = OwnProcess,
                State = state,
                Accepted = state == Running ? 1u | 4u : 0,
                Win32Exit = result == 0 ? 0u : 1066u, // ERROR_SERVICE_SPECIFIC_ERROR.
                SpecificExit = result == 0 ? 0u : (uint)Math.Max(1, result),
                Checkpoint = state is StartPending or StopPending ? status.Checkpoint + 1 : 0,
                WaitHint = state is StartPending or StopPending ? 30000u : 0
            };
            if (!SetServiceStatus(statusHandle, ref status) && state != Stopped)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    internal static string BinaryPath(string executable, string profileFile) => "\"" + executable + "\" --service-profile \"" + profileFile + "\"";

    internal static void DisableAndDelete(string profileFile, EnforcementProfile profile)
    {
        nint manager = OpenSCManagerW(null, null, 1); // SC_MANAGER_CONNECT.
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint service = 0, buffer = 0;
        try
        {
            service = OpenServiceW(manager, profile.ServiceName, 1 | 2 | 0x10000); // QUERY_CONFIG, CHANGE_CONFIG, DELETE.
            if (service == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error is 1060 or 1072) return; // Already absent or marked for deletion.
                throw new Win32Exception(error);
            }
            QueryServiceConfigW(service, 0, 0, out uint needed);
            if (Marshal.GetLastWin32Error() != 122 || needed < Marshal.SizeOf<Config>() || needed > 65536)
                throw new IOException("The registered service configuration cannot be verified.");
            buffer = Marshal.AllocHGlobal((int)needed);
            if (!QueryServiceConfigW(service, buffer, needed, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var config = Marshal.PtrToStructure<Config>(buffer);
            string? account = Marshal.PtrToStringUni(config.Account);
            if (config.Type != OwnProcess || !string.Equals(Marshal.PtrToStringUni(config.BinaryPath), BinaryPath(profile.EnforcerExecutable, profileFile), StringComparison.OrdinalIgnoreCase) ||
                !(string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase) || string.Equals(account, "NT AUTHORITY\\SYSTEM", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("The registered service does not match this installation.");
            // A previously queued SCM restart cannot be canceled. Disable before
            // deleting so a legitimately completed plan cannot be resurrected.
            if (!ChangeServiceConfigW(service, uint.MaxValue, 4, uint.MaxValue, null, null, 0, null, null, null, null))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!DeleteService(service) && Marshal.GetLastWin32Error() != 1072) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (buffer != 0) Marshal.FreeHGlobal(buffer);
            if (service != 0) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }
}
