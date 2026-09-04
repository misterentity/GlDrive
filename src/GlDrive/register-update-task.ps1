#!/usr/bin/env pwsh
<#
.SYNOPSIS
Registers the fixed-action SYSTEM tasks that apply GlDrive updates and clean rollback files.

.DESCRIPTION
The auto-updater waits for the app to be idle before installing. On a busy racing box that
reliably lands in the middle of the night, where it raised a UAC prompt with nobody at the
keyboard; the Windows secure desktop timed the prompt out after 120s and ShellExecute returned
ERROR_CANCELLED, which the app recorded as a user decision and suppressed for 24h — re-offering
at the same hour the next night. Every daytime auto-install succeeded; both ~03:35 attempts
"declined" after an invariant 131s/132s.

The installer task removes interactive elevation from that path. A separate cleanup task removes
SYSTEM-owned .old rollback files only after the updated application has started, so the backups
remain available throughout the installer's rollback window. Both are registered from an already
elevated installer/updater context.

Design notes:
  * NO TRIGGER. The task can only be started on demand (`schtasks /run`). Register-ScheduledTask
    is used rather than `schtasks /create` precisely because schtasks REQUIRES a schedule and its
    /sd date format is locale-dependent — `/sd 01/01/2099` is rejected outright on a machine
    expecting yyyy/mm/dd.
  * FIXED ACTIONS. The executable and each task's single argument are baked in here, at elevated
    install time. A caller can ask for a task to run; it can never change what runs, what arguments
    it gets, or which directory is touched.
  * The install destination is NOT passed. UpdateChecker.ApplyUpdate requires the destination to
    equal the elevated process's own directory, so it is pinned by where this action points.

Failure is non-fatal: update installation falls back to interactive elevation and rollback cleanup
is retried at the next startup.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallDir,
    [string]$TaskName = 'GlDrive Update Installer',
    [string]$CleanupTaskName = 'GlDrive Update Cleanup'
)

$ErrorActionPreference = 'Stop'

try {
    $exe = Join-Path $InstallDir 'GlDrive.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Warning "GlDrive.exe not found at $exe - skipping update task registration."
        exit 0
    }

    # ServiceAccount logon for SYSTEM; Highest so it can write into Program Files.
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' `
        -LogonType ServiceAccount -RunLevel Highest

    # The updater waits for the app to exit, copies ~1,148 files and can roll back, so give it
    # room - but bound it, so a wedged install cannot leave a SYSTEM process running forever.
    $settings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
        -StartWhenAvailable `
        -ExecutionTimeLimit (New-TimeSpan -Minutes 30)

    # Let the non-elevated app START each task. Read+execute only for Authenticated Users;
    # Administrators and SYSTEM keep full control, so a standard user still cannot redefine
    # what either fixed action runs.
    #   BA = Builtin Administrators, SY = Local System, AU = Authenticated Users
    #   GA = Generic All, GRGX = Generic Read + Generic Execute
    $sddl = 'D:(A;;GA;;;BA)(A;;GA;;;SY)(A;;GRGX;;;AU)'
    $svc = New-Object -ComObject Schedule.Service
    $svc.Connect()
    $rootFolder = $svc.GetFolder([string][char]92)

    function Register-FixedTask {
        param(
            [string]$Name,
            [string]$Argument,
            [string]$Description
        )

        $action = New-ScheduledTaskAction -Execute $exe -Argument $Argument
        Register-ScheduledTask -TaskName $Name -Action $action -Principal $principal `
            -Settings $settings -Description $Description -Force | Out-Null
        $rootFolder.GetTask($Name).SetSecurityDescriptor($sddl, 0)
        Write-Output "Registered scheduled task '$Name' -> $exe $Argument"
    }

    Register-FixedTask -Name $TaskName -Argument '--apply-update-task' `
        -Description 'Applies verified GlDrive updates without an interactive elevation prompt.'
    Register-FixedTask -Name $CleanupTaskName -Argument '--cleanup-old-updates' `
        -Description 'Removes GlDrive updater rollback files after the updated application starts.'

    exit 0
}
catch {
    # Never fail the install over this - the app degrades to the interactive prompt.
    Write-Warning "Could not register the GlDrive update task: $($_.Exception.Message)"
    Write-Warning "Updates will still install, but will ask for elevation."
    exit 0
}
