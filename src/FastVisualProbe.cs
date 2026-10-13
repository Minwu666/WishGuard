#if WISHGUARD_TESTS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace WishGuard;

// Pure visible-screen regression. Entries and returns invoke the synthetic
// game's own handlers; no OS input is generated and no real game is selected.
internal static class FastVisualProbe
{
    const int EntryBudget = 600, ReturnBudget = 3000;
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);

    public static int Run(string directory)
    {
        string root = Path.GetFullPath(directory);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Use a fresh test directory.");
        Directory.CreateDirectory(root);
        var results = new List<object>(); int exit = 1, shortcutPolls = 0; bool started = false;
        using var game = new DemoGame(); game.ShowLoadingForTest(true);
        var store = new StateStore(Path.Combine(root, "state")); store.State.Committed = true; store.Save();
        using var guard = new GuardController(store, "")
        {
            PassiveCoverForTest = true, Strong = true, DemoHandle = game.Handle,
            ShortcutForTest = () => { shortcutPolls++; return false; }
        };
        bool Cover() => guard.CoverVisibleForTest && guard.CoverHandleForTest != 0 && IsWindowVisible(guard.CoverHandleForTest);
        bool Normal() => !game.WishForTest && !Cover() && !guard.WishSeenForTest && guard.Phase == "守护中" &&
            Native.GetForegroundWindow() == game.Handle && guard.EnforcementHealthy;
        object Diagnostic() => new
        {
            guard.Phase, guard.Status, guard.DebugForTest, fastReady = guard.FastReadyForTest,
            fastReads = guard.FastReadCountForTest, fastWishHits = guard.FastWishCountForTest, lastFastMilliseconds = guard.LastFastMillisecondsForTest,
            resourceBusy = guard.ResourceBusyForTest, delayedSceneBusy = guard.SceneDelayBusyForTest,
            delayedSceneWasWish = guard.DelayedSceneWasWishForTest, foreground = (long)Native.GetForegroundWindow(),
            game = (long)game.Handle, cover = (long)guard.CoverHandleForTest,
            game.WishForTest, game.AttemptsForTest, game.WishPaintedAtForTest
        };
        void Record(string name, bool pass, double milliseconds, object? detail = null)
        {
            results.Add(new { name, pass, milliseconds = Math.Round(milliseconds, 2), detail, state = Diagnostic() });
            if (!pass) throw new InvalidOperationException(name);
        }
        async Task Check(string name, Func<bool> predicate, int timeout = 10000)
        {
            var watch = Stopwatch.StartNew(); bool pass;
            do { pass = predicate(); if (pass) break; await Task.Delay(10); } while (watch.ElapsedMilliseconds < timeout);
            Record(name, pass, watch.Elapsed.TotalMilliseconds);
        }
        async Task Hold(string name, Func<bool> predicate, int duration)
        {
            var watch = Stopwatch.StartNew(); bool pass = true;
            do { if (!predicate()) { pass = false; break; } await Task.Delay(10); } while (watch.ElapsedMilliseconds < duration);
            if (pass) pass = predicate();
            Record(name, pass, watch.Elapsed.TotalMilliseconds);
        }
        async Task Entry(string name, Action enter, bool requireResourceBusy = false)
        {
            Record(name + ": starts with a released normal foreground", Normal(), 0);
            long readsBefore = guard.FastReadCountForTest, wishHitsBefore = guard.FastWishCountForTest;
            if (requireResourceBusy) Record(name + ": resource worker is already occupied", guard.ResourceBusyForTest, 0);
            enter(); // Never Activate, Recheck, Start, or notify the F3 path here.
            long coverAt = 0, fastHitAt = 0; bool pass = false; var wait = Stopwatch.StartNew();
            do
            {
                if (coverAt == 0 && Cover()) coverAt = Stopwatch.GetTimestamp();
                if (fastHitAt == 0 && guard.FastWishCountForTest > wishHitsBefore) fastHitAt = Stopwatch.GetTimestamp();
                long painted = game.WishPaintedAtForTest;
                if (painted != 0 && coverAt != 0 && fastHitAt != 0 && guard.WishSeenForTest)
                {
                    double elapsed = (coverAt - painted) * 1000d / Stopwatch.Frequency;
                    double fastElapsed = (fastHitAt - painted) * 1000d / Stopwatch.Frequency;
                    Record(name + ": paint, cover and fast-positive clocks have a valid order", elapsed >= 0 && fastElapsed >= 0,
                        elapsed, new { wishPaintedAt = painted, coverObservedAt = coverAt, fastPositiveObservedAt = fastHitAt });
                    pass = elapsed <= EntryBudget && fastElapsed <= EntryBudget && game.AttemptsForTest == 0 && (!requireResourceBusy || guard.ResourceBusyForTest);
                    Record(name + ": first painted wish signal is covered within budget", pass, elapsed,
                        new { budgetMs = EntryBudget, wishPaintedAt = painted, coverObservedAt = coverAt,
                            fastReadsBefore = readsBefore, fastReadsAfter = guard.FastReadCountForTest, input = "synthetic own-handler invocation" });
                    Record(name + ": dedicated fast OCR positively identifies the same entry within budget", guard.FastWishCountForTest > wishHitsBefore && fastElapsed <= EntryBudget,
                        fastElapsed, new { budgetMs = EntryBudget, wishHitsBefore, wishHitsAfter = guard.FastWishCountForTest });
                    break;
                }
                await Task.Delay(5);
            } while (wait.ElapsedMilliseconds < EntryBudget + 1000);
            if (!pass) Record(name + ": painted signal and native cover were both observed", false, wait.Elapsed.TotalMilliseconds);
            await Check(name + ": independent fast worker continues to sample", () => guard.FastReadCountForTest > readsBefore, 2000);
            await Hold(name + ": confirmed wish remains continuously covered", () => Cover() && guard.WishSeenForTest && game.AttemptsForTest == 0, 400);
        }
        async Task Return(string name, Action leave, bool requireResourceBusy = false)
        {
            var watch = Stopwatch.StartNew(); leave();
            await Check(name + ": normal play returns within budget", () => Normal() && (!requireResourceBusy || guard.ResourceBusyForTest), ReturnBudget);
            Record(name + ": no synthetic wish attempt reached game", game.AttemptsForTest == 0, watch.Elapsed.TotalMilliseconds,
                new { budgetMs = ReturnBudget, input = "synthetic own-handler invocation; hardware return checked separately" });
            await Hold(name + ": released game remains foreground and clear", Normal, 350);
        }
        game.Shown += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                // Establish only the initial synthetic foreground; entry stages
                // below never activate or re-show any window.
                await Task.Delay(100); game.Hide(); game.Show(); game.Activate(); guard.DemoHandle = game.Handle;
                await guard.Start();
                await Check("dedicated fast OCR worker becomes ready", () => guard.FastReadyForTest, 30000);
                game.Activate();
                await Check("initial synthetic loading window owns foreground", () => Native.GetForegroundWindow() == game.Handle);
                await Check("startup loading remains readable without a cover", () => !Cover() && guard.Phase == "等待进入游戏");
                long loadingReads = guard.FastReadCountForTest;
                await Hold("startup loading is not mistaken for a wish", () => !Cover() && !guard.WishSeenForTest && Native.GetForegroundWindow() == game.Handle, 1800);
                Record("negative loading frames were actually sampled by the fast worker", guard.FastReadCountForTest > loadingReads && guard.FastWishCountForTest == 0, 0);
                game.ShowLoadingForTest(false);
                await Check("ordinary HUD is released before any unannounced entry", Normal, 15000);
                game.OrdinaryFooterForTest = "任务：前往祈愿之地"; game.Invalidate();
                long ordinaryReads = guard.FastReadCountForTest;
                await Hold("ordinary quest wording does not produce a wish cover", Normal, 1400);
                Record("ordinary negative wording was sampled", guard.FastReadCountForTest > ordinaryReads && guard.FastWishCountForTest == 0, 0);
                game.OrdinaryFooterForTest = ""; game.ShowWishTransitionForTest(true);
                await Hold("first wish-loading transition stays clear until a button signal exists", () => !Cover() && !guard.WishSeenForTest && Native.GetForegroundWindow() == game.Handle, 1000);
                game.ShowWishTransitionForTest(false); game.HeaderDetailsForTest = false;
                await Entry("unannounced Home entry with initially unfinished banner", () =>
                {
                    if (!game.OpenFromHomeForTest()) throw new InvalidOperationException("Synthetic Home entry failed.");
                });
                await Hold("first wish cover stays in place while banner details finish loading", () => Cover() && guard.WishSeenForTest, 700);
                game.HeaderDetailsForTest = true; game.Invalidate();
                await Hold("finishing the first banner never opens a gap", () => Cover() && guard.WishSeenForTest, 400);
                await Return("synthetic Escape after Home entry", () =>
                {
                    if (!game.ReturnFromEscapeForTest()) throw new InvalidOperationException("Synthetic Escape return failed.");
                });

                guard.ResourceDelayForTest = 8000;
                // Let an earlier resource request finish before the next entry,
                // so this round definitely uses the deliberately slow queue.
                await Check("previous resource read finishes before the occupied-worker round", () => !guard.ResourceBusyForTest, 15000);
                await Entry("unannounced mouse entry", game.OpenFromMouseForTest);
                await Check("deliberately slow resource worker is occupied", () => guard.ResourceBusyForTest, 5000);
                await Return("single synthetic close click while resource OCR is still occupied", game.ReturnFromMouseForTest, true);
                await Entry("unannounced mouse entry while resource OCR is occupied", game.OpenFromMouseForTest, true);
                long wrongScenesBefore = guard.SceneReadCountForTest, repeatedHitsBefore = guard.FastWishCountForTest;
                guard.SceneTextOverrideForTest = "元素战技 元素爆发";
                await Hold("repeated ordinary main-OCR mistakes cannot expose real painted wish buttons", () =>
                    game.WishForTest && game.WishPaintedAtForTest != 0 && Cover() && guard.WishSeenForTest && game.AttemptsForTest == 0, 3500);
                Record("at least three main-OCR results were submitted while fast positives kept arriving",
                    guard.SceneReadCountForTest >= wrongScenesBefore + 3 && guard.FastWishCountForTest >= repeatedHitsBefore + 2, 3500,
                    new { mainResultsBefore = wrongScenesBefore, mainResultsAfter = guard.SceneReadCountForTest,
                        fastHitsBefore = repeatedHitsBefore, fastHitsAfter = guard.FastWishCountForTest });
                guard.SceneTextOverrideForTest = null;
                await Return("synthetic Escape while resources remain independent", () =>
                {
                    if (!game.ReturnFromEscapeForTest()) throw new InvalidOperationException("Synthetic Escape return failed.");
                });
                guard.ResourceDelayForTest = 0;
                await Check("occupied resource queue finishes without disturbing normal play", () => !guard.ResourceBusyForTest && Normal(), 15000);

                guard.SceneDelayForTest = 1600;
                await Check("a real ordinary main-OCR result is waiting before submission", () =>
                    guard.SceneDelayBusyForTest && !guard.DelayedSceneWasWishForTest && Normal(), 10000);
                await Entry("unannounced mouse entry with an older normal frame in flight", game.OpenFromMouseForTest);
                guard.SceneDelayForTest = 0;
                await Hold("late ordinary main-OCR result cannot remove the new wish cover", () => Cover() && guard.WishSeenForTest && game.AttemptsForTest == 0, 1800);
                await Check("the held old ordinary result has actually finished", () => !guard.SceneDelayBusyForTest, 2500);
                await Return("single synthetic close click after old-frame rejection", game.ReturnFromMouseForTest);
                Record("all entries used the synthetic Home or mouse handlers and no shortcut indication", shortcutPolls > 0 &&
                    game.HomeEntriesForTest == 1 && game.MouseEntriesForTest == 3 && game.EscapeReturnsForTest == 2 && game.MouseReturnsForTest == 2 && game.AttemptsForTest == 0, 0,
                    new { shortcutIndications = 0, shortcutPolls, game.HomeEntriesForTest, game.MouseEntriesForTest,
                        game.EscapeReturnsForTest, game.MouseReturnsForTest });
                exit = 0;
            }
            catch (Exception error) { results.Add(new { name = "fast visual integration", pass = false, error = error.ToString(), state = Diagnostic() }); }
            finally
            {
                guard.SceneDelayForTest = guard.ResourceDelayForTest = 0; guard.SceneTextOverrideForTest = null;
                File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new
                {
                    passed = exit == 0, entryBudgetMs = EntryBudget, returnBudgetMs = ReturnBudget,
                    scope = "synthetic visual transitions and own-handler semantics only; no physical input or real game", results
                }, new JsonSerializerOptions { WriteIndented = true }));
                guard.Stop("隔离速度回归结束"); game.Close();
            }
        };
        Application.Run(game); return exit;
    }
}
#endif
