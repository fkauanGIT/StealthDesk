<#
.SYNOPSIS
  Runs the install command the Add device page copies, the way an administrator would, against a running server.

.DESCRIPTION
  Needs an administrator, a server started with AgentDownloads:Folder pointing to -Downloads, and the agent's zip as
  the container image builds it: the single-file agent and DesktopClient\ with the single-file desktop client. Checks
  that a tampered download never runs, that the command installs the agent and its desktop client, and that running it
  again updates it in place. Uninstalls at the end.

.EXAMPLE
  ./agent-download-test.ps1 -Bundle publish/StealthDesk.Agent.zip -Downloads C:\server-downloads -Server http://localhost:5099
#>
param(
  [Parameter(Mandatory)] [string] $Bundle,
  [Parameter(Mandatory)] [string] $Downloads,
  [Parameter(Mandatory)] [string] $Server
)

$ErrorActionPreference = "Stop"
# .NET methods resolve relative paths against the process's folder, not PowerShell's: make them absolute.
$Bundle = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Bundle)
$Downloads = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Downloads)
$service = "StealthDesk.Agent"
$installFolder = Join-Path $env:ProgramFiles "StealthDesk\default"
$dataFolder = Join-Path $env:ProgramData "StealthDesk\default"
$installed = Join-Path $installFolder "StealthDesk.Agent.exe"
$installedClient = Join-Path $installFolder "DesktopClient\StealthDesk.DesktopClient.exe"
$served = Join-Path $Downloads "windows\StealthDesk.Agent.zip"
$temporary = Join-Path $env:TEMP "StealthDesk.Agent.zip"
$unpacked = Join-Path $env:TEMP "StealthDesk.Agent"
$appsKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\StealthDesk.Agent"

function Check([string] $what, [bool] $passed, [string] $detail = "") {
  if (-not $passed) { throw "FAILED: $what. $detail" }
  Write-Host "ok - $what $(if ($detail) { "($detail)" })"
}

function WaitFor([string] $what, [scriptblock] $condition, [int] $seconds = 60) {
  $deadline = (Get-Date).AddSeconds($seconds)
  while (-not (& $condition)) {
    if ((Get-Date) -gt $deadline) { throw "FAILED: $what within $seconds seconds." }
    Start-Sleep -Milliseconds 250
  }
}

# Runs the copied command in Windows PowerShell, as an administrator pasting it would.
function RunCommand([string] $command) {
  # What the command writes as an error is part of its output here, not a failure of this script.
  $ErrorActionPreference = "Continue"
  $output = powershell.exe -NoProfile -NonInteractive -Command $command 2>&1 | Out-String
  Write-Host $output.Trim()
  return $output
}

$admin = @{ email = "admin@example.com"; password = "Service-Test-1" } | ConvertTo-Json
try { Invoke-RestMethod "$Server/api/auth/register" -Method Post -Body $admin -ContentType application/json | Out-Null } catch { }
Invoke-RestMethod "$Server/api/auth/login?useCookies=true" -Method Post -Body $admin -ContentType application/json -SessionVariable session | Out-Null

function Devices { @(Invoke-RestMethod "$Server/api/v1/devices" -WebSession $session) | ForEach-Object { $_ } }
function Options { Invoke-RestMethod "$Server/api/v1/deployment-options" -WebSession $session }
function Command([string] $key) {
  $agentDownload = (Options).windowsAgent
  $command = dotnet run "$PSScriptRoot/install-command.cs" -- $Server $key $agentDownload.path $agentDownload.fileName $agentDownload.size $agentDownload.sha256
  if ($LASTEXITCODE -ne 0) { throw "The install command couldn't be built." }
  return ($command | Out-String).Trim()
}

Check "the machine starts without the agent" (-not (Get-Service $service -ErrorAction SilentlyContinue) -and -not (Test-Path $installFolder) -and -not (Test-Path $dataFolder))
Check "the server has no build yet" ($null -eq (Options).windowsAgent)

New-Item -ItemType Directory -Force (Split-Path $served) | Out-Null
Copy-Item $Bundle $served
$build = (Get-FileHash -Algorithm SHA256 $served).Hash
# What the zip holds, to compare with what gets installed.
$expected = Join-Path $env:TEMP "expected-agent-bundle"
Remove-Item -Recurse -Force $expected -ErrorAction SilentlyContinue
Expand-Archive $Bundle $expected
$agentHash = (Get-FileHash -Algorithm SHA256 (Join-Path $expected "StealthDesk.Agent.exe")).Hash
$clientHash = (Get-FileHash -Algorithm SHA256 (Join-Path $expected "DesktopClient\StealthDesk.DesktopClient.exe")).Hash
Check "the zip holds the agent and the desktop client, and nothing else" (@(Get-ChildItem $expected -Recurse -File).Count -eq 2) ((Get-ChildItem $expected -Recurse -File).Name -join ", ")
Remove-Item -Recurse -Force $expected
Check "the server names the build's hash" ((Options).windowsAgent.sha256 -eq $build) $build
$key = (Invoke-RestMethod "$Server/api/v1/installer-keys" -Method Post -ContentType application/json -WebSession $session `
  -Body (@{ type = 0; name = "Copied command"; allowedUses = 1 } | ConvertTo-Json)).fullKey
$command = Command $key
Check "the command carries the installer key and no password" ($command.Contains($key) -and $command -notmatch "password")

Write-Host "--- A tampered download is refused before it runs"
$bytes = [IO.File]::ReadAllBytes($served)
$bytes[$bytes.Length - 1] = $bytes[$bytes.Length - 1] -bxor 0xFF
[IO.File]::WriteAllBytes($served, $bytes)
$output = RunCommand $command
Check "the command says the download doesn't match" ($output -match "does not match its SHA-256 hash")
Check "nothing was installed" (-not (Get-Service $service -ErrorAction SilentlyContinue) -and -not (Test-Path $installFolder) -and -not (Test-Path $dataFolder))
Check "the tampered download was deleted, never unpacked" (-not (Test-Path $temporary) -and -not (Test-Path $unpacked))
Copy-Item $Bundle $served -Force

Write-Host "--- The copied command installs the agent"
$output = RunCommand $command
Check "the service is running" ((Get-Service $service).Status -eq "Running")
Check "the install folder holds the agent and its desktop client alone" (@(Get-ChildItem $installFolder -Recurse -File).Count -eq 2) ((Get-ChildItem $installFolder -Recurse -File).Name -join ", ")
Check "the installed agent is the one in the zip the server named" ((Get-FileHash -Algorithm SHA256 $installed).Hash -eq $agentHash)
Check "the installed desktop client is the one in the zip" ((Get-FileHash -Algorithm SHA256 $installedClient).Hash -eq $clientHash)
Check "the download and what it unpacked were deleted" (-not (Test-Path $temporary) -and -not (Test-Path $unpacked))
WaitFor "the device to come online" { @(Devices | Where-Object { $_.isOnline }).Count -eq 1 }
$device = Devices | Where-Object { $_.isOnline } | Select-Object -First 1
Check "the device appears online" ($null -ne $device) "$($device.name) $($device.id)"

Write-Host "--- Running the command again updates the agent in place"
# The zip keeps its files' dates, so a replaced file looks the same: a file only the old folder had tells instead.
$marker = Join-Path $installFolder "left-from-before.txt"
Set-Content $marker "before"
$before = (Get-CimInstance Win32_Service -Filter "Name='$service'").ProcessId
$output = RunCommand $command
Check "the install folder was replaced" (-not (Test-Path $marker))
Check "the agent was replaced" ((Get-FileHash -Algorithm SHA256 $installed).Hash -eq $agentHash -and (Get-CimInstance Win32_Service -Filter "Name='$service'").ProcessId -ne $before)
Check "the service is running" ((Get-Service $service).Status -eq "Running")
Check "it keeps the device's identity" ((Get-Content (Join-Path $dataFolder "agent-settings.json") -Raw | ConvertFrom-Json).Agent.DeviceId -eq $device.id)
WaitFor "the device to come back online" { (Devices | Where-Object { $_.id -eq $device.id }).isOnline -eq $true }
Check "the same device is online, with a key used up" (@(Devices).Count -eq 1)

Write-Host "--- Uninstall"
cmd.exe /c (Get-ItemProperty $appsKey).UninstallString
WaitFor "the service to be removed" { -not (Get-Service $service -ErrorAction SilentlyContinue) } 90
WaitFor "the install folder to be removed" { -not (Test-Path $installFolder) } 30
Check "the data folder is gone" (-not (Test-Path $dataFolder))
Remove-Item $served
Write-Host "All download checks passed."
