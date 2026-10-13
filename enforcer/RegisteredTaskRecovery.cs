using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using WishGuard;

namespace WishGuard.Enforcer;

// Starts only pre-existing, fully verified installation tasks. It cannot
// register tasks, choose arbitrary arguments, or launch arbitrary executables.
internal sealed class RegisteredTaskRecovery(string profileFile, EnforcementProfile profile, Func<bool> required, Action<string> log)
{
    bool ready;
    long checkedAt;
    internal bool Ready => Volatile.Read(ref ready) && Environment.TickCount64 - Interlocked.Read(ref checkedAt) < 2500;
    internal static string WatcherName(EnforcementProfile profile) => "WishGuard-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(profile.OwnerSid + "|" + Path.GetFullPath(profile.DataDirectory).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant())))[..20];

    internal async Task Run(CancellationToken cancel)
    {
        bool? previous = null;
        try
        {
            while (!cancel.IsCancellationRequested && required())
            {
                bool current = false;
                try { current = CheckAndStart(); }
                catch (Exception) { /* Failed recovery cannot authorize a game session. */ }
                Interlocked.Exchange(ref checkedAt, Environment.TickCount64);
                Volatile.Write(ref ready, current);
                if (previous != current) { previous = current; log(current ? "components-ready" : "component-recovery-unavailable"); }
                await Task.Delay(1000, cancel);
            }
        }
        finally { Volatile.Write(ref ready, false); }
    }

    bool CheckAndStart()
    {
        object? service = null, folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            ((dynamic)service).Connect(); folder = ((dynamic)service).GetFolder("\\");
            bool watch = Ensure(folder, WatcherName(profile), "WishGuard game watcher", false);
            bool input = Ensure(folder, profile.TaskName + "-Input", "WishGuard input compatibility", true);
            return watch && input;
        }
        finally { Release(folder); Release(service); }
    }

    bool Ensure(object folder, string name, string source, bool input)
    {
        object? task = null, instance = null;
        try
        {
            task = ((dynamic)folder).GetTask(name);
            if (!Verified((string)((dynamic)task).Xml, source, input, profileFile, profile) || !(bool)((dynamic)task).Enabled) return false;
            int state = (int)((dynamic)task).State;
            if (state == 4) return true;
            if (state == 2) return false; // Already queued; avoid repeated start requests.
            if (!required()) return false;
            instance = ((dynamic)task).Run(null);
            return (int)((dynamic)task).State == 4;
        }
        catch (Exception) { return false; }
        finally { Release(instance); Release(task); }
    }

    internal static bool Verified(string definition, string source, bool input, string profileFile, EnforcementProfile profile, bool legacySupervisor = false)
    {
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var task = XDocument.Parse(definition).Root;
            if (task?.Name != ns + "Task" || (string?)task.Element(ns + "RegistrationInfo")?.Element(ns + "Source") != source) return false;
            var principals = task.Element(ns + "Principals")?.Elements(ns + "Principal").ToArray();
            var actions = task.Element(ns + "Actions")?.Elements().ToArray();
            if (principals?.Length != 1 || actions?.Length != 1 || actions[0].Name != ns + "Exec") return false;
            var principal = principals[0]; var action = actions[0];
            string? actionContext = (string?)task.Element(ns + "Actions")?.Attribute("Context");
            if (actionContext != null && actionContext != (string?)principal.Attribute("id")) return false;
            string expectedSid = legacySupervisor ? "S-1-5-18" : profile.OwnerSid;
            string actualSid = (string?)principal.Element(ns + "UserId") ?? "";
            if (legacySupervisor && actualSid.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)) actualSid = "S-1-5-18";
            if (!actualSid.Equals(expectedSid, StringComparison.OrdinalIgnoreCase) ||
                (string?)principal.Element(ns + "LogonType") != (legacySupervisor ? "ServiceAccount" : "InteractiveToken") ||
                ((string?)principal.Element(ns + "RunLevel") ?? "LeastPrivilege") != (input || legacySupervisor ? "HighestAvailable" : "LeastPrivilege")) return false;
            string executable = input || legacySupervisor ? profile.EnforcerExecutable : Path.Combine(profile.ClientDirectory, "WishGuard.exe");
            string working = input || legacySupervisor ? Path.GetDirectoryName(profile.EnforcerExecutable)! : profile.ClientDirectory;
            string arguments = legacySupervisor ? "--profile \"" + profileFile + "\"" : input ? "--input-profile \"" + profileFile + "\"" :
                profile.TestOnly ? "--protection-probe \"" + profileFile + "\" --control \"" + Path.Combine(profile.DataDirectory, "control.json") + "\"" :
                "--watch --data \"" + Path.GetFullPath(profile.DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + "\"";
            return SamePath((string?)action.Element(ns + "Command"), executable) &&
                SamePath((string?)action.Element(ns + "WorkingDirectory"), working) &&
                (string?)action.Element(ns + "Arguments") == arguments;
        }
        catch (Exception) { return false; }
    }

    internal static void RemoveCompleted(string profileFile, EnforcementProfile profile)
    {
        if (!profile.Completed || profile.TestOnly && string.IsNullOrEmpty(profile.ServiceName)) return;
        object? service = null, folder = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            ((dynamic)service).Connect(); folder = ((dynamic)service).GetFolder("\\");
            foreach (var item in new[]
            {
                (profile.TaskName, "WishGuard strong protection", false, true),
                (profile.TaskName + "-Input", "WishGuard input compatibility", true, false),
                (WatcherName(profile), "WishGuard game watcher", false, false)
            })
            {
                object? task = null;
                try
                {
                    task = ((dynamic)folder).GetTask(item.Item1);
                    if (Verified((string)((dynamic)task).Xml, item.Item2, item.Item3, profileFile, profile, item.Item4))
                        ((dynamic)folder).DeleteTask(item.Item1, 0);
                }
                catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { }
                finally { Release(task); }
            }
        }
        finally { Release(folder); Release(service); }
    }

    static bool SamePath(string? actual, string expected) => actual != null &&
        string.Equals(Path.GetFullPath(actual).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(expected).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
}
