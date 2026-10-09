using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Xml.Linq;
namespace WishGuard;

public static class StartupTask
{
    const string Source = "WishGuard game watcher";
    static string UserSid => WindowsIdentity.GetCurrent().User!.Value;
    public static string Name(string data) => "WishGuard-" + Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(UserSid + "|" + Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant())))[..20];

    public static string Definition(string executable, string data, string sid, bool isolated = false, string? testProcess = null)
    {
        // Windows paths cannot contain quotes. Normalize away a trailing backslash before quoting.
        string args = "--watch --data \"" + Path.GetFullPath(data).TrimEnd(Path.DirectorySeparatorChar) + "\"";
        if (isolated) args += " --isolated";
        if (testProcess != null) args += " --test-process \"" + testProcess.Replace("\"", "") + "\"";
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object? value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", new[] { E("Description", "攒愿：登录后等待原神启动；计划结束后自动移除。"), E("Source", Source) }),
            E("Triggers", E("LogonTrigger", new[] { E("Enabled", true), E("UserId", sid), E("Delay", "PT10S") })),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "User"),
                E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "LeastPrivilege"))),
            E("Settings", new[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", false),
                E("StopIfGoingOnBatteries", false), E("StartWhenAvailable", true), E("AllowStartOnDemand", true),
                E("Enabled", true), E("ExecutionTimeLimit", "PT0S"),
                E("RestartOnFailure", new[] { E("Interval", "PT1M"), E("Count", 3) }) }),
            new XElement(ns + "Actions", new XAttribute("Context", "User"), E("Exec", new[] {
                E("Command", Path.GetFullPath(executable)), E("Arguments", args),
                E("WorkingDirectory", Path.GetDirectoryName(Path.GetFullPath(executable))!) }))))
            .ToString();
    }
    static void WithFolder(Action<dynamic> action)
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        object? folder = null;
        try { service.Connect(); folder = service.GetFolder("\\"); action(folder); }
        finally
        {
            if (folder != null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(service);
        }
    }
    public static void Install(string data, string executable, bool isolated = false, string? testProcess = null)
        => WithFolder(folder =>
        {
            try
            {
                dynamic existing = folder.GetTask(Name(data));
                try { VerifyOwned((string)existing.Xml); }
                finally { Marshal.FinalReleaseComObject(existing); }
            }
            catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { }
            object task = folder.RegisterTask(Name(data), Definition(executable, data, UserSid, isolated, testProcess),
                6 /* create or update */, UserSid, null, 3 /* current interactive user */, null);
            Marshal.FinalReleaseComObject(task);
        });
    public static void Start(string data) => WithFolder(folder =>
    {
        dynamic task = folder.GetTask(Name(data));
        try { object running = task.Run(null); Marshal.FinalReleaseComObject(running); }
        finally { Marshal.FinalReleaseComObject(task); }
    });
    public static void Remove(string data) => WithFolder(folder =>
    {
        try
        {
            dynamic task = folder.GetTask(Name(data));
            try
            {
                VerifyOwned((string)task.Xml);
                folder.DeleteTask(Name(data), 0);
            }
            finally { Marshal.FinalReleaseComObject(task); }
        }
        catch (Exception e) when (e.HResult == unchecked((int)0x80070002)) { }
    });
    static void VerifyOwned(string definition)
    {
        var xml = XDocument.Parse(definition);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        if ((string?)xml.Root?.Element(ns + "RegistrationInfo")?.Element(ns + "Source") != Source)
            throw new InvalidOperationException("同名计划任务不属于攒愿，未修改。");
    }
}
