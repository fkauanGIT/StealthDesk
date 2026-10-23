# Installing agents

On a managed machine the agent runs as a service: it starts with Windows, before anyone signs in, and Windows
restarts it 5 seconds after it fails.

## From the command Add device gives

The **Add device** page gives the command that installs it, for PowerShell as an administrator on the machine. It
downloads the agent from the server (`/downloads/agent/windows`, open to anyone, since the machine has no account),
runs it only if its SHA-256 is the one the page named, deletes the download either way, and carries the installer key
but no password. The agent is a single executable with the .NET runtime inside, so the machine needs nothing else.
Running the command again updates an installed agent in place; it keeps the device's identity, so a key that was used
up still works.

The hash guards the download, not the server: it comes from the same server as the file, so it catches a file changed
on its way to the machine, but a server whose own build was replaced names the replacement's hash. Serve the page over
HTTPS. Telling a genuine build from any other takes code signing, which the agent doesn't have yet.

## A server without an agent build

The container image ships that build, and each [release](releasing.md) attaches it too. A server run from source hands
it out once it is published into its downloads folder:

```powershell
dotnet publish StealthDesk.Agent -p:PublishProfile=Windows -o StealthDesk.Web.Server/downloads/windows
```

Without it, the page says the server has no build and gives the command for an agent copied to the machine by hand,
to run from its folder:

```powershell
.\StealthDesk.Agent.exe install --server https://desk.example.com --installer-key <key>
```

## What install and uninstall do

Install copies the agent to `C:\Program Files\StealthDesk\default`, joins the server with the key and stops there if
the server refuses it, then creates the `StealthDesk.Agent` service (LocalSystem, automatic start), lists it in
Settings > Apps and starts it. The service itself never gets the key: the device joined already. Its settings,
private key and logs are in `C:\ProgramData\StealthDesk\default`, which only administrators and SYSTEM can open.
Stopping the service disconnects it, so the device shows offline at once.

```powershell
.\StealthDesk.Agent.exe uninstall
```

Uninstall, also run by **Uninstall** in Settings > Apps, removes the service, the installed files, the settings and
the device's key. `--instance` works with both, giving each instance its own service (`StealthDesk.Agent.<name>`)
and folders. Debug builds install as `StealthDesk.Agent.Debug` in `Debug` folders, apart from a real installation.
The [agent command line](../reference/agent-command-line.md) lists every option.

## Antivirus software

The agent isn't signed yet, so antivirus software that watches for remote access tools may quarantine it when it
installs as a service. `.github/scripts/agent-download-test.ps1` and `agent-service-test.ps1` check an installation end
to end on a machine where it can run.
