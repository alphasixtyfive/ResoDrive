# Hosted-only, read-only metadata; no debugger command, event message or dump contents.
function Write-WerFailureEvidence {
    [CmdletBinding()]
    param([int]$ChildId, [long]$CreationFileTime, [int]$ExitCode, [string]$DumpFolder,
        [string]$IdentityPath, [string]$MetadataRoot, [string]$FixtureKind)
    if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
        throw 'WER failure snapshots are hosted-only.'
    }
    function Numeric-RegistrySnapshot($hive, $view, [string]$path, [string[]]$numericNames, [string[]]$presenceNames = @()) {
        $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, $view)
        try {
            $key = $base.OpenSubKey($path)
            $result = [ordered]@{ hive = $hive.ToString(); view = $view.ToString(); path = $path; keyPresent = $null -ne $key }
            if ($null -ne $key) {
                try {
                    foreach ($name in $numericNames) {
                        $value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                        if ($null -ne $value) {
                            $number = [long]0
                            $result[$name] = if ([long]::TryParse([string]$value, [ref]$number)) { $number } else { 'present-nonnumeric' }
                        }
                    }
                    foreach ($name in $presenceNames) { $result[$name + 'Present'] = $null -ne $key.GetValue($name) }
                } finally { $key.Dispose() }
            }
            return [pscustomobject]$result
        } finally { $base.Dispose() }
    }
    $registry = [Collections.Generic.List[object]]::new()
    foreach ($view in [Microsoft.Win32.RegistryView]::Registry64, [Microsoft.Win32.RegistryView]::Registry32) {
        $registry.Add((Numeric-RegistrySnapshot LocalMachine $view 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug' @('Auto') @('Debugger')))
        $registry.Add((Numeric-RegistrySnapshot LocalMachine $view 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\AeDebug\AutoExclusionList' @('resodrive.exe')))
        $registry.Add((Numeric-RegistrySnapshot LocalMachine $view 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\resodrive.exe' @('GlobalFlag') @('Debugger')))
        $registry.Add((Numeric-RegistrySnapshot LocalMachine $view 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\SilentProcessExit\resodrive.exe' @('ReportingMode', 'DumpType', 'IgnoreSelfExits') @('MonitorProcess')))
        foreach ($hive in [Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryHive]::CurrentUser) {
            $registry.Add((Numeric-RegistrySnapshot $hive $view 'SOFTWARE\Microsoft\Windows\Windows Error Reporting' @('Disabled', 'DontShowUI', 'LoggingDisabled', 'ForceQueue', 'DisableQueue', 'DisableArchive')))
            $registry.Add((Numeric-RegistrySnapshot $hive $view 'SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting' @('Disabled', 'DontShowUI')))
        }
        $registry.Add((Numeric-RegistrySnapshot LocalMachine $view 'SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\resodrive.exe' @('DumpType', 'DumpCount', 'CustomDumpFlags')))
    }
    $service = Get-Service -Name WerSvc -ErrorAction SilentlyContinue
    $serviceEvidence = if ($null -eq $service) { @{ present = $false } } else { @{ present = $true; status = $service.Status.ToString(); startType = $service.StartType.ToString() } }
    $runtimeEvidence = $null
    $runtimePath = $IdentityPath + '.runtime.json'
    if (Test-Path -LiteralPath $runtimePath) {
        $runtime = Get-Content -LiteralPath $runtimePath -Raw | ConvertFrom-Json
        $runtimeEvidence = @{ errorMode = [uint32]$runtime.errorMode; werFlags = [uint32]$runtime.werFlags; werSetResult = [int]$runtime.werSetResult; debuggerPresent = [bool]$runtime.debuggerPresent }
    }
    $events = [Collections.Generic.List[object]]::new()
    $eventError = $null
    $start = [DateTime]::FromFileTimeUtc($CreationFileTime)
    try {
        foreach ($event in @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = @(1000,1001,1023,1026); StartTime = $start; EndTime = [DateTime]::UtcNow } -MaxEvents 128 -ErrorAction Stop)) {
            if ($event.ProviderName -notin @('Application Error', 'Windows Error Reporting', '.NET Runtime')) { continue }
            [xml]$xml = $event.ToXml()
            $data = @{}
            foreach ($item in @($xml.Event.EventData.Data)) {
                if ($null -ne $item -and $item.HasAttribute('Name')) { $data[$item.GetAttribute('Name')] = $item.InnerText }
            }
            $exactError = $event.Id -eq 1000 -and $event.ProviderName -eq 'Application Error' -and
                $data.ContainsKey('ProcessId') -and $data.ContainsKey('ProcessCreationTime') -and
                [Convert]::ToInt64($data.ProcessId, 16) -eq $ChildId -and [Convert]::ToInt64($data.ProcessCreationTime, 16) -eq $CreationFileTime
            $systemPid = [long]0
            $null = [long]::TryParse([string]$xml.Event.System.Execution.ProcessID, [ref]$systemPid)
            # Caller holds the original child handle while collecting this snapshot.
            if (-not $exactError -and $systemPid -ne $ChildId) { continue }
            $entry = [ordered]@{ id = $event.Id; provider = $event.ProviderName; timeUtc = $event.TimeCreated.ToUniversalTime().ToString('O'); recordId = $event.RecordId; exactProcessCreation = $exactError }
            foreach ($name in @('ExceptionCode', 'FaultingOffset', 'ModuleVersion', 'ModuleName', 'ReportId')) {
                if (-not $data.ContainsKey($name)) { continue }
                $value = [string]$data[$name]
                $valid = switch ($name) {
                    { $_ -in 'ExceptionCode','FaultingOffset' } { $value -match '^(0x)?[0-9A-Fa-f]{1,16}$'; break }
                    'ModuleVersion' { $value -match '^\d{1,5}(\.\d{1,5}){1,3}$'; break }
                    'ModuleName' { $value -match '^[A-Za-z0-9_.-]{1,100}\.(dll|exe)$'; break }
                    'ReportId' { $value -match '^\{?[0-9a-fA-F]{8}(-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\}?$'; break }
                }
                if ($valid) { $entry[$name] = $value }
            }
            $events.Add([pscustomobject]$entry)
            if ($events.Count -ge 8) { break }
        }
    } catch { $eventError = $_.Exception.GetType().FullName }
    $folder = Get-Item -LiteralPath $DumpFolder -ErrorAction SilentlyContinue
    $files = @(Get-ChildItem -LiteralPath $DumpFolder -Filter 'resodrive.exe.*.dmp' -File -ErrorAction SilentlyContinue | Select-Object -First 8 | ForEach-Object {
        [pscustomobject]@{ name = $_.Name; length = $_.Length; creationUtc = $_.CreationTimeUtc.ToString('O'); lastWriteUtc = $_.LastWriteTimeUtc.ToString('O') }
    })
    $snapshot = [ordered]@{
        fixture = $FixtureKind; childId = $ChildId; creationFileTime = $CreationFileTime; exitCode = $ExitCode
        dumpFolder = $DumpFolder; dumpFolderPresent = $null -ne $folder; dumpFolderAttributes = if ($null -eq $folder) { $null } else { $folder.Attributes.ToString() }
        metadataFolder = $MetadataRoot + '-diagnostics'; runtime = $runtimeEvidence; werService = $serviceEvidence
        registry = $registry; events = $events; eventQueryErrorType = $eventError; dumpCandidates = $files
        nativeSummaryFiles = @(Get-ChildItem -LiteralPath ($MetadataRoot + '-diagnostics') -Filter 'incident-*.txt' -File -ErrorAction SilentlyContinue | Select-Object -First 2 | ForEach-Object { $_.FullName })
    }
    $destination = Join-Path $env:RUNNER_TEMP 'resodrive-installer-smoke'
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $path = Join-Path $destination ("wer-failure-{0}-{1}.json" -f $FixtureKind, $ChildId)
    $snapshot | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    Write-Host "Sanitized WER diagnostic evidence saved: $path"
}
