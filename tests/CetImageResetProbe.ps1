param([string]$ApplicationPath = (Join-Path $env:WINDIR 'System32/cmd.exe'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Disposable GitHub-hosted runner required.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Elevated disposable runner required.' }
Import-Module ProcessMitigations -ErrorAction Stop
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CetImageResetProbe {
    [StructLayout(LayoutKind.Sequential)] public struct Shadow { public ulong Stack, Ip, Block; }
    [StructLayout(LayoutKind.Sequential)] public struct Cfg { public ulong Enable, Strict; }
    [DllImport("ntdll.dll", EntryPoint="RtlSetImageMitigationPolicy", CharSet=CharSet.Unicode)]
    static extern int Set(string path, int family, uint flags, IntPtr buffer, uint size);
    [DllImport("ntdll.dll", EntryPoint="RtlQueryImageMitigationPolicy", CharSet=CharSet.Unicode)]
    static extern int Query(string path, int family, uint flags, out Cfg value, uint size);
    public static int TrySet(string path, uint flags, bool nullBuffer, ulong first) {
        if (nullBuffer) return Set(path,15,flags,IntPtr.Zero,0);
        IntPtr memory=Marshal.AllocHGlobal(24);
        try {
            Marshal.StructureToPtr(new Shadow { Stack=first },memory,false);
            return Set(path,15,flags,memory,24);
        } finally { Marshal.FreeHGlobal(memory); }
    }
    public static ulong ReadCfg(string path) {
        Cfg value; int status=Query(path,7,0,out value,16);
        if (unchecked((uint)status)==0xC0000034U) return 0;
        if (status!=0) throw new InvalidOperationException("query CFG 0x"+unchecked((uint)status).ToString("X8"));
        return value.Enable;
    }
    public static string Hex(int status) { return "0x"+unchecked((uint)status).ToString("X8"); }
}
'@
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$root = 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\'
function Read-Filters([string]$Name) {
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $registry.OpenSubKey($root+$Name)
        if ($null -eq $key) { return @() }
        try {
            foreach($subName in $key.GetSubKeyNames()) {
                $sub = $key.OpenSubKey($subName)
                try {
                    [pscustomobject]@{ Name=$subName; FullPath=$sub.GetValue('FilterFullPath',$null); ValueNames=@($sub.GetValueNames()) }
                } finally { $sub.Dispose() }
            }
        } finally { $key.Dispose() }
    } finally { $registry.Dispose() }
}
$receipts = @()
foreach($flag in 1,2) {
 foreach($nullBuffer in $false,$true) {
  foreach($nonEmpty in $false,$true) {
   $id=[guid]::NewGuid().ToString('N')
   $name='resodrive-cet-reset-probe-'+$id+'.exe'
   $directory=[IO.Path]::GetFullPath((Join-Path $env:RUNNER_TEMP ('CetImageResetProbe-'+$id)))
   $runnerRoot=[IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')+'\'
   if (!$directory.StartsWith($runnerRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture escaped runner.' }
   New-Item -ItemType Directory -Path $directory | Out-Null
   $path=Join-Path $directory $name
   Copy-Item -LiteralPath $application -Destination $path
   $registry=[Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,[Microsoft.Win32.RegistryView]::Registry64)
   $owned=$false
   try {
    $existing=$registry.OpenSubKey($root+$name)
    if ($null -ne $existing) { $existing.Dispose(); throw 'Fixture policy collision.' }
    $owned=$true
    if ($nonEmpty) { Set-ProcessMitigation -Name $path -Enable CFG -ErrorAction Stop -WarningAction Stop }
    $created=[CetImageResetProbe]::TrySet($path,0,$false,2)
    if ($created -ne 0) { throw ('create fixture CET '+[CetImageResetProbe]::Hex($created)) }
    $filtersBefore=@(Read-Filters $name)
    if (@($filtersBefore | Where-Object FullPath -IEQ $path).Count -ne 1) { throw 'Exactly one owned filter was not created.' }
    $cfgBefore=[CetImageResetProbe]::ReadCfg($path)
    $status=[CetImageResetProbe]::TrySet($path,[uint32]$flag,$nullBuffer,0)
    $filtersAfter=@(Read-Filters $name)
    $cfgAfter=[CetImageResetProbe]::ReadCfg($path)
    if ($nonEmpty -and ($cfgBefore -ne $cfgAfter -or @($filtersAfter | Where-Object FullPath -IEQ $path).Count -ne 1)) {
        throw 'Native reset/remove erased a nonempty fixture filter or changed its CFG.'
    }
    # A later basename setting proves whether an empty retained filter still
    # changes inheritance. These policies exist only under this unique name.
    Set-ProcessMitigation -Name $name -Enable CFG -ErrorAction Stop -WarningAction Stop
    $futureBase=[CetImageResetProbe]::ReadCfg($name)
    $futurePath=[CetImageResetProbe]::ReadCfg($path)
    $receipt=[pscustomobject]@{
        Flags=$flag; NullBuffer=$nullBuffer; NonemptyCfg=$nonEmpty
        Status=[CetImageResetProbe]::Hex($status)
        BeforeFilters=$filtersBefore; AfterFilters=$filtersAfter
        CfgBefore=('{0:X16}' -f $cfgBefore); CfgAfter=('{0:X16}' -f $cfgAfter)
        FutureBasenameCfg=('{0:X16}' -f $futureBase); FutureExactCfg=('{0:X16}' -f $futurePath)
        FutureBasenameInherited=($futureBase -eq $futurePath)
    }
    $receipts += $receipt
    Write-Host ($receipt | ConvertTo-Json -Depth 5 -Compress)
   } finally {
    if ($owned) {
        # Documented RegistryKey API removes only the newly created GUID name.
        if ($name -notmatch '^resodrive-cet-reset-probe-[0-9a-f]{32}\.exe$') { throw 'Unexpected cleanup name.' }
        $registry.DeleteSubKeyTree($root+$name,$false)
    }
    $registry.Dispose()
    Remove-Item -LiteralPath $path
    Remove-Item -LiteralPath $directory
   }
  }
 }
}
$receipts | ConvertTo-Json -Depth 5
