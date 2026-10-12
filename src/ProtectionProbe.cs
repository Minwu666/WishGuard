#if WISHGUARD_TESTS
using System.Text.Json;
namespace WishGuard;

// Compiled only into isolated integration-test builds, never the shipped client.
internal static class ProtectionProbe
{
    public static async Task<int> Run(string file, string[] args)
    {
        var profile = JsonSerializer.Deserialize<EnforcementProfile>(File.ReadAllText(file))!;
        if (!profile.TestOnly || profile.GamePaths.Length != 1 || Path.GetFileName(profile.GamePaths[0]) != "WishGuardProbeGame.exe") return 3;
        using var client = new EnforcementClient(profile);
        using var input = new InputBrokerClient(profile);
        string control = Program.GetArg(args, "--control")!, report = control + ".reply";
        bool healthy = true; string? last = null;
        for (int n = 0; n < 2000; n++)
        {
            try
            {
                if (File.Exists(control))
                {
                    string text = File.ReadAllText(control);
                    if (text != last)
                    {
                        var request = JsonSerializer.Deserialize<ProtectionRequest>(text)!;
                        last = text;
                        try
                        {
                            if (request.Command == "input-disconnect")
                            {
                                input.Dispose();
                                File.WriteAllText(report, JsonSerializer.Serialize(new ProtectionReply(true, false, null, null, false, "")));
                                continue;
                            }
                            if (request.Command == "input-poll")
                            {
                                var result = await input.Poll(0, true);
                                File.WriteAllText(report, JsonSerializer.Serialize(new ProtectionReply(result.Entry == 0 && !result.Focused, false, null, null, false, "")));
                                continue;
                            }
                            if (request.Command == "health") healthy = request.Healthy;
                            var reply = await client.Send(request.Command, request.Healthy, request.Token);
                            File.WriteAllText(report, JsonSerializer.Serialize(reply));
                        }
                        catch (Exception e) { File.WriteAllText(report, JsonSerializer.Serialize(new { Ok = false, e.Message })); }
                    }
                }
                if (client.Completed) return 0;
                await client.Send("health", healthy);
                File.WriteAllText(control + ".ready", "ready");
            }
            catch (Exception e) { File.WriteAllText(control + ".error", e.ToString()); }
            await Task.Delay(150);
        }
        return 0;
    }
}
#endif
