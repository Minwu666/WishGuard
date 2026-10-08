using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WishGuard;

public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);
public sealed record OcrResult(string Text, OcrWord[] Words);

public static class WishText
{
    // Require a wish button, not just the word "wish" in a quest or dialogue.
    public static bool IsWish(string text)
    {
        var s = Regex.Replace(text.Normalize(NormalizationForm.FormKC).ToLowerInvariant(), @"[\s·•×x:：,，.。\-]", "");
        return Regex.IsMatch(s, @"(?:祈愿|祈願|祈原|祈頤)(?:1|10|一|十|l|i|lo|io)次") ||
               Regex.IsMatch(s, @"wish(?:1|10)(?!\d)");
    }
}

public sealed class OcrWorker : IDisposable
{
    Process? process;
    public string Language { get; private set; } = "";
    public async Task Start()
    {
        Dispose();
        // Execute our reviewed script as a command; no persistent execution-policy change.
        string script = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ocr-worker.ps1"));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        process = Process.Start(start) ?? throw new IOException("无法启动本地 OCR。");
        process.ErrorDataReceived += (_, _) => { }; // Drain diagnostics without keeping screenshots/text.
        process.BeginErrorReadLine();
        try
        {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(18));
            using var j = JsonDocument.Parse(line ?? "{}");
            if (!j.RootElement.TryGetProperty("ready", out var r) || !r.GetBoolean())
                throw new IOException("Windows OCR 不可用。请在 Windows 中安装简体中文 OCR 语言组件。");
            Language = j.RootElement.GetProperty("language").GetString() ?? "";
        }
        catch { Dispose(); throw; }
    }
    public async Task<string> Read(Bitmap bitmap) => (await ReadResult(bitmap)).Text;
    public async Task<OcrResult> ReadResult(Bitmap bitmap)
    {
        if (process == null || process.HasExited) throw new IOException("本地 OCR 已停止，请重启守护。");
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        try
        {
            await process.StandardInput.WriteLineAsync(Convert.ToBase64String(stream.ToArray()));
            await process.StandardInput.FlushAsync();
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(4));
            using var j = JsonDocument.Parse(line ?? "{}");
            if (!j.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean()) throw new IOException("画面识别失败。");
            return JsonSerializer.Deserialize<OcrResult>(j.RootElement.GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new IOException("OCR 返回了无效结果。");
        }
        catch { Dispose(); throw; }
    }
    public void Dispose()
    {
        if (process != null)
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            process.Dispose(); process = null;
        }
    }
}
