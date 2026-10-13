namespace WishGuard;
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--sentinel")) { Thread.Sleep(12000); return 0; }
        if (args.Contains("--self-test")) return SelfTest.Run(args).GetAwaiter().GetResult();
#if WISHGUARD_TESTS
        if (GetArg(args, "--protection-probe") is { } probe) return ProtectionProbe.Run(probe, args).GetAwaiter().GetResult();
        if (GetArg(args, "--visual-probe") is { } visual) return ProtectionVisualProbe.Run(visual);
        if (GetArg(args, "--fast-visual-probe") is { } fastVisual) return FastVisualProbe.Run(fastVisual);
        if (GetArg(args, "--return-demo") is { } demoControl) return ReturnProbe.Demo(demoControl);
        if (GetArg(args, "--return-probe") is { } returns) return ReturnProbe.Run(returns, GetArg(args,"--enforcer")!);
        if (GetArg(args, "--background-probe") is { } background) return BackgroundProbe.Run(background, GetArg(args,"--enforcer")!);
        if (GetArg(args, "--pointer-demo") is { } pointerDemo) return PointerProbe.Demo(pointerDemo);
        if (GetArg(args, "--pointer-probe") is { } pointers) return PointerProbe.Run(pointers, GetArg(args,"--enforcer")!);
#endif
        if (GetArg(args, "--analyze-image") is { } image) return SelfTest.Analyze(image, GetArg(args, "--report")!).GetAwaiter().GetResult();
        if (GetArg(args, "--analyze-fast-image") is { } fastImage) return FastWishVision.Analyze(fastImage, GetArg(args, "--report")!).GetAwaiter().GetResult();
        bool demo = args.Contains("--demo"), isolated = args.Contains("--isolated"), watch = args.Contains("--watch");
        var options = new RuntimeOptions(demo, isolated || demo, args.Contains("--session"), args.Contains("--no-watch") || demo, args.Contains("--autostart"),
            isolated ? GetArg(args, "--test-process") : null);
        string data = GetArg(args, "--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), demo ? "WishGuard-Demo" : "WishGuard");
        if (isolated) Application.ThreadException += (_, e) => new StateStore(data).Log("test_unhandled", e.Exception.ToString());
        try
        {
            var enforcement = options.Isolated ? null : EnforcementPaths.Current();
            if (enforcement != null && !enforcement.TestOnly && !string.Equals(Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(enforcement.ClientDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            {
                var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(enforcement.ClientDirectory, "WishGuard.exe")) { UseShellExecute = false };
                foreach (string arg in args) info.ArgumentList.Add(arg);
                using var child = System.Diagnostics.Process.Start(info); return 0;
            }
            if (GetArg(args, "--check-protection") is { } report)
            {
                bool reached = false, inputReached = false; string? error = null;
                try
                {
                    if (enforcement is { Completed: false })
                    {
                        using var client = new EnforcementClient(enforcement); reached = client.Send("status").GetAwaiter().GetResult().Ok;
                        using var input = new InputBrokerClient(enforcement); input.Poll(0, false).GetAwaiter().GetResult(); inputReached = true;
                    }
                }
                catch (Exception e) { error = e.GetType().Name; }
                File.WriteAllText(report, System.Text.Json.JsonSerializer.Serialize(new { installed = enforcement != null, reached, inputReached, completed = enforcement?.Completed, error }));
                return error == null ? 0 : 1;
            }
            using var mutex = new Mutex(true, Lifecycle.MutexName(data, watch), out bool first);
            if (!first) { if (!watch) MessageBox.Show("攒愿已在运行，请从系统托盘打开。", "攒愿"); return 0; }
            string keyFile = Path.Combine(AppContext.BaseDirectory, "authority-public.txt");
            string publicKey = File.Exists(keyFile) ? File.ReadAllText(keyFile).Trim() : "";
            if (!demo)
            {
                if (string.IsNullOrWhiteSpace(publicKey)) throw new IOException("尚未配置核验公钥。请先阅读使用说明，由可信核验人生成独立密钥；也可用 --demo 体验模拟界面。");
                using var key = System.Security.Cryptography.ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            }
            var state = new StateStore(data);
            if (enforcement != null) EnforcementPaths.Apply(state, enforcement);
            if ((watch || options.Session) && !state.Fault && Rules.Ended(state.State, publicKey))
            { Lifecycle.Disable(data, options.Isolated); state.Log("startup_skipped", "计划已结束，不再随原神启动"); return 0; }
            if (watch) Application.Run(new WatcherContext(data, publicKey, options.Isolated, options.TestProcess));
            else { using var form = new MainForm(state, publicKey, options, enforcement); Application.Run(form); }
            return 0;
        }
        catch (Exception e)
        {
            try { new StateStore(data).Log("startup_error", e.ToString()); } catch { }
            if (!watch) MessageBox.Show("攒愿未能启动：" + e.Message, "启动失败"); return 1;
        }
    }
    public static string? GetArg(string[] args, string name)
    { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}
