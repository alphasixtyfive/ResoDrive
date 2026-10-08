param([Parameter(Mandatory)][string]$SetupPath, [Parameter(Mandatory)][string]$AppPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Migration recovery acceptance requires a disposable GitHub-hosted Windows runner.'
}
. (Join-Path $PSScriptRoot 'installer-ui.ps1')
$setup = (Resolve-Path -LiteralPath $SetupPath).Path
$candidate = (Resolve-Path -LiteralPath $AppPath).Path
$local = [Environment]::GetFolderPath('LocalApplicationData')
$oldData = Join-Path $local 'rdrive'
$newData = Join-Path $local 'ResoDrive'
$install = Join-Path $env:ProgramFiles 'ResoDrive'
$installedApp = Join-Path $install 'resodrive.exe'
$installedAppPresent = Test-Path -LiteralPath $installedApp
$legacyDataPresent = Test-Path -LiteralPath $oldData
$migratedDataPresent = Test-Path -LiteralPath $newData
if ($installedAppPresent -or $legacyDataPresent -or -not $migratedDataPresent) {
    throw "Recovery requires the preserved, uninstalled default-root fixture. InstalledAppPresent=$installedAppPresent; LegacyDataPresent=$legacyDataPresent; MigratedDataPresent=$migratedDataPresent."
}
$evidence = Join-Path $env:RUNNER_TEMP 'resodrive-migration-recovery-smoke'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
$previousRoot = $env:RDRIVE_DATA_DIR
$previousBridge = $env:RESODRIVE_LEGACY_HANDOFF
$previousRetry = $env:RESODRIVE_MIGRATION_RETRY_ON_NEXT_START
$parent = $updated = $null
try {
    $hashes = @{}
    foreach ($name in @('settings.json', 'cache\preserve.txt', 'managed-sync\preserve.txt', 'config-pass.dpapi')) {
        $hashes[$name] = (Get-FileHash (Join-Path $newData $name)).Hash
    }
    # Only relocate the disposable fixture, after checking both exact sibling paths.
    if ([IO.Path]::GetFullPath($newData) -ine (Join-Path $local 'ResoDrive') -or
        [IO.Path]::GetFullPath($oldData) -ine (Join-Path $local 'rdrive')) { throw 'Unexpected fixture paths.' }
    [IO.Directory]::Move($newData, $oldData)
    $env:RDRIVE_DATA_DIR = $oldData
    # Keep the exact withdrawn binary independent of GitHub release visibility.
    # This pinned tagged-build artifact must never be replaced with a rebuild.
    $baselineDirectory = Join-Path $evidence 'baseline'
    gh run download 37784471088 --name 'resodrive-v0.3.35' --dir $baselineDirectory
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not download the exact .35 recovery baseline from its tagged Actions build. Restore the archived baseline; do not substitute a rebuilt binary.'
    }
    $baseline = Join-Path $baselineDirectory 'resodrive-win-x64-0.3.35.msi'
    $expected = (Get-Content "$baseline.sha256" -Raw).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0].Trim()
    $pinnedMsiHash = 'CE77B0FC9240B3D1AE45567A7B270F7888234255245C3662A4E25C8F2EB1AE66'
    if ($expected -ine $pinnedMsiHash -or (Get-FileHash $baseline).Hash -ine $pinnedMsiHash) { throw 'Recovery baseline hash mismatch.' }
    $baselineInstall = Start-Process msiexec.exe -ArgumentList @('/i', ('"' + $baseline + '"'), '/qn', '/norestart',
        ('RDRIVE_DATA_ROOT="' + $oldData + '\."'), '/l*v', ('"' + $evidence + '\baseline.log"')) -Wait -PassThru
    try { if ($baselineInstall.ExitCode -ne 0) { throw 'Recovery baseline installation failed.' } } finally { $baselineInstall.Dispose() }
    $pinnedExeHash = '4A03ECAE7DC1DFAA8BD30939BD8CB61E52EC4FC26BD521A1D8DC9A65CC3D5C4B'
    if ((Get-FileHash $installedApp).Hash -ine $pinnedExeHash) { throw 'The installed recovery baseline differs from the withdrawn .35 executable.' }
    # Reuse the application's authenticated status client in a disposable probe.
    # No additional entry point or executable is included in the product.
    $probeDirectory = Join-Path $evidence 'host-readiness-probe'
    New-Item -ItemType Directory -Path $probeDirectory -Force | Out-Null
    $windowsProject = [Security.SecurityElement]::Escape((Join-Path (Split-Path $PSScriptRoot) 'src\ResoDrive.Windows\ResoDrive.Windows.csproj'))
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.17763.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup>
  <ItemGroup><ProjectReference Include="$windowsProject" /></ItemGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $probeDirectory 'HostReadiness.csproj')
    @'
using System.Diagnostics;
using System.Text.Json;
using ResoDrive.Windows;

Environment.SetEnvironmentVariable("RDRIVE_DATA_DIR", args[1]);
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    while (!deadline.IsCancellationRequested)
    {
        var response = await HostClient.SendToInstallationAsync(new HostRequest("status"), args[0],
            TimeSpan.FromSeconds(2), deadline.Token);
        if (response.Succeeded && response.HostProcessId is int id)
        {
            using var host = Process.GetProcessById(id);
            var expected = Path.Combine(args[0], "resodrive.exe");
            if (!host.HasExited && string.Equals(host.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.WriteAllText(args[2], JsonSerializer.Serialize(new {
                    HostProcessId = id, response.HostBaseDirectory, DataRoot = new ApplicationPaths().Root,
                    AuthenticatedStatus = true
                }));
                return 0;
            }
        }
        await Task.Delay(200, deadline.Token);
    }
}
catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
Console.Error.WriteLine("The exact .35 host did not acknowledge authenticated status against the old data root.");
return 1;
'@ | Set-Content -LiteralPath (Join-Path $probeDirectory 'Program.cs')
    dotnet build (Join-Path $probeDirectory 'HostReadiness.csproj') -c Release --verbosity quiet *> (Join-Path $evidence 'host-probe-build.log')
    if ($LASTEXITCODE -ne 0) { throw 'The authenticated host readiness probe could not compile. See host-probe-build.log.' }
    # Reproduce .35's post-handoff state: current program path, old data root,
    # and a running app/host. These are the baseline's actual failure-fallback
    # flags: defer the move and suppress its automatic second helper attempt.
    $env:RESODRIVE_LEGACY_HANDOFF = '1'
    $env:RESODRIVE_MIGRATION_RETRY_ON_NEXT_START = '1'
    $completionPath = Join-Path $local 'ResoDriveMigration\completion.json'
    [IO.File]::WriteAllText($completionPath, '{"Succeeded":false,"Message":"Prior migration did not finish."}')
    $parent = Start-Process $installedApp -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 5
    if ($parent.HasExited -or -not (Test-Path $oldData) -or (Test-Path $newData)) { throw 'The partial-migration baseline was not reproduced.' }
    $hostEvidencePath = Join-Path $evidence 'baseline-host-readiness.json'
    $probe = Start-Process (Join-Path $probeDirectory 'bin\Release\net10.0-windows10.0.17763.0\HostReadiness.exe') -ArgumentList @(
        ('"' + $install + '"'), ('"' + $oldData + '"'), ('"' + $hostEvidencePath + '"')) -WindowStyle Hidden -PassThru
    try {
        if (-not $probe.WaitForExit(45000) -or $probe.ExitCode -ne 0) { throw 'The .35 host was not ready against its old data root.' }
    } finally { $probe.Dispose() }
    $hostReadiness = Get-Content -LiteralPath $hostEvidencePath -Raw | ConvertFrom-Json
    $baselineHost = Get-CimInstance Win32_Process -Filter "ProcessId=$($hostReadiness.HostProcessId)"
    $hostOwner = Invoke-CimMethod -InputObject $baselineHost -MethodName GetOwnerSid
    $runnerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($baselineHost.ExecutablePath -ine $installedApp -or $baselineHost.CommandLine -notmatch ' --host(?:\s|$)' -or
        $hostOwner.ReturnValue -ne 0 -or $hostOwner.Sid -ine $runnerSid -or $hostReadiness.DataRoot -ine $oldData) {
        throw 'The authenticated recovery host does not match the expected .35 process, Windows account and old data root.'
    }
    $env:RESODRIVE_LEGACY_HANDOFF = $null
    $env:RESODRIVE_MIGRATION_RETRY_ON_NEXT_START = $null
    $update = Start-Process $setup -ArgumentList @('/passive', '/norestart', '/log', ('"' + $evidence + '\setup.log"'),
        ('ResoDriveDataRoot="' + $oldData + '\."')) -PassThru
    try {
        Wait-VisibleSetup $update '' (Join-Path $evidence 'setup-ui.json') -Passive
        if ($update.ExitCode -notin @(0, 3010)) { throw "Recovery Setup failed: $($update.ExitCode)" }
    } finally { $update.Dispose() }
    if (-not $parent.WaitForExit(15000)) { throw 'The .35 app was not safely stopped.' }
    if ((Get-FileHash $installedApp).Hash -ne (Get-FileHash $candidate).Hash) { throw 'Recovery executable mismatch.' }
    $updated = Start-Process $installedApp -ArgumentList '--show' -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $completed = $false
    while (-not $updated.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        try { $completed = -not (Test-Path $oldData) -and (Get-Content $completionPath -Raw | ConvertFrom-Json).Succeeded }
        catch { $completed = $false } # Receipt publication can race this read.
        if ($completed) { break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $completed -or $updated.HasExited) { throw 'The corrected app did not finish the move and replace the previous failure receipt.' }
    $show = Start-Process $installedApp -ArgumentList '--show' -WindowStyle Hidden -PassThru
    try {
        if (-not $show.WaitForExit(120000) -or $show.ExitCode -ne 0 -or $updated.HasExited) {
            throw 'The recovered app did not acknowledge its actual ready window.'
        }
    } finally { $show.Dispose() }
    foreach ($name in $hashes.Keys) {
        if ((Get-FileHash (Join-Path $newData $name)).Hash -ine $hashes[$name]) { throw "Recovery changed preserved file: $name" }
    }
    [ordered]@{ PriorVersion = '0.3.35'; InstalledVersion = (Get-Item $installedApp).VersionInfo.ProductVersion;
        BaselineRun = 37784471088; BaselineMsiSha256 = $pinnedMsiHash; BaselineExeSha256 = $pinnedExeHash;
        BaselineHostAuthenticated = $true; BaselineHostProcessId = $hostReadiness.HostProcessId;
        PendingDefaultDataMoved = $true; ReadyWindowAcknowledged = $true; PreviousFailureReplaced = $true;
        SettingsCacheAndCredentialBytesPreserved = $true;
        SetupSha256 = (Get-FileHash $setup).Hash; InstalledExeSha256 = (Get-FileHash $installedApp).Hash } |
        ConvertTo-Json | Set-Content (Join-Path $evidence 'result.json')
    $remove = Start-Process msiexec.exe -ArgumentList @('/x', ('"' + (Join-Path (Split-Path $setup) ((Split-Path $setup -Leaf) -replace '-setup.exe$', '.msi')) + '"'),
        '/qn', '/norestart', ('RDRIVE_DATA_ROOT="' + $newData + '\."')) -Wait -PassThru
    try { if ($remove.ExitCode -ne 0) { throw 'Recovery fixture removal failed.' } } finally { $remove.Dispose() }
    Write-Output 'Partial-migration recovery passed against the actual .35 program with preserved default-root data.'
} finally {
    $env:RDRIVE_DATA_DIR = $previousRoot
    $env:RESODRIVE_LEGACY_HANDOFF = $previousBridge
    $env:RESODRIVE_MIGRATION_RETRY_ON_NEXT_START = $previousRetry
    if ($parent) { $parent.Dispose() }
    if ($updated) { $updated.Dispose() }
}
