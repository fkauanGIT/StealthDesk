# Agent command line

`StealthDesk.Agent.exe` takes a command and its options. From source, the same comes after `--`:
`dotnet run --project StealthDesk.Agent -- run --installer-key <key>`.

## Commands

| Command | What it does |
|---|---|
| `run` | Connects to the server and keeps reporting this device, in a console or as the Windows service. |
| `install` | Installs this agent as a Windows service that joins the server. Needs an administrator, `--server` and `--installer-key`. See [Installing agents](../guides/installing-agents.md). |
| `uninstall` | Removes this agent's Windows service, its files and the device's identity. Needs an administrator. |

## Options

| Option | Commands | Meaning |
|---|---|---|
| `--server <url>`, `-s` | `run`, `install` | The server's address, `http` or `https`, such as `http://localhost:5099`. Overrides `Agent:ServerUrl`. |
| `--installer-key <key>`, `-k` | `run`, `install` | The installer key, `{id}:{secret}`, that lets this device join. Only needed until it joins, and never saved. Also `Agent:InstallerKey`. |
| `--device-id <id>` | `run`, `install` | The id for a new device; by default the server picks one. A reinstalled agent takes back a known device with it. |
| `--instance <name>`, `-i` | all | Runs more than one agent on the same machine, each with its own folders and service. 1 to 40 letters, digits, `-` or `_`, starting with a letter or digit; `default` is taken by the agent without one. |

## Settings and files

On its first run the agent creates its key pair, and the server assigns the device an id with the first accepted
report. Both are saved and reused on every later run: that is the device's identity.

| What | Where |
|---|---|
| Server address | `Agent:ServerUrl` in [`StealthDesk.Agent/appsettings.json`](https://github.com/fkauanGIT/StealthDesk/blob/main/StealthDesk.Agent/appsettings.json), or `--server <url>` |
| Installer key (never saved by the agent) | `Agent:InstallerKey`, or `--installer-key <key>` (`-k`) |
| Identity (device id, tenant, private key) | `C:\ProgramData\StealthDesk\Debug\default\agent-settings.json` |
| Logs (one file per day, kept for 7 days) | `C:\ProgramData\StealthDesk\Debug\default\logs\` |

Debug builds use the `Debug` folder so development never touches an installed agent; an installed agent uses
`C:\ProgramData\StealthDesk\default`. Use `--instance <name>` (`-i`) to run more than one agent on the same machine,
each with its own folder instead of `default`.
