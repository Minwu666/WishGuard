#if WISHGUARD_TESTS
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace WishGuard;

// Interactive isolated regression. Computer Use supplies actual key/click input
// to this fake game; no input is synthesized by the test or the application.
internal static class ReturnProbe
{
    public static int Demo(string control)
    {
        using var game = new DemoGame(); game.ShowWishForTest(true);
        using var timer = new System.Windows.Forms.Timer { Interval = 100 };
        string? previous = null;
        timer.Tick += (_,_) => {
            try {
            string current = "";
            if (File.Exists(control))
            {
                using var file = new FileStream(control, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file); current = reader.ReadToEnd();
            }
            if(current != previous) { previous = current; if(current.StartsWith("wish"))game.ShowWishForTest(true); }
            File.WriteAllText(control+".state",JsonSerializer.Serialize(new{Wish=game.WishForTest,Attempts=game.AttemptsForTest}));
            } catch(IOException) { /* The observer may be reading; retry next tick. */ }
        };
        timer.Start(); Application.Run(game); return 0;
    }
    public static int Run(string root, string enforcer)
    {
        root = Path.GetFullPath(root); enforcer = Path.GetFullPath(enforcer);
        if(Directory.Exists(root))throw new IOException("Use a fresh directory.");
        Directory.CreateDirectory(root); string games=Path.Combine(root,"game");Directory.CreateDirectory(games);
        foreach(string file in Directory.GetFiles(AppContext.BaseDirectory))File.Copy(file,Path.Combine(games,Path.GetFileName(file)));
        string fake=Path.Combine(games,"WishGuardProbeGame.exe");File.Copy(Path.Combine(games,"WishGuard.exe"),fake);
        string control=Path.Combine(root,"control"), fileProfile=Path.Combine(root,"profile.json");
        var profile=new EnforcementProfile{OwnerSid=EnforcementPaths.Owner,ClientDirectory=AppContext.BaseDirectory,DataDirectory=Path.Combine(root,"data"),
            EnforcerExecutable=enforcer,PipeName="WishGuard-return-"+Guid.NewGuid().ToString("N"),GamePaths=[fake],TestOnly=true,Policy=new GuardState{Committed=true}};
        foreach(string name in new[]{"WishGuard.exe","WishGuard.dll","WishGuard.deps.json","WishGuard.runtimeconfig.json","ocr-worker.ps1"})
            profile.ClientHashes[name]=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,name))));
        EnforcementPaths.Save(fileProfile,profile);
        Process Start(string exe,params string[] args){var info=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true};foreach(var a in args)info.ArgumentList.Add(a);return Process.Start(info)!;}
        using var host=new Form{ShowInTaskbar=false,Opacity=0};
        using var guard=new GuardController(new StateStore(profile.DataDirectory),""){Strong=true};guard.UseInputBroker(profile);
        Process? game=null,broker=null;int exit=1;var checks=new List<object>();
        async Task Check(string name,Func<bool> condition,int ms=20000)
        {
            var time=Stopwatch.StartNew();while(!condition()&&time.ElapsedMilliseconds<ms)await Task.Delay(50);
            bool pass=condition();checks.Add(new{name,pass});if(!pass)throw new Exception(name+": "+guard.EnforcementStatus+"; "+guard.Phase);
        }
        bool Normal(){try{using var state=JsonDocument.Parse(File.ReadAllText(control+".state"));return !state.RootElement.GetProperty("Wish").GetBoolean()&&state.RootElement.GetProperty("Attempts").GetInt32()==0;}catch{return false;}}
        host.Shown+=async(_,_)=>{
            host.Hide();try{
                broker=Start(enforcer,"--input-profile",fileProfile);game=Start(fake,"--return-demo",control);
                await Check("fake game window opens",()=>{game.Refresh();return game.MainWindowHandle!=0;});
                guard.DemoHandle=game.MainWindowHandle;await guard.Start();
                await Check("passive cover keeps game foreground and protects keys",()=>guard.CoverVisibleForTest&&guard.WishSeenForTest&&guard.EnforcementHealthy&&Native.GetForegroundWindow()==guard.DemoHandle,120000);
                File.WriteAllText(Path.Combine(root,"stage"),"keyboard");
                await Check("Escape closes wish without confirming a wish",Normal,120000);
                await Check("Escape restores normal gameplay",()=>!guard.CoverVisibleForTest&&guard.Phase=="守护中");
                File.WriteAllText(control,"wish-2");
                await Check("second wish is covered",()=>guard.CoverVisibleForTest&&guard.WishSeenForTest&&guard.EnforcementHealthy);
                File.WriteAllText(Path.Combine(root,"stage"),"mouse");
                await Check("one close-button click returns without confirming a wish",Normal,120000);
                await Check("mouse return restores normal gameplay",()=>!guard.CoverVisibleForTest&&guard.Phase=="守护中");
                await Check("fake game remains running",()=>!game.HasExited);exit=0;
            }catch(Exception e){checks.Add(new{error=e.ToString()});}
            finally{
                guard.Dispose();foreach(var process in new[]{game,broker}){if(process!=null){try{if(!process.HasExited)process.Kill();}catch{}process.Dispose();}}
                File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(new{passed=exit==0,checks}));host.Close();
            }
        };
        Application.Run(host);return exit;
    }
}
#endif
