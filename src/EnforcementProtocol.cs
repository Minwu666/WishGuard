using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
namespace WishGuard;

public sealed class EnforcementProfile
{
    public string OwnerSid { get; set; } = "";
    public string ClientDirectory { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string EnforcerExecutable { get; set; } = "";
    public string PipeName { get; set; } = "";
    public string TaskName { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string[] GamePaths { get; set; } = [];
    public Dictionary<string, string> ClientHashes { get; set; } = new();
    public GuardState Policy { get; set; } = new();
    public bool Completed { get; set; }
    public bool TestOnly { get; set; }
}
public sealed record ProtectionRequest(string Command, bool Healthy = false, string? Token = null);
public sealed record ProtectionReply(bool Ok, bool Completed, long? RecoveryAt, long? RecoveryRequestedAt, bool RecoveryFinished, string Message,
    string? PermanentPass = null, string? TemporaryPass = null, long? TemporaryActivatedAt = null, long? TemporaryUntil = null,
    string[]? UsedPasses = null, long LastSeen = 0);
public static class EnforcementPaths
{
    public static string Owner => WindowsIdentity.GetCurrent().User!.Value;
    public static string Key(string sid) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))[..20];
    public static string Profile(string sid) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WishGuard", "Strong", Key(sid), "profile.json");
    public static EnforcementProfile? Current()
    {
        string path = Profile(Owner);
        if (!File.Exists(path)) return null;
        return Read(path);
    }
    public static EnforcementProfile Read(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<EnforcementProfile>(input) ?? throw new IOException("强制保护记录无法读取。");
            }
            catch (IOException) when (attempt < 2) { Thread.Sleep(20); }
        }
    }
    public static void Save(string path, EnforcementProfile profile)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(path)) File.Replace(temp, path, null);
        else File.Move(temp, path);
    }
    public static void Apply(StateStore store, EnforcementProfile profile)
    {
        if (!string.Equals(Path.GetFullPath(store.DirectoryPath), Path.GetFullPath(profile.DataDirectory), StringComparison.OrdinalIgnoreCase) || store.Fault)
            throw new IOException("强制模式的计划记录不匹配，请联系核验人修复。");
        var s = store.State; var p = profile.Policy;
        bool changed = s.InstallId != p.InstallId || s.Nonce != p.Nonce || s.Committed != p.Committed ||
            !s.StartWithWindows || s.RecoveryAt != p.RecoveryAt || s.RecoveryRequestedAt != p.RecoveryRequestedAt ||
            s.RecoveryFinished != p.RecoveryFinished || s.PermanentPass != p.PermanentPass ||
            s.TemporaryPass != p.TemporaryPass || s.TemporaryActivatedAt != p.TemporaryActivatedAt || s.TemporaryUntil != p.TemporaryUntil ||
            s.LastSeen < p.LastSeen || !s.UsedPasses.SequenceEqual(p.UsedPasses);
        s.InstallId = p.InstallId; s.Nonce = p.Nonce; s.Committed = p.Committed; s.StartWithWindows = true;
        s.RecoveryAt = p.RecoveryAt; s.RecoveryRequestedAt = p.RecoveryRequestedAt;
        s.RecoveryFinished = p.RecoveryFinished; s.PermanentPass = p.PermanentPass;
        s.TemporaryPass = p.TemporaryPass; s.TemporaryActivatedAt = p.TemporaryActivatedAt; s.TemporaryUntil = p.TemporaryUntil;
        s.LastSeen = Math.Max(s.LastSeen, p.LastSeen);
        s.UsedPasses = new(p.UsedPasses);
        if (changed) store.Save();
    }
}
public static class ProcessIdentity
{
    [DllImport("kernel32.dll")] static extern nint OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll")] static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll")] static extern bool GetTokenInformation(nint token, int informationClass, out int information, int length, out int required);
    [DllImport("kernel32.dll")] public static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out int id);
    [DllImport("kernel32.dll")] public static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out int id);
    [DllImport("kernel32.dll")] public static extern bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint id);
    public static string? PathOf(int pid)
    {
        nint process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try { var path = new StringBuilder(32768); uint length = (uint)path.Capacity; return QueryFullProcessImageName(process, 0, path, ref length) ? path.ToString() : null; }
        finally { CloseHandle(process); }
    }
    public static bool Elevated(int pid)
    {
        nint process = OpenProcess(0x1000, false, pid);
        if (process == 0) return false;
        try
        {
            if (!OpenProcessToken(process, 8, out var token)) return false;
            try { return GetTokenInformation(token, 20, out int elevated, sizeof(int), out _) && elevated != 0; }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
    public static (string Path, string Sid)? Read(int pid)
    {
        nint process = OpenProcess(0x1000, false, pid);
        if (process == 0) return null;
        try
        {
            var path = new StringBuilder(32768); uint length = (uint)path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length) || !OpenProcessToken(process, 8, out var token)) return null;
            try { using var identity = new WindowsIdentity(token); return (path.ToString(), identity.User!.Value); }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
    public static bool TrustedClient(int pid, EnforcementProfile profile)
    {
        var identity = Read(pid);
        if (identity == null || identity.Value.Sid != profile.OwnerSid ||
            !string.Equals(identity.Value.Path, Path.Combine(profile.ClientDirectory, "WishGuard.exe"), StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var entry in profile.ClientHashes)
        {
            if (Path.GetFileName(entry.Key) != entry.Key) return false;
            using var input = File.OpenRead(Path.Combine(profile.ClientDirectory, entry.Key));
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(input)), entry.Value, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return profile.ClientHashes.Count >= 4;
    }
    public static bool IsTarget(int pid, EnforcementProfile profile)
    {
        var identity = Read(pid);
        return identity != null && identity.Value.Sid == profile.OwnerSid && profile.GamePaths.Any(path =>
            string.Equals(path, identity.Value.Path, StringComparison.OrdinalIgnoreCase));
    }
}
