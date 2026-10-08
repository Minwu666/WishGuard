# Local, persistent Windows OCR worker. No network, game API or game files.
$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
    $null = [Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]
    $null = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
    $null = [Windows.Graphics.Imaging.SoftwareBitmap,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
    $null = [Windows.Storage.Streams.InMemoryRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
    $null = [Windows.Storage.Streams.DataWriter,Windows.Storage.Streams,ContentType=WindowsRuntime]
    $taskMethod = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and
        $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    function Await($operation, $resultType) {
        $task = $taskMethod.MakeGenericMethod($resultType).Invoke($null, @($operation))
        $task.GetAwaiter().GetResult()
    }
    $languageTags = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
    $language = $languageTags | Where-Object { $_ -like 'zh-Hans*' } | Select-Object -First 1
    if (-not $language) { $language = $languageTags | Where-Object { $_ -like 'en-*' } | Select-Object -First 1 }
    if (-not $language) { throw 'No Chinese/English Windows OCR language is installed.' }
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($language))
    if ($null -eq $engine) { throw 'Windows OCR could not be initialized.' }
    [Console]::WriteLine((@{ready=$true;language=$language;languages=$languageTags} | ConvertTo-Json -Compress))
    while ($null -ne ($line = [Console]::ReadLine())) {
        $stream = $writer = $bitmap = $null
        try {
            $bytes = [Convert]::FromBase64String($line)
            $stream = [Windows.Storage.Streams.InMemoryRandomAccessStream]::new()
            $writer = [Windows.Storage.Streams.DataWriter]::new($stream)
            $writer.WriteBytes($bytes)
            $null = Await ($writer.StoreAsync()) ([uint32])
            $null = $writer.DetachStream()
            $stream.Seek(0)
            $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            $bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
            $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
            $words = @($result.Lines | ForEach-Object { $_.Words } | ForEach-Object {
                @{ text=$_.Text; x=$_.BoundingRect.X; y=$_.BoundingRect.Y; width=$_.BoundingRect.Width; height=$_.BoundingRect.Height }
            })
            [Console]::WriteLine((@{ok=$true;text=$result.Text;words=$words} | ConvertTo-Json -Compress -Depth 5))
        } catch {
            [Console]::WriteLine((@{ok=$false;error=$_.Exception.Message} | ConvertTo-Json -Compress))
        } finally {
            if ($bitmap) { $bitmap.Dispose() }
            if ($writer) { $writer.Dispose() }
            if ($stream) { $stream.Dispose() }
        }
    }
} catch {
    [Console]::WriteLine((@{ready=$false;error=$_.Exception.Message} | ConvertTo-Json -Compress))
    exit 1
}
