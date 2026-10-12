using System.Diagnostics;
using System.Security.Cryptography;
using System.IO.Pipes;
using System.Text.Json;
using WishGuard;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length != 3) return 2;
        string app = Path.GetFullPath(args[0]), host = Path.GetFullPath(args[1]), root = Path.GetFullPath(args[2]);
        if (Directory.Exists(root)) throw new IOException("Use a fresh test directory.");
        Directory.CreateDirectory(root);
        string client = Path.Combine(root, "client"); Directory.CreateDirectory(client);
        foreach (string file in Directory.GetFiles(app)) File.Copy(file, Path.Combine(client, Path.GetFileName(file)));
        string exe = Path.Combine(client, "WishGuard.exe"), fake = Path.Combine(client, "WishGuardProbeGame.exe"), other = Path.Combine(client, "OtherProbeGame.exe");
        File.Copy(exe, fake); File.Copy(exe, other);
        string config = Path.Combine(root, "profile.json"), control = Path.Combine(root, "control.json");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = new EnforcementProfile {
            OwnerSid = EnforcementPaths.Owner, ClientDirectory = client, DataDirectory = Path.Combine(root, "data"),
            EnforcerExecutable = Path.Combine(host, "WishGuard.Enforcer.exe"), PipeName = "WishGuard-test-" + Guid.NewGuid().ToString("N"),
            GamePaths = [fake], TestOnly = true, PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            Policy = new GuardState { Committed = true }
        };
        foreach (string name in new[] { "WishGuard.exe", "WishGuard.dll", "WishGuard.deps.json", "WishGuard.runtimeconfig.json", "ocr-worker.ps1" })
            profile.ClientHashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(client, name))));
        EnforcementPaths.Save(config, profile);
        var checks = new List<object>(); var children = new List<Process>();
        void Check(string name, bool pass) { checks.Add(new { name, pass }); if (!pass) throw new Exception(name); }
        Process Start(string path, params string[] items)
        {
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string item in items) start.ArgumentList.Add(item);
            var child = Process.Start(start)!; children.Add(child); return child;
        }
        static async Task<bool> Until(Func<bool> ready, int ms = 8000)
        {
            var time = Stopwatch.StartNew();
            while (time.ElapsedMilliseconds < ms) { if (ready()) return true; await Task.Delay(100); }
            return ready();
        }
        async Task<ProtectionReply> Command(ProtectionRequest request)
        {
            if (File.Exists(control + ".reply")) File.Delete(control + ".reply");
            string next = control + ".tmp"; File.WriteAllText(next, JsonSerializer.Serialize(request)); File.Move(next, control, true);
            if (!await Until(() => File.Exists(control + ".reply"))) throw new IOException("Missing reply");
            return JsonSerializer.Deserialize<ProtectionReply>(File.ReadAllText(control + ".reply"))!;
        }
        int exit = 1;
        try
        {
            var supervisor = Start(profile.EnforcerExecutable, "--profile", config);
            var input = Start(profile.EnforcerExecutable, "--input-profile", config);
            var ui = Start(exe, "--protection-probe", config, "--control", control);
            Check("trusted client authenticates", await Until(() => File.Exists(control + ".ready")));
            Check("input compatibility authenticates client and rejects invalid window", (await Command(new("input-poll"))).Ok);
            using (var untrustedInput = new NamedPipeClientStream(".", profile.PipeName + "-Input-" + Process.GetCurrentProcess().SessionId, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                // The trusted probe disposes its connection for this authentication check.
                await Command(new("input-disconnect"));
                bool rejected = false;
                try
                {
                    using var timeout = new CancellationTokenSource(3000);
                    await untrustedInput.ConnectAsync(timeout.Token); await untrustedInput.WriteAsync(new byte[9], timeout.Token);
                    rejected = await untrustedInput.ReadAsync(new byte[9], timeout.Token) == 0;
                }
                catch (IOException) { rejected = true; }
                Check("untrusted client cannot use elevated window compatibility", rejected);
            }
            var game = Start(fake, "--sentinel"); await Task.Delay(4500);
            Check("healthy protection keeps game running beyond startup grace", !game.HasExited);
            var bystander = Start(other, "--sentinel");
            await Command(new("health", false));
            Check("unhealthy protection closes enrolled game", await Until(() => game.HasExited, 3000));
            Check("unrelated executable remains running", !bystander.HasExited);
            await Command(new("health", true));
            game = Start(fake, "--sentinel"); await Task.Delay(4000); ui.Kill(false);
            Check("forced client termination closes enrolled game", await Until(() => game.HasExited, 3000));
            using (var impostor = new EnforcementClient(profile))
            {
                bool rejected = false; try { await impostor.Send("health", true); } catch { rejected = true; }
                Check("untrusted client cannot report health", rejected);
            }
            File.Delete(control); File.Delete(control + ".ready");
            ui = Start(exe, "--protection-probe", config, "--control", control);
            Check("protection recovers after client restart", await Until(() => File.Exists(control + ".ready")));
            var requested = await Command(new("recover-request"));
            Check("recovery requires a full day", requested.Ok && requested.RecoveryAt - requested.RecoveryRequestedAt == 86400);
            Check("early recovery rejected", !(await Command(new("recover-finish"))).Ok);
            var cancelled = await Command(new("recover-cancel"));
            Check("recovery cancellation persists", cancelled.Ok && cancelled.RecoveryAt == null && JsonSerializer.Deserialize<EnforcementProfile>(File.ReadAllText(config))!.Policy.RecoveryAt == null);
            Check("invalid permanent pass rejected", !(await Command(new("unlock", Token: "invalid"))).Ok && !supervisor.HasExited);
            // Failing storage must not end enforcement or report success.
            Directory.CreateDirectory(config + ".tmp");
            Check("failed save rolls back recovery mutation", !(await Command(new("recover-request"))).Ok);
            Directory.Delete(config + ".tmp");
            Check("rollback preserves prior recovery state", (await Command(new("status"))).RecoveryAt == null);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var grant = new Grant("wishguard-v2", profile.Policy.InstallId, profile.Policy.PolicyId, profile.Policy.Nonce, "probe", now, now + 3600, 600, "permanent");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(grant);
            string token = JsonSerializer.Serialize(new Envelope(Rules.Encode(payload), Rules.Encode(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))));
            var unlocked = await Command(new("unlock", Token: token));
            Check("valid 600-pull pass acknowledged before shutdown", unlocked.Ok && unlocked.Completed && unlocked.PermanentPass == token);
            Check("permanent completion stops protection", await Until(() => supervisor.HasExited));
            Check("permanent completion stops input compatibility", await Until(() => input.HasExited, 3000));
            Check("completion is saved durably", JsonSerializer.Deserialize<EnforcementProfile>(File.ReadAllText(config))!.Completed);
            var restarted = Start(profile.EnforcerExecutable, "--profile", config);
            Check("completed policy cannot restart enforcement", await Until(() => restarted.HasExited, 3000));
            game = Start(fake, "--sentinel"); await Task.Delay(4500);
            Check("future games remain unaffected after completion", !game.HasExited);
            exit = 0;
        }
        catch (Exception e) { checks.Add(new { name = "integration error", pass = false, error = e.ToString() }); }
        finally
        {
            foreach (var process in children) { try { if (!process.HasExited) process.Kill(false); } catch { } process.Dispose(); }
            File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = exit == 0, checks }, new JsonSerializerOptions { WriteIndented = true }));
        }
        Console.WriteLine($"Protection integration: {(exit == 0 ? "PASS" : "FAIL")}; {checks.Count} checks. Report: {Path.Combine(root, "results.json")}");
        return exit;
    }
}
