#if WISHGUARD_TESTS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WishGuard;

// Independent synthetic state, a disposable signing key, and virtual UTC only.
// Entries/returns invoke DemoGame's own handlers; no OS input is generated.
internal static class TemporaryVisualProbe
{
    const int ExpiryBudget = 600;
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);

    public static int Run(string freshRoot)
    {
        string root = Path.GetFullPath(freshRoot);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Use a fresh test directory.");
        Directory.CreateDirectory(root);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool shortcut = false, started = false, activationFocusRestored = false;
        int exit = 1;
        var results = new List<object>();
        using var game = new DemoGame(); game.ShowLoadingForTest(true);
        var store = new StateStore(Path.Combine(root, "state"));
        store.State.Committed = true; store.Save();
        string install = store.State.InstallId, policy = store.State.PolicyId, nonce = store.State.Nonce;
        using var guard = new GuardController(store, publicKey)
        {
            Strong = true, PassiveCoverForTest = true, DemoHandle = game.Handle,
            ShortcutForTest = () => shortcut, ClockForTest = () => now
        };

        bool Cover() => guard.CoverVisibleForTest && guard.CoverHandleForTest != 0 &&
            IsWindowVisible(guard.CoverHandleForTest) && !IsIconic(guard.CoverHandleForTest);
        bool NativeCoverHidden() => !guard.CoverVisibleForTest &&
            (guard.CoverHandleForTest == 0 || !IsWindowVisible(guard.CoverHandleForTest));
        bool OwnedForeground() => Native.GetForegroundWindow() == game.Handle ||
            guard.CoverHandleForTest != 0 && Native.GetForegroundWindow() == guard.CoverHandleForTest;
        bool Normal() => !game.WishForTest && NativeCoverHidden() && !guard.WishSeenForTest &&
            guard.Phase == "守护中" && guard.Running && Native.GetForegroundWindow() == game.Handle;
        bool AllowedWish() => game.WishForTest && NativeCoverHidden() && guard.Running && guard.TemporaryActive &&
            Native.GetForegroundWindow() == game.Handle && game.AttemptsForTest == 0;
        bool OriginalPlan() => store.State.Committed && store.State.TargetPulls == 600 && store.State.UnlockMode == "permanent" &&
            store.State.StartWithWindows && store.State.InstallId == install && store.State.PolicyId == policy && store.State.Nonce == nonce &&
            store.State.PermanentPass == null && !store.State.RecoveryFinished && !Rules.Ended(store.State, publicKey);
        object Diagnostic() => new
        {
            guard.Phase, guard.Running, guard.Ready, guard.TemporaryActive, guard.TemporaryRemaining,
            cover = Cover(), nativeCoverHidden = NativeCoverHidden(), wishSeen = guard.WishSeenForTest,
            fastReady = guard.FastReadyForTest, fastReads = guard.FastReadCountForTest,
            fastWishHits = guard.FastWishCountForTest, lastFastMilliseconds = guard.LastFastMillisecondsForTest,
            foreground = (long)Native.GetForegroundWindow(), game = (long)game.Handle, coverHandle = (long)guard.CoverHandleForTest,
            game.WishForTest, game.AttemptsForTest, temporaryActivatedAt = store.State.TemporaryActivatedAt,
            temporaryUntil = store.State.TemporaryUntil, usedPassCount = store.State.UsedPasses.Count, virtualNow = now
        };
        void Record(string name, bool pass, double milliseconds = 0, object? detail = null)
        {
            results.Add(new { name, pass, milliseconds = Math.Round(milliseconds, 2), detail, state = Diagnostic() });
            if (!pass) throw new InvalidOperationException(name);
        }
        async Task<double> Check(string name, Func<bool> predicate, int timeout = 10000)
        {
            var watch = Stopwatch.StartNew(); bool pass;
            do { pass = predicate(); if (pass) break; await Task.Delay(5); } while (watch.ElapsedMilliseconds < timeout);
            double milliseconds = watch.Elapsed.TotalMilliseconds;
            Record(name, pass, milliseconds);
            return milliseconds;
        }
        async Task Hold(string name, Func<bool> predicate, int duration)
        {
            var watch = Stopwatch.StartNew(); bool pass = true;
            do { if (!predicate()) { pass = false; break; } await Task.Delay(10); } while (watch.ElapsedMilliseconds < duration);
            if (pass) pass = predicate();
            Record(name, pass, watch.Elapsed.TotalMilliseconds);
        }

        game.Shown += async (_, _) =>
        {
            if (started) return; started = true;
            try
            {
                // Only startup establishes focus. Entry and expiry below never
                // activate a window or explicitly recheck/start protection.
                await Task.Delay(100); game.Hide(); game.Show(); game.Activate(); guard.DemoHandle = game.Handle;
                await guard.Start();
                await Check("the independent fast OCR worker is ready", () => guard.FastReadyForTest, 30000);
                game.Activate();
                await Check("the synthetic loading window owns foreground", () => Native.GetForegroundWindow() == game.Handle);
                game.ShowLoadingForTest(false);
                await Check("ordinary synthetic play is stable before entry", Normal, 15000);
                await Hold("ordinary play stays clear without a shortcut indication", Normal, 400);

                long hitsBeforeEntry = guard.FastWishCountForTest;
                game.OpenFromMouseForTest();
                await Check("unannounced synthetic mouse entry is natively covered", () =>
                    game.WishForTest && Cover() && guard.WishSeenForTest && guard.FastWishCountForTest > hitsBeforeEntry, 2500);
                long painted = game.WishPaintedAtForTest;
                Record("the wish buttons actually painted without a synthetic wish attempt", painted != 0 && game.AttemptsForTest == 0);
                Record("the original 600-pull plan remains active before approval", OriginalPlan());

                string id = "temporary-visual-" + Guid.NewGuid().ToString("N");
                var grant = new Grant("wishguard-v2", install, policy, nonce, id, now - 1, now + 86399,
                    0, "temporary", 1800, "隔离回归批准：仅模拟窗口的一次性 30 分钟临时放行。");
                byte[] payload = JsonSerializer.SerializeToUtf8Bytes(grant);
                string token = JsonSerializer.Serialize(new Envelope(Rules.Encode(payload),
                    Rules.Encode(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))));
                long activated = now;
                guard.ImportTemporary(token);
                await Check("activation hides the real cover without leaving the wish scene", () =>
                    NativeCoverHidden() && game.WishForTest && guard.Running && guard.TemporaryActive && game.WishPaintedAtForTest == painted, 1000);
                Record("activation preserves the exact 1800-second deadline and single consumption",
                    store.State.TemporaryActivatedAt == activated && store.State.TemporaryUntil == activated + 1800 &&
                    store.State.UsedPasses.Count == 1 && store.State.UsedPasses.Count(value => value == id) == 1 && OriginalPlan());

                if (Native.GetForegroundWindow() != game.Handle)
                {
                    // A hiding native form can leave a hidden owner foreground.
                    // Record this separately and simulate one user focus return;
                    // no such correction is permitted at expiry below.
                    Record("activation's native focus handoff is recorded before a single correction", NativeCoverHidden() && game.WishForTest,
                        detail: new { foregroundBeforeCorrection = (long)Native.GetForegroundWindow(), correction = "own synthetic window Activate once" });
                    activationFocusRestored = true; game.Activate();
                }
                await Check("the authorized wish scene is foreground and clear", AllowedWish, 1500);
                await Hold("the temporary window leaves the same wish scene continuously clear", AllowedWish, 700);
                Record("the entry shortcut is unregistered during temporary approval", !guard.ShortcutRegisteredForTest);
                shortcut = true;
                await Hold("a default-shortcut indication cannot restore the cover during approval", AllowedWish, 500);
                shortcut = false;

                now = activated + 1799;
                await Hold("the final authorized second remains clear and running", () => AllowedWish() && guard.TemporaryRemaining == 1, 600);
                Record("expiry begins from the same untouched foreground wish page", AllowedWish() && game.WishPaintedAtForTest == painted);
                long hitsBeforeExpiry = guard.FastWishCountForTest;
                now = activated + 1800;
                double expiryMilliseconds = await Check("expiry restores the native cover and fresh fast-positive confirmation within 600 ms", () =>
                    Cover() && guard.WishSeenForTest && guard.FastWishCountForTest > hitsBeforeExpiry &&
                    !guard.TemporaryActive && guard.Running && game.WishForTest && OwnedForeground(),
                    ExpiryBudget);
                Record("expiry never pauses or completes the original plan", expiryMilliseconds <= ExpiryBudget && OriginalPlan(),
                    expiryMilliseconds, new { budgetMs = ExpiryBudget });
                await Hold("the expired wish page remains natively covered", () => Cover() && guard.WishSeenForTest && !guard.TemporaryActive &&
                    guard.Running && game.WishForTest && OwnedForeground(), 400);

                Record("the synthetic Escape handler returns without wishing", game.ReturnFromEscapeForTest() && game.AttemptsForTest == 0);
                await Check("ordinary play restores after the expired temporary window", Normal, 5000);
                await Hold("ordinary play remains clear after expiry", Normal, 400);
                long? deadline = store.State.TemporaryUntil, activationTime = store.State.TemporaryActivatedAt;
                bool rejected = false;
                try { guard.ImportTemporary(token); }
                catch (InvalidDataException) { rejected = true; }
                Record("the consumed temporary token cannot be imported again", rejected);
                Record("rejected replay cannot move activation or deadline", store.State.TemporaryUntil == deadline &&
                    store.State.TemporaryActivatedAt == activationTime && !guard.TemporaryActive);
                var saved = new StateStore(store.DirectoryPath);
                Record("single consumption and fixed timestamps are persisted", !saved.Fault && saved.State.UsedPasses.Count == 1 &&
                    saved.State.UsedPasses.Count(value => value == id) == 1 && saved.State.TemporaryActivatedAt == activationTime &&
                    saved.State.TemporaryUntil == deadline && !Rules.TemporaryActive(saved.State, publicKey, now));
                Record("the 600-pull permanent plan and its configured lifecycle remain active", OriginalPlan() &&
                    game.MouseEntriesForTest == 1 && game.HomeEntriesForTest == 0 && game.EscapeReturnsForTest == 1 &&
                    game.MouseReturnsForTest == 0 && game.AttemptsForTest == 0);
                exit = 0;
            }
            catch (Exception error)
            { results.Add(new { name = "temporary visual integration", pass = false, error = error.ToString(), state = Diagnostic() }); }
            finally
            {
                shortcut = false;
                try
                {
                    File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new
                    {
                        passed = exit == 0, expiryBudgetMs = ExpiryBudget, activationFocusRestored,
                        scope = "synthetic native cover, own-handler entry/return, disposable signing key, virtual UTC only; no real input or game", results
                    }, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally { guard.Stop("隔离临时放行回归结束"); game.Close(); }
            }
        };
        Application.Run(game); return exit;
    }
}
#endif
