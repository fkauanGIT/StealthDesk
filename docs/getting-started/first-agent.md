# Your first agent

This page joins the machine you're on to [your first server](first-server.md), running the agent from its source
code. To install agents on managed machines, see [Installing agents](../guides/installing-agents.md).

The agent runs on Windows. A new device joins a tenant with an installer key, so the server needs its first account
before an agent can join.

## Join with an installer key

Choose **Add device** on the device list, create a key and copy the command it shows. From a second terminal, run
the agent with that key:

```
dotnet run --project StealthDesk.Agent -- run --installer-key <key>
```

In a few seconds the machine appears in the list as online, without refreshing the page. Stop the agent with
`Ctrl+C` and it turns offline; start it again, with or without the key, and the same device comes back online.

The key is only needed until the device joins: from then on the device signs its reports with its own key, and
revoking the installer key doesn't affect it. Keys are usage-based (a number of installs within 24 hours),
time-based (until they expire) or persistent (until revoked), and **Installer keys** lists each key's installs.
Installs are kept for `InstallerKeys:UsageHistoryDays` (90 by default; zero keeps them all). A reinstalled agent
brings a new signing key: the server accepts it for a known device only with a key whose creator has `agent.install`
there, and `--device-id <id>` names the device to take back. [How agents are trusted](../explanation/agent-trust.md)
explains why.

## The same through the API

Scripts can do the same through the API (in PowerShell):

```powershell
$account = @{ email = "admin@example.com"; password = "Choose-a-Passw0rd" } | ConvertTo-Json
Invoke-RestMethod http://localhost:5099/api/auth/register -Method Post -Body $account -ContentType "application/json"
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-WebRequest "http://localhost:5099/api/auth/login?useCookies=true" -Method Post -Body $account -ContentType "application/json" -WebSession $session | Out-Null
Invoke-RestMethod http://localhost:5099/api/v1/devices -WebSession $session
```

## Where the agent keeps its identity

On its first run the agent creates its key pair, and the server assigns the device an id with the first
accepted report. Both are saved and reused on every later run: that is the device's identity. The
[agent command line](../reference/agent-command-line.md) reference lists where the agent finds its settings and keeps
its files.
