[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$InstallDirectory,
    [Parameter(Mandatory)][string]$DataDirectory,
    [Parameter(Mandatory)][string]$GamePath,
    [Parameter(Mandatory)][string]$OwnerSid,
    [Parameter(Mandatory)][switch]$AcceptGameExit,
    [switch]$AllowGameNotInstalled,
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ Success = $false; Version = '0.3.0'; PolicyPreserved = $false }
$changed = $false; $oldTasks = @{}
function Is-MissingTask($record) {
    $error = $record.Exception
    while ($error) { if ($error.HResult -eq -2147024894) { return $true }; $error = $error.InnerException }
    return $false
}
try {
    if (!$AcceptGameExit) { throw 'Explicit consent to game exit is required.' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    if (!([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this installer through Windows administrator confirmation.'
    }
    $owner = [Security.Principal.SecurityIdentifier]::new($OwnerSid)
    $null = $owner.Translate([Security.Principal.NTAccount])
    $BundleDirectory = [IO.Path]::GetFullPath($BundleDirectory).TrimEnd('\')
    $InstallDirectory = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    $DataDirectory = [IO.Path]::GetFullPath($DataDirectory).TrimEnd('\')
    $GamePath = [IO.Path]::GetFullPath($GamePath)
    if ([IO.Path]::GetFileName($GamePath) -notin @('YuanShen.exe','GenshinImpact.exe')) { throw 'Select the Genshin executable.' }
    $gameInstalled = Test-Path -LiteralPath $GamePath -PathType Leaf
    if (!$gameInstalled -and !$AllowGameNotInstalled) { throw 'The selected game is not installed. Confirm the future location explicitly before proceeding.' }
    # Display preferences only. Do not change the game's settings from the installer.
    $displayKey = 'Registry::HKEY_USERS\' + $OwnerSid + '\Software\miHoYo\原神'
    if ([IO.Path]::GetFileName($GamePath) -eq 'YuanShen.exe' -and (Test-Path -LiteralPath $displayKey)) {
        $display = Get-ItemProperty -LiteralPath $displayKey
        if ($display.'Screenmanager Is Fullscreen mode_h3981298716' -eq 1) {
            throw '请先在原神中切换为窗口模式并退出游戏，再安装强制模式。当前全屏配置可能导致游戏反复最小化。'
        }
    }
    if (!(Test-Path -LiteralPath (Join-Path $InstallDirectory 'WishGuard.exe'))) { throw 'Existing WishGuard installation is required.' }
    $stateFile = Join-Path $DataDirectory 'state.json'
    $state = Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$state.Committed -or $state.TargetPulls -ne 600 -or $state.UnlockMode -ne 'permanent' -or $state.RecoveryFinished -or $state.PermanentPass) { throw 'An active 600-pull plan is required.' }
    # A process snapshot remains readable even when an elevated game's executable
    # path is unavailable through WMI. Never replace protection during gameplay.
    $game = @(Get-Process YuanShen,GenshinImpact -ErrorAction SilentlyContinue)
    if ($game.Count) { throw 'Please exit Genshin normally before installing.' }
    function Get-Key([string]$text) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text)))).Replace('-','').Substring(0,20) }
        finally { $sha.Dispose() }
    }
    $id = Get-Key $OwnerSid
    $accountRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) ('WishGuard\' + $id)
    $programRoot = Join-Path $accountRoot ('0.3.0-' + [Guid]::NewGuid().ToString('N'))
    $client = Join-Path $programRoot 'Client'
    $enforcer = Join-Path $programRoot 'Enforcer'
    $policyDir = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) ('WishGuard\Strong\' + $id)
    $policyFile = Join-Path $policyDir 'profile.json'
    $strongTask = 'WishGuard-Strong-' + $id
    $inputTask = $strongTask + '-Input'
    $watchTask = 'WishGuard-' + (Get-Key ($OwnerSid + '|' + $DataDirectory.ToLowerInvariant()))
    $clientFiles = @('WishGuard.exe','WishGuard.dll','WishGuard.deps.json','WishGuard.runtimeconfig.json','ocr-worker.ps1','使用说明.md')
    $enforcerFiles = @('WishGuard.Enforcer.exe','WishGuard.Enforcer.dll','WishGuard.Enforcer.deps.json','WishGuard.Enforcer.runtimeconfig.json')
    foreach ($name in $clientFiles) { if (!(Test-Path -LiteralPath (Join-Path $BundleDirectory ('Client\' + $name)) -PathType Leaf)) { throw ('Missing client file: ' + $name) } }
    foreach ($name in $enforcerFiles) { if (!(Test-Path -LiteralPath (Join-Path $BundleDirectory ('Enforcer\' + $name)) -PathType Leaf)) { throw ('Missing protection file: ' + $name) } }
    $assemblyText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $BundleDirectory 'Client\WishGuard.dll')))
    if ($assemblyText.Contains('ProtectionVisualProbe') -or $assemblyText.Contains('ProtectionProbe')) { throw 'Test builds must never be installed.' }
    # CLR type/method names are ASCII metadata; diagnostic file paths can be
    # UTF-16 literals, so use the compiled diagnostic names as the stable marker.
    $enforcerAssemblyText = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $BundleDirectory 'Enforcer\WishGuard.Enforcer.dll')))
    if ($enforcerAssemblyText.Contains('PointerTrace')) { throw 'Protection test builds must never be installed.' }
    $keyFile = Join-Path $InstallDirectory 'authority-public.txt'
    $keyHash = (Get-FileHash -LiteralPath $keyFile).Hash
    $publicKey = [IO.File]::ReadAllText($keyFile).Trim()
    $oldPolicy = $null
    if (Test-Path -LiteralPath $policyFile) {
        $oldPolicy = Get-Content -LiteralPath $policyFile -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($oldPolicy.Completed -or $oldPolicy.OwnerSid -ne $OwnerSid -or $oldPolicy.Policy.InstallId -ne $state.InstallId -or $oldPolicy.PublicKey -ne $publicKey) { throw 'Existing protected plan cannot be replaced.' }
        $state = $oldPolicy.Policy
    }
    $service = New-Object -ComObject 'Schedule.Service'; $service.Connect(); $folder = $service.GetFolder('\')
    foreach ($item in @(@($strongTask,'WishGuard strong protection'),@($watchTask,'WishGuard game watcher'),@($inputTask,'WishGuard input compatibility'))) {
        try {
            $existing = $folder.GetTask($item[0]); if ($existing.Definition.RegistrationInfo.Source -ne $item[1]) { throw 'Task name belongs to another application.' }
            if ($item[0] -eq $watchTask) {
                if ($existing.Definition.Principal.RunLevel -ne 0) { throw 'The ordinary watcher must not run elevated.' }
            } else {
                if (!$oldPolicy) { throw 'A pre-existing protected task requires a verified installed profile.' }
                $mode = if ($item[0] -eq $inputTask) { '--input-profile' } else { '--profile' }
                $expectedArguments = $mode + ' "' + $policyFile + '"'
                if ($existing.Definition.Actions.Count -ne 1 -or $existing.Definition.Actions.Item(1).Path -ne $oldPolicy.EnforcerExecutable -or $existing.Definition.Actions.Item(1).Arguments -ne $expectedArguments) {
                    throw 'Existing protected task does not match the installed component.'
                }
            }
            $oldTasks[$item[0]] = @{ Xml=$existing.Xml; Sddl=$existing.GetSecurityDescriptor(7) }
        }
        catch { if (!(Is-MissingTask $_)) { throw } }
    }
    # Only our existing task and exact executable locations are stopped, with the game already closed.
    $changed = $true
    foreach ($name in @($strongTask,$watchTask,$inputTask)) {
        try { $folder.GetTask($name).Stop(0) } catch { if (!(Is-MissingTask $_)) { throw } }
    }
    $allowed = @((Join-Path $InstallDirectory 'WishGuard.exe'),(Join-Path $client 'WishGuard.exe'),(Join-Path $enforcer 'WishGuard.Enforcer.exe'))
    if ($oldPolicy) {
        foreach ($path in @((Join-Path $oldPolicy.ClientDirectory 'WishGuard.exe'),$oldPolicy.EnforcerExecutable)) {
            if (![IO.Path]::GetFullPath($path).StartsWith($accountRoot + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Previous protected location is invalid.' }
            $allowed += $path
        }
    }
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='WishGuard.exe' OR Name='WishGuard.Enforcer.exe'" | Where-Object { $_.ExecutablePath -in $allowed })) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    $backup = Join-Path $InstallDirectory ('backup\before-0.3.0-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($name in $clientFiles) { $path = Join-Path $InstallDirectory $name; if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $backup $name) } }
    function Protect-Directory([string]$path) {
        # Never follow junctions or symlinks into an unrelated directory.
        $current = $path
        while ($current) {
            if (Test-Path -LiteralPath $current) { if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not supported for protected installation.' } }
            $current = [IO.Path]::GetDirectoryName($current)
        }
        New-Item -ItemType Directory -Path $path -Force | Out-Null
        $acl = New-Object Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true,$false)
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
        }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($owner,'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
        Set-Acl -LiteralPath $path -AclObject $acl
    }
    Protect-Directory (Split-Path $accountRoot -Parent)
    Protect-Directory $accountRoot
    Protect-Directory (Split-Path (Split-Path $policyDir -Parent) -Parent)
    Protect-Directory (Split-Path $policyDir -Parent)
    if (Test-Path -LiteralPath $programRoot) { throw 'Use a fresh protected runtime directory.' }
    Protect-Directory $programRoot; Protect-Directory $client; Protect-Directory $enforcer; Protect-Directory $policyDir
    function Protect-File([string]$path) {
        $acl = New-Object Security.AccessControl.FileSecurity
        $acl.SetAccessRuleProtection($true,$false)
        $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
        foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
            $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','Allow'))
        }
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($owner,'ReadAndExecute','Allow'))
        Set-Acl -LiteralPath $path -AclObject $acl
    }
    foreach ($directory in @($client,$enforcer,$policyDir)) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $directory -Force)) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are not supported for protected files.' }
            if (!$entry.PSIsContainer) { Protect-File $entry.FullName }
        }
    }
    foreach ($name in $clientFiles) {
        Copy-Item -LiteralPath (Join-Path $BundleDirectory ('Client\' + $name)) -Destination (Join-Path $client $name) -Force
        Copy-Item -LiteralPath (Join-Path $BundleDirectory ('Client\' + $name)) -Destination (Join-Path $InstallDirectory $name) -Force
    }
    Copy-Item -LiteralPath $keyFile -Destination (Join-Path $client 'authority-public.txt') -Force
    foreach ($name in $enforcerFiles) { Copy-Item -LiteralPath (Join-Path $BundleDirectory ('Enforcer\' + $name)) -Destination (Join-Path $enforcer $name) -Force }
    foreach ($name in $clientFiles + @('authority-public.txt')) { Protect-File (Join-Path $client $name) }
    foreach ($name in $enforcerFiles) { Protect-File (Join-Path $enforcer $name) }
    $hashes = [ordered]@{}
    foreach ($name in $clientFiles + @('authority-public.txt')) { $hashes[$name] = (Get-FileHash -LiteralPath (Join-Path $client $name)).Hash }
    $profile = [ordered]@{
        OwnerSid=$OwnerSid; ClientDirectory=$client; DataDirectory=$DataDirectory;
        EnforcerExecutable=(Join-Path $enforcer 'WishGuard.Enforcer.exe'); PipeName=('WishGuard-Strong-' + $id);
        TaskName=$strongTask; PublicKey=$publicKey; GamePaths=@($GamePath); ClientHashes=$hashes; Policy=$state; Completed=$false; TestOnly=$false
    }
    [IO.File]::WriteAllText($policyFile, ($profile | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
    Protect-File $policyFile
    function New-TaskDefinition([string]$source) {
        $task = $service.NewTask(0); $task.RegistrationInfo.Source = $source
        $task.Settings.Enabled = $true; $task.Settings.StartWhenAvailable = $true
        $task.Settings.DisallowStartIfOnBatteries = $false; $task.Settings.StopIfGoingOnBatteries = $false
        $task.Settings.ExecutionTimeLimit = 'PT0S'; $task.Settings.MultipleInstances = 2
        $task.Settings.RestartCount = 3; $task.Settings.RestartInterval = 'PT1M'
        return $task
    }
    $watch = New-TaskDefinition 'WishGuard game watcher'
    $watch.Principal.UserId = $OwnerSid; $watch.Principal.LogonType = 3; $watch.Principal.RunLevel = 0
    $trigger = $watch.Triggers.Create(9); $trigger.UserId = $OwnerSid; $trigger.Delay = 'PT2S'
    $action = $watch.Actions.Create(0); $action.Path = Join-Path $client 'WishGuard.exe'
    $action.Arguments = '--watch --data "' + $DataDirectory + '"'; $action.WorkingDirectory = $client
    $watchSddl = 'D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;' + $OwnerSid + ')'
    $registeredWatch = $folder.RegisterTaskDefinition($watchTask,$watch,6,$OwnerSid,$null,3,$watchSddl)
    $strong = New-TaskDefinition 'WishGuard strong protection'
    $strong.RegistrationInfo.Description = '攒愿强制模式：保护不可用时退出已登记的原神；计划结束后移除。'
    $strong.Principal.UserId = 'SYSTEM'; $strong.Principal.LogonType = 5; $strong.Principal.RunLevel = 1
    $null = $strong.Triggers.Create(8)
    $trigger = $strong.Triggers.Create(9); $trigger.UserId = $OwnerSid
    $action = $strong.Actions.Create(0); $action.Path = $profile.EnforcerExecutable
    $action.Arguments = '--profile "' + $policyFile + '"'; $action.WorkingDirectory = $enforcer
    $strongSddl = 'O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;' + $OwnerSid + ')'
    $registeredStrong = $folder.RegisterTaskDefinition($strongTask,$strong,6,'SYSTEM',$null,5,$strongSddl)
    $input = New-TaskDefinition 'WishGuard input compatibility'
    $input.RegistrationInfo.Description = '攒愿窗口兼容组件；仅服务于当前用户已登记的游戏与攒愿窗口。'
    $input.Principal.UserId = $OwnerSid; $input.Principal.LogonType = 3; $input.Principal.RunLevel = 1
    $trigger = $input.Triggers.Create(9); $trigger.UserId = $OwnerSid
    $action = $input.Actions.Create(0); $action.Path = $profile.EnforcerExecutable
    $action.Arguments = '--input-profile "' + $policyFile + '"'; $action.WorkingDirectory = $enforcer
    $registeredInput = $folder.RegisterTaskDefinition($inputTask,$input,6,$OwnerSid,$null,3,$strongSddl)
    $null = $registeredInput.Run($null); $null = $registeredWatch.Run($null); $null = $registeredStrong.Run($null)
    Start-Sleep -Seconds 3
    if ($folder.GetTask($strongTask).State -ne 4 -or $folder.GetTask($watchTask).State -ne 4 -or $folder.GetTask($inputTask).State -ne 4) { throw 'Installed protection did not remain running.' }
    if ((Get-FileHash -LiteralPath $keyFile).Hash -ne $keyHash) { throw 'Public key changed unexpectedly.' }
    $after = Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($field in @('InstallId','Nonce','TargetPulls','UnlockMode','Committed','RecoveryAt','RecoveryRequestedAt','PermanentPass','RecoveryFinished')) {
        if ($after.$field -cne $state.$field) { throw ('Plan changed unexpectedly: ' + $field) }
    }
    $result.Success = $true; $result.PolicyPreserved = $true
    $result.WaitingForGameInstall = !$gameInstalled
    $result.Backup = $backup
} catch {
    $result.Error = $_.Exception.ToString()
    $result.ErrorLocation = $_.InvocationInfo.PositionMessage
    Write-Error $_ -ErrorAction Continue
    if ($changed) {
        try {
            foreach ($name in @($strongTask,$watchTask,$inputTask)) {
                try { $folder.GetTask($name).Stop(0); $folder.DeleteTask($name,0) }
                catch { if (!(Is-MissingTask $_)) { throw } }
            }
            if ($backup -and (Test-Path -LiteralPath $backup)) {
                foreach ($name in $clientFiles) { $file = Join-Path $backup $name; if (Test-Path -LiteralPath $file) { Copy-Item -LiteralPath $file -Destination (Join-Path $InstallDirectory $name) -Force } }
            }
            if ($oldPolicy) { [IO.File]::WriteAllText($policyFile,($oldPolicy | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false)) }
            elseif ($policyFile -eq (Join-Path $policyDir 'profile.json') -and (Test-Path -LiteralPath $policyFile)) { Remove-Item -LiteralPath $policyFile -Force }
            foreach ($name in @($watchTask,$strongTask,$inputTask)) {
                if (!$oldTasks.ContainsKey($name)) { continue }
                $account = if ($name -eq $strongTask) { 'SYSTEM' } else { $OwnerSid }
                $logon = if ($name -eq $strongTask) { 5 } else { 3 }
                $restored = $folder.RegisterTask($name,$oldTasks[$name].Xml,6,$account,$null,$logon,$oldTasks[$name].Sddl)
                $null = $restored.Run($null)
            }
            $result.RolledBack = $true
        } catch { $result.RollbackError = $_.Exception.ToString() }
    }
} finally {
    if ($ReportPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($ReportPath), ($result | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false)) }
}
if (!$result.Success) { exit 1 }
