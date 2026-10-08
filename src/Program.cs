namespace WishGuard;
static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--sentinel")) { Thread.Sleep(12000); return 0; }
        if (args.Contains("--self-test")) return SelfTest.Run(args).GetAwaiter().GetResult();
        if (GetArg(args, "--analyze-image") is { } image) return SelfTest.Analyze(image, GetArg(args, "--report")!).GetAwaiter().GetResult();
        bool demo = args.Contains("--demo"), isolated = args.Contains("--isolated"), watch = args.Contains("--watch");
        var options = new RuntimeOptions(demo, isolated || demo, args.Contains("--session"), args.Contains("--no-watch") || demo, args.Contains("--autostart"),
            isolated ? GetArg(args, "--test-process") : null);
        string data = GetArg(args, "--data") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), demo ? "WishGuard-Demo" : "WishGuard");
        if (isolated) Application.ThreadException += (_, e) => new StateStore(data).Log("test_unhandled", e.Exception.ToString());
        using var mutex = new Mutex(true, Lifecycle.MutexName(data, watch), out bool first);
        if (!first) { if (!watch) MessageBox.Show("攒愿已在运行，请从系统托盘打开。", "攒愿"); return 0; }
        try
        {
            string keyFile = Path.Combine(AppContext.BaseDirectory, "authority-public.txt");
            string publicKey = File.Exists(keyFile) ? File.ReadAllText(keyFile).Trim() : "";
            if (!demo)
            {
                if (string.IsNullOrWhiteSpace(publicKey)) throw new IOException("尚未配置核验公钥。请先阅读使用说明，由可信核验人生成独立密钥；也可用 --demo 体验模拟界面。");
                using var key = System.Security.Cryptography.ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            }
            var state = new StateStore(data);
            if ((watch || options.Session) && !state.Fault && Rules.Ended(state.State, publicKey))
            { Lifecycle.Disable(data, options.Isolated); state.Log("startup_skipped", "计划已结束，不再随原神启动"); return 0; }
            if (watch) Application.Run(new WatcherContext(data, publicKey, options.Isolated, options.TestProcess));
            else { using var form = new MainForm(state, publicKey, options); Application.Run(form); }
            return 0;
        }
        catch (Exception e) { if (!watch) MessageBox.Show("攒愿未能启动：" + e.Message, "启动失败"); return 1; }
    }
    public static string? GetArg(string[] args, string name)
    { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
}
