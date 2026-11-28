# Native UI acceptance helper for the disposable hosted installer fixture.
# Never use this helper to operate an installation on a developer's desktop.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Visible installer checks require a disposable GitHub-hosted Windows runner.'
}

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public sealed class ResoDriveInstallerWindow
{
    public IntPtr Handle;
    public uint ProcessId;
    public string Caption;
    public string ClassName;
}
public static class ResoDriveInstallerUi
{
    private delegate bool EnumCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private static ResoDriveInstallerWindow Describe(IntPtr window)
    {
        var caption = new StringBuilder(1024);
        var className = new StringBuilder(256);
        GetWindowText(window, caption, caption.Capacity);
        GetClassName(window, className, className.Capacity);
        GetWindowThreadProcessId(window, out uint processId);
        return new ResoDriveInstallerWindow { Handle = window, ProcessId = processId, Caption = caption.ToString(), ClassName = className.ToString() };
    }
    public static ResoDriveInstallerWindow[] VisibleWindows()
    {
        var result = new List<ResoDriveInstallerWindow>();
        EnumWindows((window, parameter) => { if (IsWindowVisible(window)) result.Add(Describe(window)); return true; }, IntPtr.Zero);
        return result.ToArray();
    }
    public static ResoDriveInstallerWindow[] Children(IntPtr parent)
    {
        var result = new List<ResoDriveInstallerWindow>();
        EnumChildWindows(parent, (window, parameter) => {
            if (IsWindowVisible(window)) result.Add(Describe(window));
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
    public static bool ClickButton(IntPtr parent, string caption)
    {
        foreach (var child in Children(parent))
        {
            if (child.ClassName == "Button" && child.Caption.Replace("&", "") == caption && IsWindowEnabled(child.Handle))
                return PostMessage(child.Handle, 0x00F5, IntPtr.Zero, IntPtr.Zero); // BM_CLICK
        }
        return false;
    }
}
'@

function Wait-VisibleSetup($Process, [string]$Action, [string]$EvidencePath, [switch]$Passive) {
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    $observed = [Collections.Generic.Dictionary[string, object]]::new()
    $setupSeen = $progressSeen = $actionClicked = $false
    try {
        while (-not $Process.WaitForExit(50)) {
            if ([DateTime]::UtcNow -gt $deadline) { throw 'Visible Setup timed out.' }
            foreach ($window in [ResoDriveInstallerUi]::VisibleWindows()) {
                $owner = Get-Process -Id $window.ProcessId -ErrorAction SilentlyContinue
                if ($null -eq $owner) { continue }
                $isSetup = $window.Caption -ceq 'ResoDrive Setup'
                $isMsi = $owner.ProcessName -ieq 'msiexec' -or
                    $window.ClassName -ieq 'MsiDialogCloseClass' -or $window.Caption -ieq 'Windows Installer'
                if (-not $isSetup -and -not $isMsi) { continue }
                $key = "$($window.ProcessId)/$($window.Handle.ToInt64())"
                if (-not $observed.ContainsKey($key)) {
                    $observed.Add($key, [ordered]@{
                        Process = $owner.ProcessName; ProcessId = $window.ProcessId
                        Caption = $window.Caption; ClassName = $window.ClassName; WindowsInstaller = $isMsi
                    })
                }
                if (-not $isSetup) { continue }
                $setupSeen = $true
                $children = @([ResoDriveInstallerUi]::Children($window.Handle))
                if (@($children | Where-Object { $_.ClassName -ieq 'msctls_progress32' }).Count -gt 0) {
                    $progressSeen = $true
                }
                if (-not $Passive -and -not $actionClicked) {
                    $actionClicked = [ResoDriveInstallerUi]::ClickButton($window.Handle, $Action)
                } elseif (-not $Passive -and @($children | Where-Object { $_.Caption -ceq 'Setup complete' -or $_.Caption -ceq 'Setup could not finish' -or $_.Caption -ceq 'ResoDrive is ready' }).Count -gt 0) {
                    [ResoDriveInstallerUi]::ClickButton($window.Handle, 'Close') | Out-Null
                }
            }
        }
        if (-not $setupSeen -or (-not $Passive -and -not $actionClicked) -or -not $progressSeen) {
            throw 'The Setup window and its actual progress page were not observed.'
        }
        $msiWindows = @($observed.Values | Where-Object WindowsInstaller -EQ $true)
        if ($msiWindows.Count -ne 0) {
            throw "Windows Installer displayed a separate window: $($msiWindows.Caption -join ', ')."
        }
    } finally {
        [ordered]@{
            SetupSeen = $setupSeen; Passive = [bool]$Passive; ActionClicked = $actionClicked; ProgressSeen = $progressSeen
            ExitCode = if ($Process.HasExited) { $Process.ExitCode } else { $null }
            Windows = @($observed.Values)
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
    }
}
