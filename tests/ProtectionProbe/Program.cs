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
        string Sign(Grant grant)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(grant);
            return JsonSerializer.Serialize(new Envelope(Rules.Encode(payload), Rules.Encode(key.SignData(payload,
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))));
        }
        EnforcementProfile Saved() => EnforcementPaths.Read(config);
        static void Stop(Process process)
        {
            if (!process.HasExited) process.Kill(false);
            if (!process.WaitForExit(3000)) throw new IOException("Test child did not stop.");
        }
        void ClearControl()
        {
            foreach (string suffix in new[] { "", ".ready", ".reply", ".error" })
                if (File.Exists(control + suffix)) File.Delete(control + suffix);
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
            var faultRestartTime = Stopwatch.StartNew();
            game = Start(fake, "--sentinel");
            Check("fresh enrolled game is closed while protection is still unavailable",
                await Until(() => game.HasExited, 5000) && faultRestartTime.ElapsedMilliseconds <= 5500 && game.ExitCode != 0);
            Check("supervisor remains active after rejecting a fresh game", !supervisor.HasExited);
            using (var impostor = new EnforcementClient(profile))
            {
                bool rejected = false; try { await impostor.Send("health", true); } catch { rejected = true; }
                Check("untrusted client cannot report health", rejected);
            }
            File.Delete(control); File.Delete(control + ".ready");
            ui = Start(exe, "--protection-probe", config, "--control", control);
            Check("protection recovers after client restart", await Until(() => File.Exists(control + ".ready")));
            game = Start(fake, "--sentinel"); await Task.Delay(4500);
            Check("fresh enrolled game survives startup grace after trusted health recovers", !game.HasExited);
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
            const string temporaryId = "probe-temporary";
            string temporaryToken = Sign(new Grant("wishguard-v2", profile.Policy.InstallId, profile.Policy.PolicyId,
                profile.Policy.Nonce, temporaryId, now, now + 86400, 0, "temporary", DurationSeconds: 1800,
                ApprovalReason: "Isolated integration approval for a new character."));
            Check("temporary pass cannot use permanent completion endpoint", !(await Command(new("unlock", Token: temporaryToken))).Ok);
            Check("wrong completion endpoint preserves locked 600-pull plan", !Saved().Completed && Saved().Policy.PermanentPass == null &&
                Saved().Policy.TargetPulls == 600 && Saved().Policy.UnlockMode == "permanent");

            // A failed durable save must not consume the signed token or permit a game.
            Directory.CreateDirectory(config + ".tmp");
            Check("failed save rejects temporary activation", !(await Command(new("temporary-unlock", Token: temporaryToken))).Ok);
            Directory.Delete(config + ".tmp");
            var afterFailedTemporary = await Command(new("status"));
            Check("failed temporary save rolls back receipt and used id", afterFailedTemporary.TemporaryPass == null &&
                afterFailedTemporary.TemporaryActivatedAt == null && afterFailedTemporary.TemporaryUntil == null &&
                afterFailedTemporary.UsedPasses?.Contains(temporaryId) != true && !afterFailedTemporary.Completed);
            Check("failed temporary save leaves persisted plan locked", Saved().Policy.TemporaryPass == null &&
                !Saved().Policy.UsedPasses.Contains(temporaryId) && !Saved().Completed);
            await Command(new("health", false));
            Check("failed temporary save does not grant unavailable protection", await Until(() => game.HasExited, 3000));
            game = Start(fake, "--sentinel");
            Check("failed temporary save cannot permit a fresh game", await Until(() => game.HasExited, 5000));

            long beforeActivation = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var temporary = await Command(new("temporary-unlock", Token: temporaryToken));
            Check("same unconsumed temporary pass activates after storage recovers", temporary.Ok && temporary.TemporaryPass == temporaryToken &&
                temporary.TemporaryActivatedAt is { } activated && activated >= beforeActivation &&
                temporary.TemporaryUntil == activated + 1800 && temporary.LastSeen >= activated &&
                temporary.UsedPasses?.Contains(temporaryId) == true);
            Check("temporary activation preserves permanent plan and supervisor", !temporary.Completed && !supervisor.HasExited && !input.HasExited &&
                Saved().Policy.Committed && Saved().Policy.TargetPulls == 600 && Saved().Policy.UnlockMode == "permanent" &&
                Saved().Policy.PermanentPass == null && !Saved().Completed);
            Check("temporary receipt and consumption are saved durably", Saved().Policy.TemporaryPass == temporaryToken &&
                Saved().Policy.TemporaryActivatedAt == temporary.TemporaryActivatedAt &&
                Saved().Policy.TemporaryUntil == temporary.TemporaryUntil && Saved().Policy.UsedPasses.Contains(temporaryId));
            game = Start(fake, "--sentinel"); await Task.Delay(4500);
            Check("active temporary receipt permits game without healthy client", !game.HasExited);

            // Switch commands so the trusted probe processes a duplicate activation.
            await Command(new("status"));
            Check("temporary pass is rejected on second activation", !(await Command(new("temporary-unlock", Token: temporaryToken))).Ok);
            var afterDuplicate = await Command(new("status"));
            Check("duplicate temporary activation neither extends time nor consumes again",
                afterDuplicate.TemporaryActivatedAt == temporary.TemporaryActivatedAt && afterDuplicate.TemporaryUntil == temporary.TemporaryUntil &&
                afterDuplicate.UsedPasses?.Count(id => id == temporaryId) == 1 && !afterDuplicate.Completed);

            // Restart only the isolated processes and reload the persisted receipt.
            Stop(ui); Stop(input); Stop(supervisor); ClearControl();
            supervisor = Start(profile.EnforcerExecutable, "--profile", config);
            input = Start(profile.EnforcerExecutable, "--input-profile", config);
            ui = Start(exe, "--protection-probe", config, "--control", control);
            Check("temporary plan reconnects after supervisor and client restart", await Until(() => File.Exists(control + ".ready")));
            var restartedTemporary = await Command(new("health", false));
            Check("restart reloads original receipt without restarting thirty minutes", restartedTemporary.TemporaryPass == temporaryToken &&
                restartedTemporary.TemporaryActivatedAt == temporary.TemporaryActivatedAt && restartedTemporary.TemporaryUntil == temporary.TemporaryUntil &&
                restartedTemporary.UsedPasses?.Contains(temporaryId) == true && !restartedTemporary.Completed);
            Check("used temporary id remains rejected after restart", !(await Command(new("temporary-unlock", Token: temporaryToken))).Ok);
            var afterRestartDuplicate = await Command(new("status"));
            Check("restart replay preserves original deadline", afterRestartDuplicate.TemporaryUntil == temporary.TemporaryUntil &&
                afterRestartDuplicate.TemporaryActivatedAt == temporary.TemporaryActivatedAt &&
                afterRestartDuplicate.UsedPasses?.Count(id => id == temporaryId) == 1);
            Stop(game); game = Start(fake, "--sentinel");
            await Task.Delay(4500);
            Check("restarted supervisor honors remaining temporary time for a fresh game", !game.HasExited && !supervisor.HasExited && !input.HasExited);

            // Simulate an expired, previously activated receipt in a separate profile.
            // Never change the machine clock or wait thirty minutes in this probe.
            Stop(game);
            var expiredProfile = JsonSerializer.Deserialize<EnforcementProfile>(JsonSerializer.Serialize(Saved()))!;
            expiredProfile.PipeName = "WishGuard-test-expired-" + Guid.NewGuid().ToString("N");
            expiredProfile.DataDirectory = Path.Combine(root, "expired-data");
            long expiredNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            const string expiredId = "probe-expired-temporary";
            string expiredToken = Sign(new Grant("wishguard-v2", expiredProfile.Policy.InstallId, expiredProfile.Policy.PolicyId,
                expiredProfile.Policy.Nonce, expiredId, expiredNow - 1900, expiredNow + 84500, 0, "temporary", DurationSeconds: 1800,
                ApprovalReason: "Isolated expired receipt fixture."));
            expiredProfile.Policy.TemporaryPass = expiredToken;
            expiredProfile.Policy.TemporaryActivatedAt = expiredNow - 1900;
            expiredProfile.Policy.TemporaryUntil = expiredNow - 100;
            expiredProfile.Policy.LastSeen = expiredNow - 1;
            expiredProfile.Policy.UsedPasses.Add(expiredId);
            string expiredConfig = Path.Combine(root, "expired-profile.json");
            EnforcementPaths.Save(expiredConfig, expiredProfile);
            var expiredSupervisor = Start(expiredProfile.EnforcerExecutable, "--profile", expiredConfig);
            await Task.Delay(1000); Stop(expiredSupervisor);
            expiredSupervisor = Start(expiredProfile.EnforcerExecutable, "--profile", expiredConfig);
            game = Start(fake, "--sentinel");
            Check("expired persisted temporary receipt cannot permit game after restart", await Until(() => game.HasExited, 5000));
            Check("temporary expiration keeps supervisor and 600-pull plan enrolled", !expiredSupervisor.HasExited &&
                !EnforcementPaths.Read(expiredConfig).Completed && EnforcementPaths.Read(expiredConfig).Policy.TargetPulls == 600 &&
                EnforcementPaths.Read(expiredConfig).Policy.TemporaryUntil == expiredNow - 100);
            Stop(expiredSupervisor);

            var grant = new Grant("wishguard-v2", profile.Policy.InstallId, profile.Policy.PolicyId, profile.Policy.Nonce, "probe", now, now + 3600, 600, "permanent");
            string token = Sign(grant);
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
