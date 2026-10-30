# Installing agents

On a managed machine the agent runs as a service: it starts with Windows, before anyone signs in, and Windows
restarts it 5 seconds after it fails.

## From the command Add device gives

The **Add device** page gives the command that installs it, for PowerShell as an administrator on the machine. It
downloads the agent from the server (`/downloads/agent/windows`, open to anyone, since the machine has no account): a
zip of `StealthDesk.Agent.exe` and its [desktop client](#the-desktop-client) in `DesktopClient\`. It unpacks and runs
it only if the zip's SHA-256 is the one the page named, deletes the download and what it unpacked either way, and
carries the installer key but no password. Each program is a single executable with the .NET runtime inside, so the
machine needs nothing else. Running the command again updates an installed agent in place; it keeps the device's
identity, so a key that was used up still works.

The hash guards the download, not the server: it comes from the same server as the file, so it catches a file changed
on its way to the machine, but a server whose own build was replaced names the replacement's hash. Serve the page over
HTTPS. Telling a genuine build from any other takes code signing, which the agent doesn't have yet.

## A server without an agent build

The container image ships that build, and each [release](releasing.md) attaches it too. A server run from source hands
it out once its zip is in the downloads folder, made the way the image makes it:

```powershell
dotnet publish StealthDesk.Agent -p:PublishProfile=Windows -o publish/agent
dotnet publish StealthDesk.DesktopClient -p:PublishProfile=Windows -o publish/desktop-client
New-Item -ItemType Directory -Force publish/bundle/DesktopClient, StealthDesk.Web.Server/downloads/windows | Out-Null
Copy-Item publish/agent/StealthDesk.Agent.exe publish/bundle/
Copy-Item publish/desktop-client/StealthDesk.DesktopClient.exe publish/bundle/DesktopClient/
Compress-Archive -Path publish/bundle/* -DestinationPath StealthDesk.Web.Server/downloads/windows/StealthDesk.Agent.zip -Force
```

Without it, the page says the server has no build and gives the command for an agent copied to the machine by hand,
with its `DesktopClient` folder, to run from its folder:

```powershell
.\StealthDesk.Agent.exe install --server https://desk.example.com --installer-key <key>
```

## What install and uninstall do

Install copies the agent and its desktop client to `C:\Program Files\StealthDesk\default`, joins the server with the
key and stops there if the server refuses it, then creates the `StealthDesk.Agent` service (LocalSystem, automatic
start), lists it in Settings > Apps and starts it. The service itself never gets the key: the device joined already. Its settings,
private key and logs are in `C:\ProgramData\StealthDesk\default`, which only administrators and SYSTEM can open.
Stopping the service disconnects it, so the device shows offline at once.

```powershell
.\StealthDesk.Agent.exe uninstall
```

Uninstall, also run by **Uninstall** in Settings > Apps, removes the service, the installed files, the settings and
the device's key. `--instance` works with both, giving each instance its own service (`StealthDesk.Agent.<name>`)
and folders. Debug builds install as `StealthDesk.Agent.Debug` in `Debug` folders, apart from a real installation.
The [agent command line](../reference/agent-command-line.md) lists every option.

## The desktop client

The service starts `DesktopClient\StealthDesk.DesktopClient.exe` in every Windows session with a screen: the console,
even at its sign-in screen with nobody logged on, and each active remote desktop session. It is what will see and use
that session's desktop for remote control. It runs as LocalSystem inside the session, started with the token of the
session's `winlogon.exe`: only that reaches the sign-in screen and secure screens such as UAC, which belong to no user.

The agent checks the sessions every 5 seconds. A client that exits is started again, after 2 seconds and then twice as
long each time it exits again, up to a minute; one that stayed up for a minute starts over from 2 seconds. A client
whose session ended is stopped, and so is any client the agent didn't start itself, such as a second one in the same
session, so each session has one. Stopping the service stops its clients. Opened by hand, the desktop client says it
is started by the agent and closes. Each one logs to `logs\desktop-client-session<id>` in the data folder.

The agent starts desktop clients only as the service: run from a console, it never does.

Each desktop client talks to its agent through a named pipe, `StealthDesk-<instance>`, that the agent serves. As the
service, the pipe lets in only LocalSystem and users logged on at a desktop, and denies anonymous connections; those
users may connect but not serve the pipe themselves, so no program of theirs can pose as the agent. No
password is shared: when a process connects, the agent asks Windows which process it is, and keeps the connection only
if it runs the desktop client installed next to the agent and, once the agent is code-signed, is signed with the same
certificate. A program refused 5 times within a minute is turned away without being checked until the minute passes.
A client whose agent goes away tries again every 5 seconds and closes after a minute without reaching it; the agent,
once back, starts new ones. A debug build serves `StealthDesk-debug-<instance>` instead and also takes a desktop client
run from the source tree, so the two can be run by hand.

## Antivirus software

The agent isn't signed yet, so antivirus software that watches for remote access tools may quarantine it when it
installs as a service. `.github/scripts/agent-download-test.ps1` and `agent-service-test.ps1` check an installation end
to end on a machine where it can run.
