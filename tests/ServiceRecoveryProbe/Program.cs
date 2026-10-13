using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using WishGuard;

// Administrator-only, disposable SCM/component fixture. The watcher task is a
// trusted heartbeat probe, so this does not claim to exercise OCR or real input.
internal static class Program
{
    sealed record Stamp(int Pid, long Started);
    sealed record ServiceStatus(bool Exists, uint State, int Pid, uint ExitCode);
    sealed record CheckResult(string Name, bool Pass, object? Detail = null);
    sealed record TaskSpec(string Name, string Source, string Executable, string Arguments, string Directory, int RunLevel);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    static async Task<int> Main(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: ServiceRecoveryProbe <test-client-output> <enforcer-output> <fresh-root> (administrator)");
            return 2;
        }
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { Console.Error.WriteLine("Run this isolated fixture as administrator."); return 2; }
        string app = Path.GetFullPath(args[0]), host = Path.GetFullPath(args[1]), root = Path.GetFullPath(args[2]);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Use a fresh test directory.");
        RejectReparseAncestors(root); RejectReparseAncestors(app); RejectReparseAncestors(host);
        Directory.CreateDirectory(root);
        string client = Path.Combine(root, "Client"), enforcer = Path.Combine(root, "Enforcer"), data = Path.Combine(root, "data");
        Directory.CreateDirectory(client); Directory.CreateDirectory(enforcer); Directory.CreateDirectory(data);
        foreach (string name in new[] { "WishGuard.exe", "WishGuard.dll", "WishGuard.deps.json", "WishGuard.runtimeconfig.json", "ocr-worker.ps1" })
            File.Copy(Path.Combine(app, name), Path.Combine(client, name));
        foreach (string name in new[] { "WishGuard.Enforcer.exe", "WishGuard.Enforcer.dll", "WishGuard.Enforcer.deps.json", "WishGuard.Enforcer.runtimeconfig.json" })
            File.Copy(Path.Combine(host, name), Path.Combine(enforcer, name));
        string exe = Path.Combine(client, "WishGuard.exe"), fake = Path.Combine(client, "WishGuardProbeGame.exe");
        string metadata = Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(client, "WishGuard.dll")));
        if (!metadata.Contains("ProtectionProbe", StringComparison.Ordinal) || !metadata.Contains("ReturnProbe", StringComparison.Ordinal))
            throw new IOException("The client must be an isolated test build with both probe entry points.");
        File.Copy(exe, fake);
        string config = Path.Combine(root, "profile.json"), control = Path.Combine(data, "control.json");
        string taskBase = "WishGuard-Test-" + Guid.NewGuid().ToString("N"), serviceName = taskBase + "-Service";
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var profile = new EnforcementProfile
        {
            OwnerSid = identity.User!.Value, ClientDirectory = client, DataDirectory = data,
            EnforcerExecutable = Path.Combine(enforcer, "WishGuard.Enforcer.exe"),
            PipeName = "WishGuard-test-" + Guid.NewGuid().ToString("N"),
            TaskName = taskBase, ServiceName = serviceName, GamePaths = [fake], TestOnly = true,
            PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), Policy = new GuardState { Committed = true }
        };
        foreach (string name in new[] { "WishGuard.exe", "WishGuard.dll", "WishGuard.deps.json", "WishGuard.runtimeconfig.json", "ocr-worker.ps1" })
            profile.ClientHashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(client, name))));
        File.WriteAllText(Path.Combine(client, "authority-public.txt"), profile.PublicKey);
        EnforcementPaths.Save(config, profile);
        string watchName = "WishGuard-" + EnforcementPaths.Key(profile.OwnerSid + "|" + Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant());
        var watch = new TaskSpec(watchName, "WishGuard game watcher", exe,
            $"--protection-probe \"{config}\" --control \"{control}\"", client, 0);
        var input = new TaskSpec(taskBase + "-Input", "WishGuard input compatibility", profile.EnforcerExecutable,
            $"--input-profile \"{config}\"", enforcer, 1);
        var checks = new List<CheckResult>(); var observations = new List<object>(); var cleanup = new List<string>();
        var registered = new List<TaskSpec>(); var fakeChildren = new List<Process>();
        var observed = new Dictionary<Stamp, string>();
        dynamic? scheduler = null, folder = null;
        bool serviceCreated = false; int serviceStarts = 0, componentStarts = 0, result = 1;
        void Check(string name, bool pass, object? detail = null)
        { checks.Add(new(name, pass, detail)); if (!pass) throw new InvalidOperationException(name); }
        Stamp? Find(string executable)
        {
            Stamp? found = null;
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable)))
                using (process)
                {
                    try
                    {
                        if (process.HasExited || process.SessionId != Process.GetCurrentProcess().SessionId) continue;
                        var who = ProcessIdentity.Read(process.Id);
                        if (who == null || who.Value.Sid != profile.OwnerSid || !SamePath(who.Value.Path, executable)) continue;
                        var stamp = new Stamp(process.Id, process.StartTime.ToUniversalTime().Ticks);
                        observed[stamp] = executable;
                        if (found != null) throw new InvalidOperationException("Duplicate fixture component: " + executable);
                        found = stamp;
                    }
                    catch (InvalidOperationException) when (process.HasExited) { }
                }
            return found;
        }
        ServiceStatus Service()
        {
            var status = Scm.Query(serviceName);
            if (status.State == 4 && status.Pid > 0)
            {
                var who = ProcessIdentity.Read(status.Pid);
                // SCM can briefly retain RUNNING plus the old PID after a
                // crash; this is a transition, not a recovered generation.
                if (who == null) return status with { State = 2, Pid = 0 };
                if (who.Value.Sid != "S-1-5-18" || !SamePath(who.Value.Path, profile.EnforcerExecutable))
                    throw new InvalidOperationException("The isolated service identity does not match its copied executable.");
                try
                {
                    using var p = Process.GetProcessById(status.Pid);
                    if (p.HasExited) return status with { State = 2, Pid = 0 };
                    observed[new(p.Id, p.StartTime.ToUniversalTime().Ticks)] = profile.EnforcerExecutable;
                }
                catch (ArgumentException) { return status with { State = 2, Pid = 0 }; }
                catch (InvalidOperationException) { return status with { State = 2, Pid = 0 }; }
            }
            return status;
        }
        Stamp RunningService()
        {
            var state = Service();
            if (!state.Exists || state.State != 4 || state.Pid == 0) throw new InvalidOperationException("Fixture service is not running.");
            using var process = Process.GetProcessById(state.Pid);
            return new(process.Id, process.StartTime.ToUniversalTime().Ticks);
        }
        void Kill(Stamp process, string executable, bool system = false)
        {
            AssertInside(root, executable);
            using var live = Process.GetProcessById(process.Pid);
            var who = ProcessIdentity.Read(process.Pid);
            if (live.HasExited || live.StartTime.ToUniversalTime().Ticks != process.Started || who == null ||
                who.Value.Sid != (system ? "S-1-5-18" : profile.OwnerSid) || !SamePath(who.Value.Path, executable))
                throw new InvalidOperationException("A fixture process changed identity before termination.");
            live.Kill(false); // No tree termination and no process outside this fresh directory.
        }
        bool ReadyAfter(DateTime since) => TryRead(control + ".ready") != null && File.GetLastWriteTimeUtc(control + ".ready") >= since;
        async Task<ProtectionReply> Command(ProtectionRequest request)
        {
            string reply = control + ".reply";
            if (File.Exists(reply)) File.Delete(reply);
            // Harmless whitespace makes repeated requests distinct to the probe.
            AtomicWrite(control, JsonSerializer.Serialize(request) + "\n" + new string(' ', RandomNumberGenerator.GetInt32(1, 20000)));
            ProtectionReply? response = null;
            if (!await Until(() =>
            {
                string? text = TryRead(reply);
                if (text == null) return false;
                try { response = JsonSerializer.Deserialize<ProtectionReply>(text); return response != null; }
                catch (JsonException) { return false; }
            })) throw new IOException("The trusted component did not return a reply.");
            return response!;
        }
        async Task VerifyReady(string label, DateTime since)
        {
            Check(label + ": service automatically reaches running state", await Until(() => Service() is { State: 4, Pid: > 0 }));
            Check(label + ": registered watcher and input tasks automatically have live processes",
                await Until(() => Find(exe) != null && Find(profile.EnforcerExecutable) != null && ReadyAfter(since)));
            // The heartbeat fixture does not keep polling input like the real
            // client. Its idle pipe expires after one second, so exercise the
            // normal reconnect on a subsequent poll, with a bounded deadline.
            ProtectionReply inputReply = await Command(new("input-poll"));
            for (int attempt = 0; !inputReply.Ok && attempt < 20; attempt++)
            {
                await Task.Delay(100);
                inputReply = await Command(new("input-poll"));
            }
            Check(label + ": recovered input pipe authenticates the trusted client", inputReply.Ok, inputReply.Message);
            Check(label + ": restored tasks retain their exact identities", Matches(folder!, watch, profile.OwnerSid) && Matches(folder!, input, profile.OwnerSid));
        }
        async Task FreshGame(string label)
        {
            string scene = Path.Combine(data, "scene-" + Guid.NewGuid().ToString("N") + ".txt"); AtomicWrite(scene, "normal");
            var start = new ProcessStartInfo(fake) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--return-demo"); start.ArgumentList.Add(scene);
            var game = Process.Start(start) ?? throw new IOException("The synthetic game could not start."); fakeChildren.Add(game);
            Check(label + ": a fresh controllable fake game survives the full startup grace",
                await Hold(() => !game.HasExited && Service() is { State: 4, Pid: > 0 } && Find(exe) != null && Find(profile.EnforcerExecutable) != null, 4500));
            observations.Add(new { label, fakePid = game.Id, durationMs = 4500, naturalTimeout = false });
            if (!game.HasExited) game.Kill(false);
            await game.WaitForExitAsync();
        }
        try
        {
            scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!; scheduler.Connect(); folder = scheduler.GetFolder("\\");
            foreach (var spec in new[] { watch, input })
            {
                if (TaskExists(folder, spec.Name)) throw new IOException("The fresh fixture task name already exists.");
                Register(scheduler, folder, spec, profile.OwnerSid); registered.Add(spec);
            }
            Scm.CreateAndConfigure(serviceName, $"\"{profile.EnforcerExecutable}\" --service-profile \"{config}\""); serviceCreated = true;
            DateTime initial = DateTime.UtcNow;
            Scm.Start(serviceName); serviceStarts++;
            await VerifyReady("initial SCM launch", initial);
            await FreshGame("initial healthy chain");
            for (int failure = 1; failure <= 4; failure++)
            {
                var previous = RunningService(); DateTime fault = DateTime.UtcNow;
                Kill(previous, profile.EnforcerExecutable, true);
                Check($"SCM failure {failure}: a different service generation automatically restarts", await Until(() =>
                {
                    var state = Service();
                    return state is { State: 4, Pid: > 0 } && RunningService() != previous;
                }, 12000));
                observations.Add(new { failure, previous, recovered = RunningService() });
                await VerifyReady($"SCM failure {failure}", fault);
                await FreshGame($"SCM failure {failure}");
            }
            var watchBefore = Find(exe)!; var inputBefore = Find(profile.EnforcerExecutable)!; DateTime inputFault = DateTime.UtcNow;
            Kill(inputBefore, profile.EnforcerExecutable);
            Check("input-only failure automatically restores a new input generation", await Until(() => Find(profile.EnforcerExecutable) is { } now && now != inputBefore));
            Check("input-only recovery preserves the healthy watcher generation", Find(exe) == watchBefore);
            await VerifyReady("input-only recovery", inputFault); await FreshGame("input-only recovery");
            watchBefore = Find(exe)!; inputBefore = Find(profile.EnforcerExecutable)!; DateTime watchFault = DateTime.UtcNow;
            Kill(watchBefore, exe);
            Check("watcher-only failure automatically restores a new trusted client generation", await Until(() => Find(exe) is { } now && now != watchBefore));
            Check("watcher-only recovery preserves the healthy input generation", Find(profile.EnforcerExecutable) == inputBefore);
            await VerifyReady("watcher-only recovery", watchFault); await FreshGame("watcher-only recovery");
            watchBefore = Find(exe)!; inputBefore = Find(profile.EnforcerExecutable)!; var serviceBefore = RunningService(); DateTime allFault = DateTime.UtcNow;
            Kill(inputBefore, profile.EnforcerExecutable); Kill(watchBefore, exe); Kill(serviceBefore, profile.EnforcerExecutable, true);
            Check("combined failure automatically restores every process generation", await Until(() =>
                Service() is { State: 4, Pid: > 0 } && RunningService() != serviceBefore &&
                Find(exe) is { } newWatch && newWatch != watchBefore && Find(profile.EnforcerExecutable) is { } newInput && newInput != inputBefore, 12000));
            await VerifyReady("combined recovery", allFault); await FreshGame("combined recovery");
            Check("watcher identity is verified before the incompatible-definition fixture", Matches(folder, watch, profile.OwnerSid));
            watchBefore = Find(exe)!;
            var incompatibleWatch = watch with { Source = "WishGuard isolated incompatible fixture" };
            Register(scheduler, folder, incompatibleWatch, profile.OwnerSid, 6);
            registered.Remove(watch); registered.Add(incompatibleWatch);
            if (Find(exe) == watchBefore) Kill(watchBefore, exe);
            Check("the incompatible watcher becomes inactive", await Until(() => Find(exe) == null && TaskInstanceCount(folder, incompatibleWatch.Name) == 0));
            Check("the service refuses to launch a watcher whose Source no longer matches", await Hold(() =>
                Service() is { State: 4, Pid: > 0 } && Find(exe) == null && TaskInstanceCount(folder, incompatibleWatch.Name) == 0, 5000));
            string rejectedScene = Path.Combine(data, "rejected-scene.txt"); AtomicWrite(rejectedScene, "normal");
            var rejectedStart = new ProcessStartInfo(fake) { UseShellExecute = false, CreateNoWindow = true };
            rejectedStart.ArgumentList.Add("--return-demo"); rejectedStart.ArgumentList.Add(rejectedScene);
            var rejectedTimer = Stopwatch.StartNew();
            var rejectedGame = Process.Start(rejectedStart)!; fakeChildren.Add(rejectedGame);
            Check("a fresh fake is closed while the only watcher definition is incompatible", await Until(() => rejectedGame.HasExited, 5000) &&
                rejectedTimer.ElapsedMilliseconds <= 5500 && rejectedGame.ExitCode != 0);
            Check("only the fixture-owned incompatible definition is restored", Matches(folder, incompatibleWatch, profile.OwnerSid));
            Register(scheduler, folder, watch, profile.OwnerSid, 6);
            registered.Remove(incompatibleWatch); registered.Add(watch);
            DateTime restored = DateTime.UtcNow;
            Check("the corrected watcher definition is automatically relaunched", await Until(() => Find(exe) is { } now && now != watchBefore));
            await VerifyReady("corrected task identity", restored); await FreshGame("corrected task identity");
            Check("the fixture issued only one initial SCM start and no component starts", serviceStarts == 1 && componentStarts == 0);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var grant = new Grant("wishguard-v2", profile.Policy.InstallId, profile.Policy.PolicyId, profile.Policy.Nonce,
                "isolated-service-completion", now, now + 3600, 600, "permanent");
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(grant);
            string token = JsonSerializer.Serialize(new Envelope(Rules.Encode(payload), Rules.Encode(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))));
            var unlocked = await Command(new("unlock", Token: token));
            Check("temporary authority legitimately completes the isolated plan", unlocked.Ok && unlocked.Completed && unlocked.PermanentPass == token);
            Check("completion is durably saved", EnforcementPaths.Read(config).Completed);
            Check("completed service removes itself and both exact tasks without fixture cleanup", await Until(() =>
                !Service().Exists && !TaskExists(folder, watch.Name) && !TaskExists(folder, input.Name) && Find(exe) == null && Find(profile.EnforcerExecutable) == null));
            string completedScene = Path.Combine(data, "completed-scene.txt"); AtomicWrite(completedScene, "normal");
            var completedStart = new ProcessStartInfo(fake) { UseShellExecute = false, CreateNoWindow = true };
            completedStart.ArgumentList.Add("--return-demo"); completedStart.ArgumentList.Add(completedScene);
            var completedGame = Process.Start(completedStart)!; fakeChildren.Add(completedGame);
            Check("completed service and tasks never revive beyond recovery delay plus five seconds; a new fake remains running",
                await Hold(() => !Service().Exists && !TaskExists(folder, watch.Name) && !TaskExists(folder, input.Name) &&
                    Find(exe) == null && Find(profile.EnforcerExecutable) == null && !completedGame.HasExited, 7500));
            result = 0;
        }
        catch (Exception error) { checks.Add(new("integration error", false, error.ToString())); }
        finally
        {
            // These operations begin only after all no-revival assertions. No
            // files are recursively removed; private artifacts remain for review.
            if (serviceCreated) try { Scm.Cleanup(serviceName); } catch (Exception e) { cleanup.Add("service: " + e.Message); }
            if (folder != null)
                foreach (var spec in registered)
                    try
                    {
                        if (!TaskExists(folder, spec.Name)) continue;
                        if (!Matches(folder, spec, profile.OwnerSid)) throw new IOException("Fixture task identity changed; cleanup refused.");
                        dynamic task = folder.GetTask(spec.Name); try { task.Stop(0); } finally { Marshal.FinalReleaseComObject(task); }
                        folder.DeleteTask(spec.Name, 0);
                    }
                    catch (Exception e) { cleanup.Add("task " + spec.Name + ": " + e.Message); }
            foreach (var process in fakeChildren)
            { try { if (!process.HasExited) { AssertInside(root, ProcessIdentity.PathOf(process.Id)!); process.Kill(false); } } catch (Exception e) { cleanup.Add("fake: " + e.Message); } process.Dispose(); }
            try
            {
                foreach (var path in new[] { exe, profile.EnforcerExecutable })
                {
                    var stamp = Find(path);
                    if (stamp != null) Kill(stamp, path);
                }
                foreach (var entry in observed)
                {
                    try
                    {
                        using var process = Process.GetProcessById(entry.Key.Pid);
                        if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == entry.Key.Started && process.SessionId == 0)
                            Kill(entry.Key, entry.Value, true);
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                }
            }
            catch (Exception e) { cleanup.Add("component: " + e.Message); }
            if (folder != null) Marshal.FinalReleaseComObject(folder);
            if (scheduler != null) Marshal.FinalReleaseComObject(scheduler);
            if (cleanup.Count != 0) { checks.Add(new("precise fixture cleanup", false, cleanup)); result = 1; }
            AtomicWrite(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new
            { passed = result == 0, scope = "isolated SCM and heartbeat/input component recovery; no OCR or real game", serviceName, watchName, inputName = input.Name,
                serviceStarts, componentStarts, checks, observations, cleanup }, Json));
        }
        Console.WriteLine($"Service recovery: {(result == 0 ? "PASS" : "FAIL")}; {checks.Count} checks. Private report: {Path.Combine(root, "results.json")}");
        return result;
    }

    static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    static void AssertInside(string root, string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Fixture target escaped the fresh test directory.");
    }
    static void RejectReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Fixture paths cannot contain reparse points.");
    }
    static string? TryRead(string path)
    {
        try { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); using var reader = new StreamReader(file); return reader.ReadToEnd(); }
        catch (IOException) { return null; }
    }
    static void AtomicWrite(string path, string text)
    {
        for (int attempt = 0; ; attempt++)
            try { string temp = path + ".probe-next"; File.WriteAllText(temp, text, new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); return; }
            catch (IOException) when (attempt < 2) { Thread.Sleep(20); }
    }
    static async Task<bool> Until(Func<bool> ready, int timeout = 10000)
    {
        var time = Stopwatch.StartNew();
        do { if (ready()) return true; await Task.Delay(100); } while (time.ElapsedMilliseconds < timeout);
        return false;
    }
    static async Task<bool> Hold(Func<bool> condition, int duration)
    {
        var time = Stopwatch.StartNew();
        do { if (!condition()) return false; await Task.Delay(100); } while (time.ElapsedMilliseconds < duration);
        return condition();
    }
    static bool TaskExists(dynamic folder, string name)
    {
        try { object task = folder.GetTask(name); Marshal.FinalReleaseComObject(task); return true; }
        catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { return false; }
    }
    static int TaskInstanceCount(dynamic folder, string name)
    {
        dynamic task = folder.GetTask(name);
        try { dynamic instances = task.GetInstances(0); try { return (int)instances.Count; } finally { Marshal.FinalReleaseComObject(instances); } }
        finally { Marshal.FinalReleaseComObject(task); }
    }
    static bool Matches(dynamic folder, TaskSpec spec, string owner)
    {
        dynamic task = folder.GetTask(spec.Name);
        try
        {
            // COM Principal.UserId resolves the stored SID to a display name.
            // Compare the canonical persisted XML, as the production verifier does.
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var definition = XDocument.Parse((string)task.Xml).Root!;
            var principals = definition.Element(ns + "Principals")!.Elements(ns + "Principal").ToArray();
            var actions = definition.Element(ns + "Actions")!.Elements().ToArray();
            if (principals.Length != 1 || actions.Length != 1 || actions[0].Name != ns + "Exec") return false;
            var principal = principals[0]; var action = actions[0];
            return (string?)definition.Element(ns + "RegistrationInfo")?.Element(ns + "Source") == spec.Source &&
                (string?)principal.Element(ns + "UserId") == owner &&
                (string?)principal.Element(ns + "LogonType") == "InteractiveToken" &&
                ((string?)principal.Element(ns + "RunLevel") ?? "LeastPrivilege") == (spec.RunLevel == 1 ? "HighestAvailable" : "LeastPrivilege") &&
                (string?)definition.Element(ns + "Actions")?.Attribute("Context") == (string?)principal.Attribute("id") &&
                SamePath((string)action.Element(ns + "Command")!, spec.Executable) &&
                (string?)action.Element(ns + "Arguments") == spec.Arguments &&
                SamePath((string)action.Element(ns + "WorkingDirectory")!, spec.Directory) && (bool)task.Enabled;
        }
        finally { Marshal.FinalReleaseComObject(task); }
    }
    static void Register(dynamic scheduler, dynamic folder, TaskSpec spec, string owner, int flags = 2)
    {
        dynamic task = scheduler.NewTask(0);
        try
        {
            task.RegistrationInfo.Source = spec.Source;
            task.RegistrationInfo.Description = "Disposable WishGuard TestOnly component recovery fixture";
            task.Principal.UserId = owner; task.Principal.LogonType = 3; task.Principal.RunLevel = spec.RunLevel;
            task.Settings.Enabled = true; task.Settings.AllowDemandStart = true; task.Settings.MultipleInstances = 2;
            task.Settings.ExecutionTimeLimit = "PT0S"; task.Settings.DisallowStartIfOnBatteries = false; task.Settings.StopIfGoingOnBatteries = false;
            dynamic action = task.Actions.Create(0); action.Path = spec.Executable; action.Arguments = spec.Arguments; action.WorkingDirectory = spec.Directory;
            // No trigger and no task RestartOnFailure: only the service can Run
            // these existing tasks during the automatic recovery assertions.
            object registered = folder.RegisterTaskDefinition(spec.Name, task, flags, owner, null, 3, null); Marshal.FinalReleaseComObject(registered);
        }
        finally { Marshal.FinalReleaseComObject(task); }
    }

    static class Scm
    {
        [StructLayout(LayoutKind.Sequential)] struct Status
        { public uint Type, State, Accepted, ExitCode, SpecificExit, CheckPoint, WaitHint, Pid, Flags; }
        [StructLayout(LayoutKind.Sequential)] struct Action { public uint Type, Delay; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct Failure
        { public uint ResetPeriod; public nint Reboot, Command; public uint Count; public nint Actions; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenSCManager(string? machine, string? database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint OpenService(nint manager, string name, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateService(nint manager, string name, string display, uint access, uint type, uint start, uint error, string binary, string? group, nint tag, string? dependencies, string? account, string? password);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ChangeServiceConfig2(nint service, uint level, ref Failure info);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool ChangeServiceConfig(nint service, uint type, uint start, uint error, string? binary, string? group, nint tag, string? dependencies, string? account, string? password, string? display);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatusEx(nint service, int level, out Status status, int size, out int needed);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool StartService(nint service, uint count, nint arguments);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool ControlService(nint service, uint control, out Status status);
        [DllImport("advapi32.dll", SetLastError = true)] static extern bool DeleteService(nint service);
        [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(nint handle);
        static Win32Exception Error() => new(Marshal.GetLastWin32Error());
        static nint Manager(uint access = 1) { nint handle = OpenSCManager(null, null, access); return handle != 0 ? handle : throw Error(); }
        public static void CreateAndConfigure(string name, string binary)
        {
            if (!name.StartsWith("WishGuard-Test-", StringComparison.Ordinal)) throw new IOException("Only a disposable test service is allowed.");
            nint manager = Manager(3), service = 0, actions = 0;
            try
            {
                service = CreateService(manager, name, name, 0xF01FF, 0x10, 3, 1, binary, null, 0, null, null, null);
                if (service == 0) throw Error();
                actions = Marshal.AllocHGlobal(Marshal.SizeOf<Action>() * 3);
                for (int n = 0; n < 3; n++) Marshal.StructureToPtr(new Action { Type = 1, Delay = 2000 }, actions + n * Marshal.SizeOf<Action>(), false);
                var failure = new Failure { ResetPeriod = 86400, Count = 3, Actions = actions };
                // SCM repeats the final action after the third crash. Four
                // failures below verify that this recovery is not exhausted.
                if (!ChangeServiceConfig2(service, 2, ref failure)) throw Error();
            }
            catch { if (service != 0) DeleteService(service); throw; }
            finally { if (actions != 0) Marshal.FreeHGlobal(actions); if (service != 0) CloseServiceHandle(service); CloseServiceHandle(manager); }
        }
        public static void Start(string name)
        {
            nint manager = Manager(), service = OpenService(manager, name, 0x10);
            try { if (service == 0 || !StartService(service, 0, 0)) throw Error(); }
            finally { if (service != 0) CloseServiceHandle(service); CloseServiceHandle(manager); }
        }
        public static ServiceStatus Query(string name)
        {
            nint manager = Manager(), service = OpenService(manager, name, 4);
            try
            {
                if (service == 0)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 1060) return new(false, 1, 0, 0);
                    if (error == 1072) return new(true, 3, 0, 0); // Deletion is still pending, not yet proof of removal.
                    throw new Win32Exception(error);
                }
                if (!QueryServiceStatusEx(service, 0, out var state, Marshal.SizeOf<Status>(), out _)) throw Error();
                return new(true, state.State, (int)state.Pid, state.ExitCode);
            }
            finally { if (service != 0) CloseServiceHandle(service); CloseServiceHandle(manager); }
        }
        public static void Cleanup(string name)
        {
            if (!name.StartsWith("WishGuard-Test-", StringComparison.Ordinal)) throw new IOException("Cleanup accepts only the fixture service name.");
            nint manager = Manager(), service = OpenService(manager, name, 0x10000 | 2 | 4 | 0x20);
            try
            {
                if (service == 0) { if (Marshal.GetLastWin32Error() is 1060 or 1072) return; throw Error(); }
                if (!ChangeServiceConfig(service, uint.MaxValue, 4, uint.MaxValue, null, null, 0, null, null, null, null)) throw Error();
                if (QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<Status>(), out _) && status.State != 1)
                    if (!ControlService(service, 1, out _) && Marshal.GetLastWin32Error() != 1062) throw Error();
                if (!DeleteService(service) && Marshal.GetLastWin32Error() != 1072) throw Error();
            }
            finally { if (service != 0) CloseServiceHandle(service); CloseServiceHandle(manager); }
        }
    }
}
