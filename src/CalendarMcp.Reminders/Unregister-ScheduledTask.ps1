<#
.SYNOPSIS
    Removes the Calendar MCP reminders scheduled task.

.DESCRIPTION
    Undoes Register-ScheduledTask.ps1. Pass -ClearState to delete the reminder
    state file as well, which makes the next start replay the current day from
    scratch.

.EXAMPLE
    .\Unregister-ScheduledTask.ps1

.EXAMPLE
    .\Unregister-ScheduledTask.ps1 -ClearState -ConfigDirectory "C:\CalendarMcp\config"
#>
[CmdletBinding()]
param(
    [string]$TaskName = 'CalendarMcp.Reminders',

    [switch]$ClearState,

    [string]$ConfigDirectory
)

$ErrorActionPreference = 'Stop'

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($null -eq $task) {
    Write-Host "Task '$TaskName' is not registered; nothing to remove."
}
else {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Host "Removed scheduled task '$TaskName'."
}

if ($ClearState) {
    if ([string]::IsNullOrWhiteSpace($ConfigDirectory)) {
        $ConfigDirectory = $env:CALENDAR_MCP_CONFIG
    }

    if ([string]::IsNullOrWhiteSpace($ConfigDirectory)) {
        Write-Warning '-ClearState needs -ConfigDirectory or a CALENDAR_MCP_CONFIG value; state file left alone.'
    }
    else {
        $stateFile = Join-Path $ConfigDirectory 'logs\reminder-state.json'
        if (Test-Path -LiteralPath $stateFile) {
            Remove-Item -LiteralPath $stateFile -Force
            Write-Host "Deleted $stateFile"
        }
        else {
            Write-Host "No state file at $stateFile"
        }
    }
}