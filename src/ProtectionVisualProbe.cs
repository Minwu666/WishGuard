#if WISHGUARD_TESTS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace WishGuard;
internal static class ProtectionVisualProbe
{
    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]
    static extern bool IsIconic(nint window);

    public static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh test directory.");
        Directory.CreateDirectory(root); int exit = 1;
        var results = new List<object>();
        using var game = new DemoGame(); using var alternate = new Form { Text = "攒愿隔离测试", Size = new(400, 300) };
        var store = new StateStore(Path.Combine(root, "state")); store.State.Committed = true; store.Save();
        using var guard = new GuardController(store, "") { Strong = true, DemoHandle = game.Handle, CaptureForTest = Path.Combine(root,"footer.png") };
        bool GameMinimized() => IsIconic(game.Handle) && game.WindowState == FormWindowState.Minimized;
        async Task RetainFailure(string name)
        {
            var scene = new
            {
                name,
                foreground = (long)Native.GetForegroundWindow(),
                foregroundProcess = Native.ProcessName(Native.GetForegroundWindow()),
                gameHandle = (long)game.Handle,
                gameVisible = game.Visible,
                gameNativeVisible = IsWindowVisible(game.Handle),
                gameIconic = IsIconic(game.Handle),
                gameWindowState = game.WindowState.ToString(),
                alternateHandle = alternate.IsHandleCreated ? (long)alternate.Handle : 0,
                alternateVisible = alternate.Visible,
                alternateNativeVisible = alternate.IsHandleCreated && IsWindowVisible(alternate.Handle),
                alternateWindowState = alternate.WindowState.ToString(),
                coverVisible = guard.CoverVisibleForTest,
                coverHandle = (long)guard.CoverHandleForTest,
                coverNativeVisible = guard.CoverHandleForTest != 0 && IsWindowVisible(guard.CoverHandleForTest),
                guard.Phase,
                guard.DebugForTest
            };
            File.WriteAllText(Path.Combine(root, "focus-failure.json"), JsonSerializer.Serialize(scene, new JsonSerializerOptions { WriteIndented = true }));
            // Leave the failing synthetic scene unchanged briefly for observation.
            await Task.Delay(20000);
        }
        async Task Check(string name, Func<bool> condition, int ms = 10000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < ms) await Task.Delay(50);
            bool pass = condition(); results.Add(new { name, pass, milliseconds = watch.ElapsedMilliseconds, guard.Phase, guard.DebugForTest });
            if (!pass) { await RetainFailure(name); throw new Exception(name + ": " + guard.Status); }
        }
        async Task Hold(string name, Func<bool> condition, int ms = 2500)
        {
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < ms)
            {
                if (!condition())
                {
                    results.Add(new { name, pass = false, milliseconds = watch.ElapsedMilliseconds, guard.Phase, guard.DebugForTest });
                    await RetainFailure(name);
                    throw new Exception(name + ": " + guard.Status);
                }
                await Task.Delay(50);
            }
            bool pass = condition(); results.Add(new { name, pass, milliseconds = watch.ElapsedMilliseconds, guard.Phase, guard.DebugForTest });
            if (!pass) { await RetainFailure(name); throw new Exception(name + ": " + guard.Status); }
        }
        game.Shown += async (_, _) =>
        {
            try
            {
                await Task.Delay(100); game.Hide(); game.Show(); game.Activate(); guard.DemoHandle = game.Handle;
                game.ShowLoadingForTest(true);
                await guard.Start();
                await Check("startup loading is identified without cover", () => guard.Phase == "等待进入游戏" && !guard.CoverVisibleForTest);
                for (int i = 0; i < 35; i++)
                {
                    await Task.Delay(100);
                    if (guard.CoverVisibleForTest) throw new Exception("Startup loading flickered a cover.");
                }
                await Check("loading remains uncovered and protection responds", () => !guard.CoverVisibleForTest && guard.EnforcementHealthy);
                alternate.Show(); alternate.Activate();
                await Check("switching away during loading leaves game unminimized", () => Native.GetForegroundWindow() == alternate.Handle && !guard.CoverVisibleForTest && !IsIconic(game.Handle) && game.WindowState == FormWindowState.Normal);
                await Hold("loading stays unminimized without reclaiming focus", () => Native.GetForegroundWindow() == alternate.Handle && !guard.CoverVisibleForTest && !IsIconic(game.Handle) && game.WindowState == FormWindowState.Normal, 1500);
                game.Activate();
                await Check("returning to loading keeps startup unobstructed", () => Native.GetForegroundWindow() == game.Handle && !guard.CoverVisibleForTest && guard.Phase == "等待进入游戏");
                game.ShowLoadingForTest(false);
                await Check("readable normal game is released", () => !guard.CoverVisibleForTest && guard.Phase == "守护中" && Native.GetForegroundWindow() == game.Handle, 15000);
                await Check("foreground game entry shortcut is available", () => guard.ShortcutRegisteredForTest);
                await Task.Delay(1000);
                await Check("normal foreground remains clear after focus handoff", () => !guard.CoverVisibleForTest && Native.GetForegroundWindow() == game.Handle);
                alternate.Show(); alternate.Activate();
                await Check("switching away from normal play minimizes game", () => GameMinimized() && !guard.CoverVisibleForTest && Native.GetForegroundWindow() == alternate.Handle && !guard.ShortcutRegisteredForTest);
                await Hold("normal play stays minimized without reclaiming focus", () => GameMinimized() && !guard.CoverVisibleForTest && Native.GetForegroundWindow() == alternate.Handle && !guard.ShortcutRegisteredForTest, 3500);
                game.WindowState = FormWindowState.Normal; game.Activate();
                await Check("restoring normal play releases the confirmation cover", () => !guard.CoverVisibleForTest && guard.Phase == "守护中" && Native.GetForegroundWindow() == game.Handle, 15000);
                bool shortcut = true; guard.ShortcutForTest = () => shortcut;
                await Check("entry indication covers before wish scene renders", () => guard.CoverVisibleForTest, 150);
                shortcut = false; await Task.Delay(350);
                await Check("transition cover remains while normal frame is still visible", () => guard.CoverVisibleForTest, 100);
                game.ShowWishForTest(true); game.Activate();
                await Check("wish scene is covered", () => guard.CoverVisibleForTest && guard.WishSeenForTest);
                await Check("covered game reports usable protection", () => guard.EnforcementHealthy);
                for (int i = 0; i < 5; i++)
                {
                    game.Activate();
                    string status = await guard.ConfirmEnforcementStatus();
                    if (status != "ready") throw new Exception("Focus handoff reported " + status);
                    await Task.Delay(80);
                }
                await Check("repeated game activation retains usable protection", () => guard.CoverVisibleForTest && guard.EnforcementHealthy);
                alternate.Show(); alternate.Activate();
                await Check("switching away from wish minimizes game", () => GameMinimized() && !guard.CoverVisibleForTest && Native.GetForegroundWindow() == alternate.Handle);
                await Check("other applications retain their shortcuts", () => !guard.ShortcutRegisteredForTest);
                await Hold("switching away from wish keeps game minimized and other window accessible", () => GameMinimized() && !guard.CoverVisibleForTest && Native.GetForegroundWindow() == alternate.Handle && !guard.ShortcutRegisteredForTest);
                game.WindowState = FormWindowState.Normal; game.Activate();
                await Check("returning wish scene is covered without waiting for OCR", () => guard.CoverVisibleForTest, 800);
                game.WindowState = FormWindowState.Minimized; alternate.Activate();
                await Check("manual minimization conceals cover", () => GameMinimized() && !guard.CoverVisibleForTest && Native.GetForegroundWindow() == alternate.Handle);
                await Hold("wish stays minimized without showing cover or reclaiming focus", () => GameMinimized() && !guard.CoverVisibleForTest && Native.GetForegroundWindow() == alternate.Handle);
                game.WindowState = FormWindowState.Normal; game.Activate();
                await Check("restoring wish scene retains protection", () => guard.CoverVisibleForTest && guard.WishSeenForTest, 800);
                game.ShowWishForTest(false);
                await Check("returning to normal play releases cover", () => !guard.CoverVisibleForTest && guard.Phase == "守护中" && Native.GetForegroundWindow() == game.Handle);
                guard.ProtectionAvailable(false); game.Activate();
                await Check("unavailable protection keeps game covered", () => guard.CoverVisibleForTest && !guard.EnforcementHealthy);
                guard.ProtectionAvailable(true);
                await Check("restored protection allows normal play", () => !guard.CoverVisibleForTest && guard.Phase == "守护中" && Native.GetForegroundWindow() == game.Handle);
                guard.ShortcutForTest = null;
                exit = 0;
            }
            catch (Exception e) { results.Add(new { name = "visual integration", pass = false, error = e.ToString() }); }
            finally
            {
                File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = exit == 0, results }, new JsonSerializerOptions { WriteIndented = true }));
                guard.Stop("测试结束"); game.Close(); alternate.Close();
            }
        };
        Application.Run(game); return exit;
    }
}
#endif
