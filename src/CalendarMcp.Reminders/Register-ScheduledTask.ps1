<#
.SYNOPSIS
    Registers the Calendar MCP reminders host as a per-user scheduled task.

.DESCRIPTION
    The reminders host listens for bot commands as well as timing alerts, so it
    runs continuously instead of once every few minutes. The task starts at logon,
    restarts on failure, and never runs while the session is locked.

    The host resolves its configuration through CALENDAR_MCP_CONFIG. Pass
    -ConfigDirectory to have this script store that value for the current user, or
    omit it and the host falls back to its default data location.

.EXAMPLE
    .\Register-ScheduledTask.ps1 -Executable "C:\CalendarMcp\CalendarMcp.Reminders.exe" -ConfigDirectory "C:\CalendarMcp\config"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,

    [string]$ConfigDirectory,

    [string]$TaskName = 'CalendarMcp.Reminders',

    [int]$RestartDelaySeconds = 60
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Executable)) {
    throw "Executable not found: $Executable"
}

$workingDirectory = Split-Path -Parent $Executable

# A console application started by Task Scheduler pops a visible window on the
# desktop and leaves it there for the whole session. Launching it through a
# hidden PowerShell keeps the host in the background where it belongs; all
# output still goes to the log file under <config>\logs.
$action = New-ScheduledTaskAction `
    -Execute 'powershell.exe' `
    -Argument "-NoProfile -NonInteractive -WindowStyle Hidden -Command `"& '$Executable'`"" `
    -WorkingDirectory $workingDirectory

if (-not [string]::IsNullOrWhiteSpace($ConfigDirectory)) {
    if (-not (Test-Path -LiteralPath $ConfigDirectory)) {
        throw "Config directory not found: $ConfigDirectory"
    }

    [Environment]::SetEnvironmentVariable('CALENDAR_MCP_CONFIG', $ConfigDirectory, 'User')
    Write-Host "Stored CALENDAR_MCP_CONFIG=$ConfigDirectory for the current user."
}

$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -StartWhenAvailable `
    -RestartInterval (New-TimeSpan -Seconds $RestartDelaySeconds) `
    -RestartCount 3 `
    -MultipleInstances IgnoreNew

# A continuous host must not be killed by the idle-timeout policies that are fine
# for short-running tasks. Task Scheduler wants the ISO-8601 duration here; the
# "00:00:00" that [TimeSpan]::Zero renders to is rejected as out of range.
$settings.ExecutionTimeLimit = 'PT0S'
$settings.StopIfGoingOnBatteries = $false

$principal = New-ScheduledTaskPrincipal `
    -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive `
    -RunLevel Limited

$task = New-ScheduledTask `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description 'Calendar MCP reminders: morning digest, pre-meeting alerts, and Telegram commands.'

Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force -ErrorAction Stop | Out-Null

# Verify rather than trust: a rejected task leaves nothing registered, and a
# script that says "registered" when it is not sends the user chasing ghosts.
$registered = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $registered) {
    throw "Task Scheduler rejected the definition and no task named '$TaskName' exists."
}

Write-Host "Registered scheduled task '$TaskName' (state: $($registered.State))."
Write-Host "  Executable: $Executable"
Write-Host ""
Write-Host "Start it now with:  Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "Check it with:      Get-ScheduledTask -TaskName '$TaskName' | Get-ScheduledTaskInfo"