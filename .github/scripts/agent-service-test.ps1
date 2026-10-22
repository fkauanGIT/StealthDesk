<#
.SYNOPSIS
  Installs the agent as a Windows service against a running server and checks it the way a machine would use it.

.DESCRIPTION
  Needs an administrator and a machine whose antivirus lets an unsigned agent run as a service. Creates a local user
  without administrator rights to read the device key as someone else. Undoes everything it installs.

.EXAMPLE
  ./agent-service-test.ps1 -Agent StealthDesk.Agent/bin/Release/net10.0/StealthDesk.Agent.exe -Server http://localhost:5099
#>
param(
  [Parameter(Mandatory)] [string] $Agent,
  [Parameter(Mandatory)] [string] $Server
)

$ErrorActionPreference = "Stop"
$service = "StealthDesk.Agent"
$installFolder = Join-Path $env:ProgramFiles "StealthDesk\default"
$dataFolder = Join-Path $env:ProgramData "StealthDesk\default"
$settingsFile = Join-Path $dataFolder "agent-settings.json"
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

function ServiceProcess { (Get-CimInstance Win32_Service -Filter "Name='$service'").ProcessId }

$admin = @{ email = "admin@example.com"; password = "Service-Test-1" } | ConvertTo-Json
# Registering fails when another test signed the administrator up first.
try { Invoke-RestMethod "$Server/api/auth/register" -Method Post -Body $admin -ContentType application/json | Out-Null } catch { }
Invoke-RestMethod "$Server/api/auth/login?useCookies=true" -Method Post -Body $admin -ContentType application/json -SessionVariable session | Out-Null
$key = (Invoke-RestMethod "$Server/api/v1/installer-keys" -Method Post -ContentType application/json -WebSession $session `
  -Body (@{ type = 0; name = "Service test"; allowedUses = 5 } | ConvertTo-Json)).fullKey

# The device this installation registered, by the id it saved: other tests may have left devices behind.
function Device { @(Invoke-RestMethod "$Server/api/v1/devices" -WebSession $session) | ForEach-Object { $_ } | Where-Object { $_.id -eq $script:deviceId } }
function Online { (Device).isOnline -eq $true }

Check "the machine starts without the agent" (-not (Get-Service $service -ErrorAction SilentlyContinue) -and -not (Test-Path $installFolder) -and -not (Test-Path $dataFolder) -and -not (Test-Path $appsKey))

Write-Host "--- A key the server refuses leaves nothing behind"
& $Agent install --server $Server --installer-key "00000000-0000-0000-0000-000000000000:not-a-key"
Check "install with a bad key fails" ($LASTEXITCODE -ne 0) "exit code $LASTEXITCODE"
Check "no service is left" (-not (Get-Service $service -ErrorAction SilentlyContinue))
Check "no install folder is left" (-not (Test-Path $installFolder))
Check "no data folder is left" (-not (Test-Path $dataFolder))
Check "no Apps entry is left" (-not (Test-Path $appsKey))

Write-Host "--- Install"
& $Agent install --server $Server --installer-key $key
Check "install succeeds" ($LASTEXITCODE -eq 0) "exit code $LASTEXITCODE"

$config = Get-CimInstance Win32_Service -Filter "Name='$service'"
Check "the service is running" ($config.State -eq "Running")
Check "it starts with Windows" ($config.StartMode -eq "Auto")
Check "it runs as LocalSystem" ($config.StartName -eq "LocalSystem")
Check "it runs the installed copy" ($config.PathName -eq "`"$installFolder\StealthDesk.Agent.exe`" run") $config.PathName
$failure = sc.exe qfailure $service | Out-String
Check "Windows restarts it when it fails" (([regex]::Matches($failure, "RESTART|REINICIAR")).Count -eq 3) ($failure -replace "\s+", " ")
Check "also when it stops with an error" ((sc.exe qfailureflag $service | Out-String) -match "TRUE|VERDADEIRO")

$apps = Get-ItemProperty $appsKey
Check "Apps lists it" ($apps.DisplayName -eq "StealthDesk Agent")
Check "Apps uninstalls it from the install folder" ($apps.UninstallString -eq "`"$installFolder\StealthDesk.Agent.exe`" uninstall") $apps.UninstallString

$script:deviceId = (Get-Content $settingsFile -Raw | ConvertFrom-Json).Agent.DeviceId
WaitFor "the device to come online" { Online }
$device = Device
Check "the device is online" ($device.isOnline -eq $true) "$($device.name) $($device.id)"
$settings = (Get-Content $settingsFile -Raw | ConvertFrom-Json).Agent
Check "the service keeps the device's identity" ($settings.DeviceId -eq $device.id)
Check "the key isn't kept" (-not ((Get-Content $settingsFile -Raw) -match [regex]::Escape($key.Split(":")[1])))

Write-Host "--- A reinstall that can't reach its server keeps the installed agent"
$installed = (Get-Item "$installFolder\StealthDesk.Agent.exe").LastWriteTimeUtc
& $Agent install --server "http://127.0.0.1:9" --installer-key $key
Check "the reinstall fails" ($LASTEXITCODE -ne 0) "exit code $LASTEXITCODE"
Check "the installed service runs again" ((Get-Service $service).Status -eq "Running")
Check "its files weren't touched" ((Get-Item "$installFolder\StealthDesk.Agent.exe").LastWriteTimeUtc -eq $installed)
Check "it still reports to its server" ((Get-Content $settingsFile -Raw | ConvertFrom-Json).Agent.ServerUrl -eq "$Server/")
WaitFor "the device to come back online" { Online }
Check "the device is online again" (Online)

Write-Host "--- Only administrators and SYSTEM can read the device key"
$acl = Get-Acl $dataFolder
$allowed = $acl.Access | ForEach-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value } | Sort-Object -Unique
Check "the data folder inherits nothing" ($acl.AreAccessRulesProtected)
Check "it grants Administrators and SYSTEM only" (($allowed -join ",") -eq "S-1-5-18,S-1-5-32-544") ($allowed -join ",")

$userName = "sdtest$(Get-Random -Maximum 9999)"
$password = "P@" + [guid]::NewGuid().ToString("N")
New-LocalUser $userName -Password (ConvertTo-SecureString $password -AsPlainText -Force) -AccountNeverExpires | Out-Null
# A regular user: a member of Users (by its SID, whatever the system's language), and of nothing more.
Add-LocalGroupMember -SID "S-1-5-32-545" -Member $userName
try {
  $credential = [pscredential]::new("$env:COMPUTERNAME\$userName", (ConvertTo-SecureString $password -AsPlainText -Force))
  # This process writes what the user's process prints: the user can't write into the administrator's folders.
  $result = Join-Path ([IO.Path]::GetTempPath()) "$userName.txt"
  $probe = "whoami; try { Get-Content '$settingsFile' -ErrorAction Stop | Out-Null; 'read' } catch [UnauthorizedAccessException] { 'denied' } catch { 'other: ' + `$_.Exception.GetType().Name }"
  Start-Process powershell.exe -Credential $credential -ArgumentList "-NoProfile", "-Command", $probe -Wait -WorkingDirectory $env:SystemRoot -RedirectStandardOutput $result
  $lines = @(Get-Content $result | ForEach-Object { $_.Trim() } | Where-Object { $_ })
  Check "the check ran as that user" ($lines[0] -like "*\$userName") $lines[0]
  Check "a regular user can't read the device key" ($lines[-1] -eq "denied") $lines[-1]
}
finally {
  Remove-LocalUser $userName -ErrorAction SilentlyContinue
  Remove-Item $result -ErrorAction SilentlyContinue
}

Write-Host "--- Killing the process"
$before = ServiceProcess
Stop-Process -Id $before -Force
WaitFor "Windows to restart the service" { $after = ServiceProcess; $after -and $after -ne $before -and (Get-Service $service).Status -eq "Running" }
Check "Windows restarted it" $true "process $before, then $(ServiceProcess)"
WaitFor "the device to come back online" { Online }
Check "the device is back online" (Online)

Write-Host "--- A clean stop"
$stopped = Get-Date
Stop-Service $service
WaitFor "the device to go offline" { -not (Online) } 10
Check "the server shows the device offline right away" $true "$([int]((Get-Date) - $stopped).TotalMilliseconds) ms after stopping"
Start-Service $service
WaitFor "the device to come back online" { Online }
Check "it comes back when started" (Online)

Write-Host "--- Uninstall, the way Apps does it"
cmd.exe /c $apps.UninstallString
WaitFor "the service to be removed" { -not (Get-Service $service -ErrorAction SilentlyContinue) } 90
WaitFor "the install folder to be removed" { -not (Test-Path $installFolder) } 30
WaitFor "the copy uninstall ran from to remove itself" { -not (Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter "StealthDesk-agent-*") } 30
Check "the service is gone" (-not (Get-Service $service -ErrorAction SilentlyContinue))
Check "the install folder is gone" (-not (Test-Path $installFolder))
Check "no empty StealthDesk folder is left in Program Files" (-not (Test-Path (Split-Path $installFolder)))
Check "the settings and device key are gone" (-not (Test-Path $dataFolder))
Check "the Apps entry is gone" (-not (Test-Path $appsKey))
Check "no copy is left in the temp folder" (-not (Get-ChildItem ([IO.Path]::GetTempPath()) -Directory -Filter "StealthDesk-agent-*"))
WaitFor "the device to show offline" { -not (Online) } 10
Write-Host "All service checks passed."
