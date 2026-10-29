# v0.6.0 — Remote terminal

Sixth milestone of StealthDesk: a PowerShell terminal on a device, from the browser, for the people allowed to use it.

## What's in it

**The terminal**
- A **Terminal** action on a device's page opens PowerShell 7.6 on the device, running in the agent: commands keep
  their variables and folder between them, errors show in red, and the screen keeps the last 500 lines
- Enter runs a command, Up and Down walk the last 500, Shift+Enter adds a line in multiline mode, and a dialog lists
  every key
- Tab and Shift+Tab complete commands, parameters, paths and variables from the device's own PowerShell; Ctrl+Space
  lists every match to filter and pick
- Questions a command asks (`Read-Host`, confirmations, credentials) are answered in the terminal, secrets shown as
  stars and kept out of the history; one unanswered for 5 minutes goes on as if Enter was pressed
- `cls` clears the screen; programs such as `ping` and `ipconfig` show their output, accents included
- A session ends when the user leaves the device, runs `exit`, or leaves it unused for 10 minutes

**Access**
- A new `device.terminal.use` permission, checked on every command, part of the Device Superuser preset; upgrading
  gives it to whoever was given reading devices by the system
- A terminal's output goes only to the browser that opened it, and its sessions close when that browser disconnects
- Each visit and terminal call is traced with the user, the device and its outcome; what is typed is never recorded,
  in traces or logs, whatever the log level

**Under the hood**
- The server can call a connected agent and wait for its answer, with a time limit (`Gateway:AgentCallTimeout`)
- A device that reconnected could stay marked offline while connected; it no longer does

**Quality**
- The terminal is tested end to end in CI on Windows: the agent from the container image runs PowerShell, through the
  server, from a browser

## Upgrading

Nothing to do beyond the usual [upgrade](https://fkauangit.github.io/StealthDesk/guides/upgrading/): a migration adds
the terminal permission. Review who holds `device.terminal.use` afterwards: commands run as the agent's account,
LocalSystem for an installed agent.
