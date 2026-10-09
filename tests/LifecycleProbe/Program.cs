using System.Diagnostics;
using System.Text.Json;
using WishGuard;

// Local integration probe: explicitly creates one temporary per-user scheduled
// task. It never uses the real profile, game process, or signing authority.
internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length != 3) throw new ArgumentException("Usage: LifecycleProbe <build-output> <self-test-data> <fresh-probe-directory>");
        string build = Path.GetFullPath(args[0]), fixture = Path.GetFullPath(args[1]), root = Path.GetFullPath(args[2]);
        if (Directory.Exists(root)) throw new IOException("Use a fresh probe directory.");
        string client = Path.Combine(root, "client"), data = Path.Combine(root, "profile");
        Directory.CreateDirectory(client); Directory.CreateDirectory(data);
        foreach (string file in Directory.GetFiles(build)) File.Copy(file, Path.Combine(client, Path.GetFileName(file)));
        File.Copy(Path.Combine(fixture, "test-public.txt"), Path.Combine(client, "authority-public.txt"), true);
        File.Copy(Path.Combine(fixture, "lifecycle", "state.json"), Path.Combine(data, "state.json"));
        string executable = Path.Combine(client, "WishGuard.exe"), fake = Path.Combine(client, "WishGuardProbeGame.exe");
        File.Copy(executable, fake);
        var checks = new List<object>();
        void Check(string name, bool pass) { checks.Add(new { name, pass }); if (!pass) throw new Exception(name); }
        static bool Until(Func<bool> ready, int seconds = 12)
        {
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(seconds)) { if (ready()) return true; Thread.Sleep(200); }
            return ready();
        }
        Process Start(string exe, params string[] arguments)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = client };
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            return Process.Start(info)!;
        }
        int exit = 1;
        try
        {
            using (var orphan = Start(executable, "--session", "--isolated", "--no-watch", "--data", data, "--test-process", "WishGuardProbeGame"))
                Check("session exits when game disappears before initialization", orphan.WaitForExit(12000));
            StartupTask.Install(data, executable, true, "WishGuardProbeGame");
            StartupTask.Start(data);
            Check("Task Scheduler starts the background watcher", Until(() => Lifecycle.Active(data, true)));
            Check("watcher waits without opening an OCR session", !Lifecycle.Active(data, false));
            for (int cycle = 1; cycle <= 2; cycle++)
            {
                using var game = Start(fake, "--sentinel");
                Check($"game launch {cycle} starts a guard session", Until(() => Lifecycle.Active(data, false), 8));
                game.WaitForExit();
                Check($"game exit {cycle} closes the guard session", Until(() => !Lifecycle.Active(data, false)));
                Check($"watcher survives game exit {cycle}", Lifecycle.Active(data, true));
            }
            var store = new StateStore(data);
            store.State.PermanentPass = File.ReadAllText(Path.Combine(fixture, "lifecycle-permanent.wishpass")); store.Save();
            Check("permanent completion stops the watcher", Until(() => !Lifecycle.Active(data, true)));
            // The isolated watcher deliberately avoids OS changes; exercise the
            // real cleanup here against only this uniquely named probe task.
            Lifecycle.Disable(data, false);
            bool removed = false;
            try { StartupTask.Start(data); } catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { removed = true; }
            Check("completion cleanup removes the scheduled task", removed);
            using (var finished = Start(executable, "--watch", "--isolated", "--data", data, "--test-process", "WishGuardProbeGame"))
                Check("completed profile rejects another automatic launch", finished.WaitForExit(6000));
            string log = File.ReadAllText(Path.Combine(data, "events.jsonl"));
            Check("lifecycle diagnostics record game and session changes", log.Contains("watcher_started") && log.Contains("session_launch") && log.Contains("session_closed"));
            Check("no unhandled or startup errors", !log.Contains("test_unhandled") && !log.Contains("startup_error") && !log.Contains("watcher_error"));
            exit = 0;
        }
        catch (Exception e) { checks.Add(new { name = "integration error", pass = false, error = e.ToString() }); }
        finally
        {
            foreach (var process in Process.GetProcessesByName("WishGuard").Concat(Process.GetProcessesByName("WishGuardProbeGame")))
            {
                using (process) try
                {
                    string? path = process.MainModule?.FileName;
                    if (path == executable || path == fake) { process.Kill(); process.WaitForExit(3000); }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            try { StartupTask.Remove(data); } catch (Exception e) { exit = 1; checks.Add(new { name = "cleanup", pass = false, error = e.Message }); }
            string report = JsonSerializer.Serialize(new { passed = exit == 0, checks }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(Path.Combine(root, "results.json"), report); Console.WriteLine(report);
        }
        return exit;
    }
}
