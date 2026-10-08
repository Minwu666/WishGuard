using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace WishGuard;

public sealed record ResourceSnapshot(long Primogems, long Intertwined, long ObservedAt)
{ public long Pulls => Rules.Pulls(Primogems, Intertwined); }
public sealed class GuardState
{
    public int Schema { get; set; } = 2;
    public string InstallId { get; set; } = Guid.NewGuid().ToString("N");
    public int TargetPulls { get; set; } = 600;
    public string UnlockMode { get; set; } = "permanent";
    public bool Committed { get; set; }
    public bool StartWithWindows { get; set; } = true;
    public bool RecoveryFinished { get; set; }
    public long? RecoveryAt { get; set; }
    public long? RecoveryRequestedAt { get; set; }
    public long LastSeen { get; set; }
    public string Nonce { get; set; } = NewNonce();
    public string? PermanentPass { get; set; }
    public ResourceSnapshot? Resources { get; set; }
    public List<string> UsedPasses { get; set; } = new();
    public static string NewNonce() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    public string PolicyId => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"wishguard-v2|{InstallId}|600|intertwined+floor(primogems/160)|permanent")));
}
public sealed record ApprovalRequest(string Version, string InstallId, string PolicyId, string Nonce,
    int TargetPulls, string UnlockMode, long Primogems, long Intertwined, long ClaimedPulls,
    long ObservedAt, long RequestedAt, string Note);
public sealed record Grant(string Version, string InstallId, string PolicyId, string Nonce, string Id,
    long IssuedAt, long RedeemBefore, long VerifiedPulls, string UnlockMode);
public sealed record Envelope(string Payload, string Signature);
public static class Rules
{
    public static long Pulls(long primogems, long intertwined)
    {
        if (primogems < 0 || intertwined < 0 || primogems > 100_000_000 || intertwined > 1_000_000)
            throw new ArgumentOutOfRangeException("资源数量不合理。");
        return primogems / 160 + intertwined;
    }
    public static byte[] Decode(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/') + new string('=', (4 - s.Length % 4) % 4));
    public static string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Grant Validate(string token, string publicKey, GuardState s, long now, bool importing)
    {
        if (token.Length > 16000) throw new InvalidDataException("凭证太长。");
        var e = JsonSerializer.Deserialize<Envelope>(token) ?? throw new InvalidDataException("无效凭证。");
        byte[] payload = Decode(e.Payload), signature = Decode(e.Signature);
        using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
        if (!key.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("凭证签名无效。");
        var g = JsonSerializer.Deserialize<Grant>(payload) ?? throw new InvalidDataException("凭证内容无效。");
        if (g.Version != "wishguard-v2" || g.UnlockMode != "permanent" || g.InstallId != s.InstallId ||
            g.PolicyId != s.PolicyId || g.Nonce != s.Nonce || !s.Committed || g.VerifiedPulls < 600 || string.IsNullOrWhiteSpace(g.Id))
            throw new InvalidDataException("凭证不符合这份 600 抽永久解除计划。");
        if (g.RedeemBefore <= g.IssuedAt || g.RedeemBefore - g.IssuedAt > 7 * 86400)
            throw new InvalidDataException("凭证的导入期限无效。");
        if (importing && (g.IssuedAt > now + 60 || now >= g.RedeemBefore || s.UsedPasses.Contains(g.Id)))
            throw new InvalidDataException("凭证未生效、已过导入期限或已使用。");
        return g;
    }
    public static bool Permanent(GuardState s, string publicKey)
    {
        if (s.PermanentPass == null) return false;
        try { Validate(s.PermanentPass, publicKey, s, 0, false); return true; } catch { return false; }
    }
    public static bool Ended(GuardState s, string publicKey) => s.RecoveryFinished || Permanent(s, publicKey);
    public static bool ClockRolledBack(GuardState s, long now) => now + 120 < s.LastSeen;
    public static void RequestRecovery(GuardState s, long now)
    {
        if (!s.Committed || s.RecoveryFinished || s.RecoveryAt != null) return;
        s.RecoveryRequestedAt = now; s.RecoveryAt = now + 86400;
    }
    public static void CancelRecovery(GuardState s) { s.RecoveryAt = null; s.RecoveryRequestedAt = null; }
    public static bool CanFinishRecovery(GuardState s, long now) =>
        s.RecoveryAt is { } at && now >= at && !ClockRolledBack(s, now) && !s.RecoveryFinished;
}
public sealed class StateStore
{
    public string DirectoryPath { get; }
    public bool Fault { get; private set; }
    public bool Migrated { get; private set; }
    public string FaultMessage { get; private set; } = "";
    public GuardState State { get; private set; }
    readonly string path;
    public StateStore(string directory)
    {
        DirectoryPath = directory; Directory.CreateDirectory(directory); path = Path.Combine(directory, "state.json");
        try
        {
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                State = JsonSerializer.Deserialize<GuardState>(text) ?? throw new InvalidDataException();
                if (State.Schema == 1)
                {
                    if (State.TargetPulls != 300) throw new InvalidDataException();
                    File.WriteAllText(Path.Combine(directory, "state-v1-backup.json"), text);
                    State.Schema = 2; State.TargetPulls = 600; State.UnlockMode = "permanent";
                    State.PermanentPass = null; State.Nonce = GuardState.NewNonce(); Migrated = true;
                }
                if (State.Schema != 2 || State.TargetPulls != 600 || State.UnlockMode != "permanent" ||
                    State.InstallId.Length != 32 || State.Nonce.Length != 48 || State.UsedPasses == null)
                    throw new InvalidDataException();
                if (State.Resources != null) Rules.Pulls(State.Resources.Primogems, State.Resources.Intertwined);
                if (Migrated) Save();
            }
            else if (File.Exists(Path.Combine(directory, "initialized"))) throw new InvalidDataException();
            else State = new();
        }
        catch
        {
            State = new() { Committed = true }; Fault = true;
            FaultMessage = "守护记录无法读取，已按锁定处理。原文件保留，请联系你的核验人协助恢复。";
        }
    }
    public void Save()
    {
        if (Fault) throw new IOException(FaultMessage);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(State, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temp, path, true); File.WriteAllText(Path.Combine(DirectoryPath, "initialized"), "1");
    }
    public void Log(string type, string detail)
    {
        try
        {
            string log = Path.Combine(DirectoryPath, "events.jsonl");
            if (File.Exists(log) && new FileInfo(log).Length > 2_000_000) File.Move(log, log + ".previous", true);
            File.AppendAllText(log, JsonSerializer.Serialize(new { time = DateTimeOffset.Now, type, detail }) + Environment.NewLine);
        } catch { }
    }
}
