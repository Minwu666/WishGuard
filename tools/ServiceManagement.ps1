# Standard Windows service configuration for the enrolled WishGuard supervisor.
# The caller verifies ownership and stops the game before invoking these helpers.
function Get-WishGuardService([string]$Name) {
    if ($Name -notmatch '^WishGuard-Protection-[0-9A-F]{20}$') { throw 'Unexpected protection service name.' }
    Get-CimInstance -ClassName Win32_Service -Filter ("Name='" + $Name + "'")
}
function Invoke-WishGuardSc([string[]]$Arguments) {
    $output = & (Join-Path $env:SystemRoot 'System32\sc.exe') @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('Service configuration failed: ' + ($output -join ' ')) }
    $output
}
function Get-WishGuardServiceSnapshot([string]$Name) {
    $item = Get-WishGuardService $Name
    if (!$item) { return $null }
    $security = (Invoke-WishGuardSc @('sdshow', $Name) | Where-Object { $_ -match '^(O:|G:|D:|S:)' }) -join ''
    if (!$security) { throw 'Cannot back up service permissions.' }
    $key = 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\' + $Name
    $settings = Get-ItemProperty -LiteralPath $key
    $values = @{}
    foreach ($field in @('FailureActions','FailureActionsOnNonCrashFailures','DelayedAutoStart','Description')) {
        if ($settings.PSObject.Properties[$field]) { $values[$field] = $settings.$field }
    }
    $startMode = if ($item.StartMode -eq 'Auto') { 'Automatic' } else { $item.StartMode }
    @{ PathName=$item.PathName; StartName=$item.StartName; ServiceType=$item.ServiceType;
       StartMode=$startMode; DisplayName=$item.DisplayName; Running=($item.State -eq 'Running');
       Security=$security; Values=$values }
}
function Stop-WishGuardService([string]$Name) {
    $item = Get-WishGuardService $Name
    if (!$item) { return }
    # Prevent an already-queued recovery from racing an intentional update.
    $reply = Invoke-CimMethod -InputObject $item -MethodName ChangeStartMode -Arguments @{StartMode='Disabled'}
    if ($reply.ReturnValue -ne 0) { throw 'Cannot disable protection for the update.' }
    if ($item.State -ne 'Stopped') {
        $reply = Invoke-CimMethod -InputObject $item -MethodName StopService
        if ($reply.ReturnValue -notin @(0,6)) { throw 'Cannot stop protection for the update.' }
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $item = Get-WishGuardService $Name
        if (!$item -or $item.State -eq 'Stopped') { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Protection did not stop in time.'
}
function Set-WishGuardService([string]$Name, [string]$Command, [string]$OwnerSid) {
    $item = Get-WishGuardService $Name
    if ($item) {
        $reply = Invoke-CimMethod -InputObject $item -MethodName Change -Arguments @{
            PathName=$Command; StartMode='Automatic'; StartName='LocalSystem'; DesktopInteract=$false
        }
    } else {
        $reply = Invoke-CimMethod -ClassName Win32_Service -MethodName Create -Arguments @{
            Name=$Name; DisplayName=('攒愿保护 · ' + $Name.Substring(21)); PathName=$Command;
            ServiceType=[byte]16; ErrorControl=[byte]1; StartMode='Automatic';
            DesktopInteract=$false; StartName='LocalSystem'
        }
    }
    if ($reply.ReturnValue -ne 0) { throw ('Cannot register protection service: ' + $reply.ReturnValue) }
    $null = Invoke-WishGuardSc @('description',$Name,'WishGuard enrolled-game protection; removed when the plan is completed.')
    $null = Invoke-WishGuardSc @('failure',$Name,'reset=','86400','actions=','restart/2000/restart/2000/restart/2000')
    $null = Invoke-WishGuardSc @('failureflag',$Name,'1')
    $sddl = 'O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;CCLCSWLORC;;;' + $OwnerSid + ')'
    $null = Invoke-WishGuardSc @('sdset',$Name,$sddl)
}
function Start-WishGuardService([string]$Name) {
    $item = Get-WishGuardService $Name
    $reply = Invoke-CimMethod -InputObject $item -MethodName StartService
    if ($reply.ReturnValue -notin @(0,10)) { throw ('Cannot start protection service: ' + $reply.ReturnValue) }
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        $item = Get-WishGuardService $Name
        if ($item.State -eq 'Running') { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'Protection service did not start in time.'
}
function Restore-WishGuardService([string]$Name, $Snapshot) {
    $item = Get-WishGuardService $Name
    if ($item) { Stop-WishGuardService $Name }
    if (!$Snapshot) {
        if ($item) {
            $reply = Invoke-CimMethod -InputObject (Get-WishGuardService $Name) -MethodName Delete
            if ($reply.ReturnValue -ne 0) { throw 'Cannot remove the incomplete protection service.' }
        }
        return
    }
    $item = Get-WishGuardService $Name
    if (!$item) { throw 'The previous protection service is missing.' }
    $reply = Invoke-CimMethod -InputObject $item -MethodName Change -Arguments @{
        PathName=$Snapshot.PathName; StartMode=$Snapshot.StartMode; StartName=$Snapshot.StartName;
        DisplayName=$Snapshot.DisplayName; DesktopInteract=$false
    }
    if ($reply.ReturnValue -ne 0) { throw 'Cannot restore the previous protection service.' }
    $null = Invoke-WishGuardSc @('sdset',$Name,$Snapshot.Security)
    $key = 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\' + $Name
    foreach ($field in @('FailureActions','FailureActionsOnNonCrashFailures','DelayedAutoStart','Description')) {
        if ($Snapshot.Values.ContainsKey($field)) {
            $kind = if ($field -eq 'FailureActions') { 'Binary' } elseif ($field -eq 'Description') { 'String' } else { 'DWord' }
            $null = New-ItemProperty -LiteralPath $key -Name $field -Value $Snapshot.Values[$field] -PropertyType $kind -Force
        } else { Remove-ItemProperty -LiteralPath $key -Name $field -ErrorAction SilentlyContinue }
    }
    if ($Snapshot.Running) { Start-WishGuardService $Name }
}
