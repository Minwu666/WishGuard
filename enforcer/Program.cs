using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using WishGuard;

namespace WishGuard.Enforcer;
internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length != 2 || args[0] is not ("--profile" or "--input-profile")) return 2;
        string path = Path.GetFullPath(args[1]);
        var profile = EnforcementPaths.Read(path);
        if (args[0] == "--input-profile")
        {
            if (profile.TestOnly && (profile.GamePaths.Length != 1 || Path.GetFileName(profile.GamePaths[0]) != "WishGuardProbeGame.exe")) return 3;
            return await InputBroker.Run(path, profile);
        }
        if (!profile.TestOnly)
        {
            if (!WindowsIdentity.GetCurrent().IsSystem || !string.Equals(path, EnforcementPaths.Profile(profile.OwnerSid), StringComparison.OrdinalIgnoreCase)) return 3;
            if (profile.GamePaths.Length == 0 || profile.GamePaths.Any(p => Path.GetFileName(p) is not ("YuanShen.exe" or "GenshinImpact.exe"))) return 3;
        }
        else if (profile.GamePaths.Length != 1 || Path.GetFileName(profile.GamePaths[0]) != "WishGuardProbeGame.exe") return 3;
        if (!profile.Policy.Committed || profile.Policy.TargetPulls != 600 || profile.Policy.UnlockMode != "permanent") return 3;
        using var singleton = new Mutex(true, "Global\\" + profile.PipeName, out bool first);
        if (!first) return 0;
        if (profile.Completed) { RemoveTask(profile); return 0; }
        using var stop = new CancellationTokenSource();
        var monitor = new Supervisor(path, profile);
        Task server = monitor.Serve(stop.Token);
        try { await monitor.Run(stop.Token); }
        finally { stop.Cancel(); try { await server; } catch (OperationCanceledException) { } }
        RemoveTask(monitor.Profile);
        return 0;
    }
    static void RemoveTask(EnforcementProfile profile)
    {
        if (profile.TestOnly || !profile.Completed || !profile.TaskName.StartsWith("WishGuard-Strong-", StringComparison.Ordinal)) return;
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try
        {
            service.Connect(); folder = service.GetFolder("\\");
            foreach (var item in new[] { (profile.TaskName, "WishGuard strong protection"), (profile.TaskName + "-Input", "WishGuard input compatibility") })
            {
                try
                {
                    dynamic task = ((dynamic)folder).GetTask(item.Item1);
                    if ((string)task.Definition.RegistrationInfo.Source == item.Item2 &&
                        string.Equals((string)task.Definition.Actions.Item(1).Path, profile.EnforcerExecutable, StringComparison.OrdinalIgnoreCase))
                        ((dynamic)folder).DeleteTask(item.Item1, 0);
                }
                catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { }
            }
        }
        catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { }
        finally { if (folder != null) Marshal.FinalReleaseComObject(folder); Marshal.FinalReleaseComObject(service); }
    }
}

public sealed class Supervisor(string file, EnforcementProfile profile)
{
    public EnforcementProfile Profile => profile;
    readonly object sync = new();
    readonly Stopwatch uptime = Stopwatch.StartNew();
    readonly long startWall = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    readonly Dictionary<(int Pid, long Start), long> games = new();
    int clientId; long lastHealth = -10000; bool healthy;
    volatile bool completionPublished;
    long Now => Math.Max(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), startWall + uptime.ElapsedMilliseconds / 1000);
    void Log(string name)
    {
        try
        {
            string log = Path.Combine(Path.GetDirectoryName(file)!, "protection.log");
            if (File.Exists(log) && new FileInfo(log).Length > 1_000_000) File.Move(log, log + ".previous", true);
            File.AppendAllText(log, $"{DateTimeOffset.Now:O} {name}\n");
        } catch { }
    }
    public async Task Serve(CancellationToken cancel)
    {
        var acl = new PipeSecurity();
        acl.SetOwner(WindowsIdentity.GetCurrent().User!);
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(profile.OwnerSid), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(profile.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, acl);
                await pipe.WaitForConnectionAsync(cancel);
                if (!ProcessIdentity.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out int pid) || !ProcessIdentity.TrustedClient(pid, profile)) continue;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(2000);
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                string? line = await reader.ReadLineAsync(timeout.Token);
                if (line == null || line.Length > 20000) continue;
                ProtectionReply reply;
                lock (sync)
                {
                    string previous = JsonSerializer.Serialize(profile);
                    try
                    {
                        var request = JsonSerializer.Deserialize<ProtectionRequest>(line) ?? throw new IOException();
                        switch (request.Command)
                        {
                            case "health": clientId = pid; healthy = request.Healthy; lastHealth = uptime.ElapsedMilliseconds; break;
                            case "status": break;
                            case "recover-request": Rules.RequestRecovery(profile.Policy, Now); Save(); break;
                            case "recover-cancel": Rules.CancelRecovery(profile.Policy); Save(); break;
                            case "recover-finish":
                                if (!Rules.CanFinishRecovery(profile.Policy, Now)) throw new IOException("冷静期尚未结束。");
                                profile.Policy.RecoveryFinished = true; profile.Completed = true; Save(); break;
                            case "unlock":
                                if (Rules.ClockRolledBack(profile.Policy, Now)) throw new IOException();
                                var grant = Rules.Validate(request.Token ?? "", profile.PublicKey, profile.Policy, Now, true);
                                profile.Policy.PermanentPass = request.Token; profile.Policy.UsedPasses.Add(grant.Id); profile.Completed = true; Save(); break;
                            default: throw new IOException("请求无效。");
                        }
                        reply = Reply(true, "");
                    }
                    catch (Exception)
                    {
                        profile = JsonSerializer.Deserialize<EnforcementProfile>(previous)!;
                        reply = Reply(false, "保护操作未完成，请检查计划状态或凭证。");
                    }
                }
                try { await writer.WriteLineAsync(JsonSerializer.Serialize(reply).AsMemory(), timeout.Token); }
                finally { if (reply.Completed) completionPublished = true; }
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
            catch (Exception) { Log("connection-error"); await Task.Delay(100, cancel); }
        }
    }
    ProtectionReply Reply(bool ok, string message) => new(ok, profile.Completed, profile.Policy.RecoveryAt, profile.Policy.RecoveryRequestedAt, profile.Policy.RecoveryFinished, message, profile.Policy.PermanentPass);
    void Save() { profile.Policy.LastSeen = Math.Max(profile.Policy.LastSeen, Now); EnforcementPaths.Save(file, profile); }
    public async Task Run(CancellationToken cancel)
    {
        Log("started"); long lastSave = 0;
        while (!cancel.IsCancellationRequested)
        {
            bool allowed;
            lock (sync)
            {
                if (profile.Completed && completionPublished) break;
                allowed = profile.Completed || healthy && uptime.ElapsedMilliseconds - lastHealth < 1500 && Alive(clientId);
                if (!profile.Completed && uptime.ElapsedMilliseconds - lastSave > 20000)
                {
                    try { Save(); lastSave = uptime.ElapsedMilliseconds; }
                    catch { healthy = allowed = false; Log("state-write-error"); }
                }
            }
            var seen = new HashSet<(int, long)>();
            foreach (var path in profile.GamePaths)
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
                    using (process)
                    {
                        try
                        {
                            if (!ProcessIdentity.IsTarget(process.Id, profile)) continue;
                            var key = (process.Id, process.StartTime.ToUniversalTime().Ticks); seen.Add(key);
                            if (!games.TryGetValue(key, out long began)) games[key] = began = uptime.ElapsedMilliseconds;
                            if (!allowed && uptime.ElapsedMilliseconds - began >= 3500)
                            {
                                // Only this owner's explicitly enrolled game executable. No process tree termination.
                                if (ProcessIdentity.IsTarget(process.Id, profile)) { process.Kill(false); Log("game-closed-protection-unavailable"); }
                            }
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { Log("game-close-error"); }
                    }
            foreach (var key in games.Keys.Where(k => !seen.Contains(k)).ToArray()) games.Remove(key);
            await Task.Delay(200, cancel);
        }
        Log("completed");
    }
    static bool Alive(int pid)
    { try { using var process = Process.GetProcessById(pid); return !process.HasExited; } catch { return false; } }
}
