#if WISHGUARD_TESTS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
namespace WishGuard;

// Cross-process regression for the passive cover. Only the copied synthetic
// game is restored or activated; scenes change through a private control file.
internal static class BackgroundProbe
{
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern bool ShowWindowAsync(nint window, int command);

    sealed class AlternateWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
    }
    sealed record DemoState(bool Wish, int Attempts, string ForegroundToken, bool ForegroundGranted,
        int ForegroundError, string PauseToken, bool Paused, long PauseStarted, long PauseEnded);

    public static int Run(string root, string enforcer)
    {
        root = Path.GetFullPath(root); enforcer = Path.GetFullPath(enforcer);
        if (Directory.Exists(root)) throw new IOException("Use a fresh test directory.");
        if (!File.Exists(enforcer)) throw new FileNotFoundException("The test input broker was not found.", enforcer);
        Directory.CreateDirectory(root);
        string games = Path.Combine(root, "game"); Directory.CreateDirectory(games);
        foreach (string file in Directory.GetFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(games, Path.GetFileName(file)));
        string fake = Path.Combine(games, "WishGuardProbeGame.exe");
        File.Copy(Path.Combine(games, "WishGuard.exe"), fake);
        string control = Path.Combine(root, "control"), fileProfile = Path.Combine(root, "profile.json");
        File.WriteAllText(control, "normal-start");
        var profile = new EnforcementProfile
        {
            OwnerSid = EnforcementPaths.Owner,
            ClientDirectory = AppContext.BaseDirectory,
            DataDirectory = Path.Combine(root, "data"),
            EnforcerExecutable = enforcer,
            PipeName = "WishGuard-background-" + Guid.NewGuid().ToString("N"),
            GamePaths = [fake],
            TestOnly = true,
            Policy = new GuardState { Committed = true }
        };
        foreach (string name in new[] { "WishGuard.exe", "WishGuard.dll", "WishGuard.deps.json", "WishGuard.runtimeconfig.json", "ocr-worker.ps1" })
            profile.ClientHashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, name))));
        EnforcementPaths.Save(fileProfile, profile);
        var store = new StateStore(profile.DataDirectory); store.State.Committed = true; store.Save();
        using var host = new Form { Text = "攒愿后台回归控制", ShowInTaskbar = false, Opacity = 0 };
        using var alternate = new AlternateWindow
        {
            Text = "后台检查 · 其他应用模拟窗口",
            Size = new Size(460, 260),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(Screen.PrimaryScreen!.WorkingArea.Left + 45, Screen.PrimaryScreen.WorkingArea.Top + 45)
        };
        alternate.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "正在检查切出与恢复。\n此窗口不连接真实游戏，也不会发送按键或抽卡。",
            TextAlign = ContentAlignment.MiddleCenter
        });
        using var guard = new GuardController(store, "") { Strong = true }; guard.UseInputBroker(profile);
        Process? game = null, broker = null; nint gameWindow = 0;
        bool foregroundRequestAccepted = false;
        object? finalNormalSample = null;
        int exit = 1; var checks = new List<object>();

        Process Start(string exe, params string[] args)
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in args) info.ArgumentList.Add(arg);
            return Process.Start(info)!;
        }
        bool Alive() => game != null && !game.HasExited;
        bool NormalReleased() => Alive() && !guard.CoverVisibleForTest && guard.Phase == "守护中" &&
            guard.EnforcementDiagnostic.Contains(";gameplay=True;", StringComparison.Ordinal) &&
            guard.EnforcementHealthy && Native.GetForegroundWindow() == gameWindow;
        DemoState? ReadDemoState()
        {
            try
            {
                using var file = new FileStream(control + ".state", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var state = JsonDocument.Parse(file);
                var value = state.RootElement;
                return new(value.GetProperty("Wish").GetBoolean(), value.GetProperty("Attempts").GetInt32(),
                    value.GetProperty("ForegroundToken").GetString()!, value.GetProperty("ForegroundGranted").GetBoolean(),
                    value.GetProperty("ForegroundError").GetInt32(), value.GetProperty("PauseToken").GetString()!,
                    value.GetProperty("Paused").GetBoolean(), value.GetProperty("PauseStarted").GetInt64(), value.GetProperty("PauseEnded").GetInt64());
            }
            catch (IOException) { return null; }
            catch (JsonException) { return null; }
        }
        bool Scene(bool wish) => ReadDemoState() is { Attempts: 0 } state && state.Wish == wish;
        void Diagnostic(string name)
        {
            nint foreground = Native.GetForegroundWindow();
            File.WriteAllText(Path.Combine(root, "failure.json"), JsonSerializer.Serialize(new
            {
                name,
                foreground = (long)foreground,
                foregroundProcess = Native.ProcessName(foreground),
                gameWindow = (long)gameWindow,
                gameAlive = Alive(),
                gameVisible = gameWindow != 0 && IsWindowVisible(gameWindow),
                gameMinimized = gameWindow != 0 && IsIconic(gameWindow),
                alternateWindow = alternate.IsHandleCreated ? (long)alternate.Handle : 0,
                foregroundRequestAccepted,
                coverVisible = guard.CoverVisibleForTest,
                coverWindow = (long)guard.CoverHandleForTest,
                guard.Phase,
                guard.Status,
                guard.EnforcementDiagnostic,
                guard.DebugForTest,
                finalNormalSample,
                demoState = ReadDemoState()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        async Task Check(string name, Func<bool> condition, int ms = 15000)
        {
            var time = Stopwatch.StartNew();
            bool pass = condition();
            while (!pass && time.ElapsedMilliseconds < ms)
            {
                await Task.Delay(10);
                pass = condition();
            }
            checks.Add(new { name, pass, milliseconds = time.ElapsedMilliseconds });
            if (!pass) { Diagnostic(name); throw new Exception(name + ": " + guard.Phase + "; " + guard.Status); }
        }
        async Task Hold(string name, Func<bool> condition, int ms = 2500)
        {
            var time = Stopwatch.StartNew();
            while (time.ElapsedMilliseconds < ms)
            {
                if (!condition())
                {
                    checks.Add(new { name, pass = false, milliseconds = time.ElapsedMilliseconds });
                    Diagnostic(name); throw new Exception(name + ": a continuous assertion failed.");
                }
                await Task.Delay(20);
            }
            bool pass = condition(); checks.Add(new { name, pass, milliseconds = time.ElapsedMilliseconds });
            if (!pass) { Diagnostic(name); throw new Exception(name + ": the final assertion failed."); }
        }
        async Task<string> GrantForeground(string name, bool pause)
        {
            string token = Guid.NewGuid().ToString("N");
            File.WriteAllText(control, (pause ? "pause:" : "foreground:") + Environment.ProcessId + ":" + token);
            await Check(name + (pause ? " confirms the fake UI pause and parent foreground grant" : " authorizes the test parent's foreground request"),
                () => Alive() && Native.GetForegroundWindow() == gameWindow && ReadDemoState() is { ForegroundGranted: true } state &&
                    state.ForegroundToken == token && (!pause || state.Paused && state.PauseToken == token), 3000);
            return token;
        }
        async Task Leave(string name, bool delayed = false)
        {
            string token = await GrantForeground(name, delayed);
            alternate.Show();
            // The fake child granted this exact parent process permission. Make
            // one foreground request; never attach to the paused UI thread.
            foregroundRequestAccepted = Native.SetForegroundWindow(alternate.Handle);
            await Check(name + " lets another window take foreground", () => foregroundRequestAccepted && Native.GetForegroundWindow() == alternate.Handle, 1500);
            if (delayed)
            {
                bool PendingCovered() => Alive() && !IsIconic(gameWindow) && guard.CoverVisibleForTest &&
                    Native.GetForegroundWindow() == alternate.Handle && ReadDemoState() is { Paused: true, Attempts: 0 } state && state.PauseToken == token;
                // A passing check requires a measured pending interval. A UI
                // pause missed by the observer must never count as success.
                await Check(name + " exposes pending minimization with the cover retained", PendingCovered, 0);
                await Hold(name + " keeps the cover during a real pending minimization", PendingCovered, 150);
            }
            await Check(name + " minimizes the real synthetic game HWND and conceals cover",
                () => Alive() && IsIconic(gameWindow) && !guard.CoverVisibleForTest && Native.GetForegroundWindow() == alternate.Handle, 2000);
            if (delayed)
                await Check(name + " finishes the 500ms fake UI pause without a wish attempt",
                    () => ReadDemoState() is { Paused: false, Attempts: 0 } state && state.PauseToken == token && state.PauseEnded - state.PauseStarted >= 500, 2000);
            await Hold(name + " keeps the other window foreground and the game minimized",
                () => Alive() && IsIconic(gameWindow) && !guard.CoverVisibleForTest && Native.GetForegroundWindow() == alternate.Handle);
        }
        async Task Restore(string name, bool wish)
        {
            if (!Alive()) throw new Exception("The synthetic game exited before restore.");
            ShowWindowAsync(gameWindow, 9); // SW_RESTORE; this handle is exclusively the copied fake game.
            await Check(name + " restores the synthetic game without exiting", () => Alive() && !IsIconic(gameWindow) && IsWindowVisible(gameWindow), 2000);
            Native.SetForegroundWindow(gameWindow);
            await Check(name + " immediately restores the passive protection cover",
                () => guard.CoverVisibleForTest && Native.GetForegroundWindow() == gameWindow, 400);
            if (wish)
                await Check(name + " retains wish protection and game foreground", () => guard.CoverVisibleForTest && guard.WishSeenForTest && guard.EnforcementHealthy && Native.GetForegroundWindow() == gameWindow && Scene(true));
            else
                await Check(name + " releases normal play after recognition", () => NormalReleased() && Scene(false));
        }

        host.Shown += async (_, _) =>
        {
            host.Hide();
            try
            {
                broker = Start(enforcer, "--input-profile", fileProfile);
                game = Start(fake, "--return-demo", control);
                await Check("cross-process synthetic game window opens", () => { game.Refresh(); return !game.HasExited && game.MainWindowHandle != 0; });
                gameWindow = game.MainWindowHandle; guard.DemoHandle = gameWindow;
                await Check("control file selects normal play without wishing", () => Scene(false));
                Native.SetForegroundWindow(gameWindow); await guard.Start();
                await Check("normal play is recognized with a connected passive broker",
                    NormalReleased);
                for (int round = 1; round <= 2; round++)
                {
                    await Leave("round " + round + " normal play");
                    await Restore("round " + round + " normal play", false);
                    File.WriteAllText(control, "wish-" + round);
                    await Check("round " + round + " control selects wish without an attempt", () => Scene(true));
                    await Check("round " + round + " wish scene is protected by the passive broker",
                        () => guard.CoverVisibleForTest && guard.WishSeenForTest && guard.EnforcementHealthy && Native.GetForegroundWindow() == gameWindow);
                    await Leave("round " + round + " wish scene", delayed: round == 1);
                    await Restore("round " + round + " wish scene", true);
                    File.WriteAllText(control, "normal-" + round);
                    await Check("round " + round + " control returns without an attempt", () => Scene(false));
                    await Check("round " + round + " normal play resumes after the wish scene",
                        NormalReleased);
                }
                await Hold("final normal game stays running, foreground and clear", () =>
                {
                    bool alive = Alive(), minimized = IsIconic(gameWindow), covered = guard.CoverVisibleForTest;
                    nint foreground = Native.GetForegroundWindow(); var state = ReadDemoState();
                    finalNormalSample = new { alive, minimized, covered, foreground = (long)foreground,
                        expectedForeground = (long)gameWindow, stateAvailable = state != null, state?.Wish, state?.Attempts };
                    return alive && !minimized && !covered && foreground == gameWindow && state is { Wish: false, Attempts: 0 };
                }, 1000);
                exit = 0;
            }
            catch (Exception e) { checks.Add(new { name = "background integration", pass = false, error = e.ToString() }); }
            finally
            {
                guard.Dispose();
                foreach (var process in new[] { game, broker })
                    if (process != null)
                    {
                        try { if (!process.HasExited) process.Kill(); }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception e) { checks.Add(new { name = "test process cleanup", pass = false, error = e.Message }); exit = 1; }
                        process.Dispose();
                    }
                File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = exit == 0, checks }, new JsonSerializerOptions { WriteIndented = true }));
                alternate.Close(); host.Close();
            }
        };
        Application.Run(host); return exit;
    }
}
#endif
