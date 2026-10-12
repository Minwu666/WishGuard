#if WISHGUARD_TESTS
using System.Diagnostics;
using System.Text.Json;
namespace WishGuard;
internal static class ProtectionVisualProbe
{
    public static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Use a fresh test directory.");
        Directory.CreateDirectory(root); int exit = 1;
        var results = new List<object>();
        using var game = new DemoGame(); using var alternate = new Form { Text = "攒愿隔离测试", Size = new(400, 300) };
        var store = new StateStore(Path.Combine(root, "state")); store.State.Committed = true; store.Save();
        using var guard = new GuardController(store, "") { Strong = true, DemoHandle = game.Handle, CaptureForTest = Path.Combine(root,"footer.png") };
        async Task Check(string name, Func<bool> condition, int ms = 10000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition() && watch.ElapsedMilliseconds < ms) await Task.Delay(50);
            bool pass = condition(); results.Add(new { name, pass, milliseconds = watch.ElapsedMilliseconds, guard.Phase, guard.DebugForTest });
            if (!pass) throw new Exception(name + ": " + guard.Status);
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
                game.ShowLoadingForTest(false);
                await Check("readable normal game is released", () => !guard.CoverVisibleForTest && guard.Phase == "守护中" && Native.GetForegroundWindow() == game.Handle, 15000);
                await Check("foreground game entry shortcut is available", () => guard.ShortcutRegisteredForTest);
                await Task.Delay(1000);
                await Check("normal foreground remains clear after focus handoff", () => !guard.CoverVisibleForTest && Native.GetForegroundWindow() == game.Handle);
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
                await Check("other windows remain accessible", () => !guard.CoverVisibleForTest);
                await Check("other applications retain their shortcuts", () => !guard.ShortcutRegisteredForTest);
                game.Activate();
                await Check("returning wish scene is covered without waiting for OCR", () => guard.CoverVisibleForTest, 800);
                game.WindowState = FormWindowState.Minimized; alternate.Activate();
                await Check("minimizing conceals cover", () => !guard.CoverVisibleForTest);
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
