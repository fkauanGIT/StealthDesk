# Using the terminal

The terminal runs PowerShell on a device, from the browser: commands, their output, and the questions they ask.

## Who may use it

Using the terminal takes the `device.terminal.use` permission on the device, on a device group it is in, or on the
whole tenant. The **Device Superuser** preset includes it, and so does the person who creates a tenant. Reading a
device (`device.read`) isn't enough. The permission is checked on every command, so a deny added or a grant removed
applies to the next line typed, and the session is closed. [Permissions](../explanation/permissions.md) explains how
grants and denies combine.

!!! warning "Commands run as the agent's account"
    On a device where the agent is installed as a service, that account is **LocalSystem**: a command can do anything
    on the machine, as an administrator could. Give `device.terminal.use` only to people you would give the device's
    administrator password, and prefer granting it on a device or a device group over the whole tenant.

## Opening a terminal

1. Open the device from **Devices**.
2. Choose **Terminal**, at the top right. It shows only to people who may use the terminal on that device, and while
   the device is offline it is disabled, with "Available when the device is online".

The terminal opens a PowerShell session on the device: the first one takes a few seconds while PowerShell loads. It
shows the PowerShell version and the device's system, then a prompt in the agent's folder. Type a command and press
Enter: it shows after the prompt, followed by its output. Errors show in red.

The screen keeps the last 500 lines and follows the output as it arrives, unless you scrolled up to read; `cls`
clears it.

## Keyboard shortcuts

| Keys | What they do |
|---|---|
| Enter | Run the command, or answer the question asked |
| Shift + Enter | New line, in multiline mode |
| Up / Down | The previous and next commands of the last 500, then back to what you were typing |
| Tab / Shift + Tab | The next and previous completion |
| Ctrl + Space or Ctrl + . | List every completion |
| Escape | Close the list of completions, or clear what is typed |

**Keyboard shortcuts**, at the top right, lists them too. With **Multiline** on, under the command box, Shift+Enter
adds a line and Up and Down move between lines; Enter still runs the whole command.

## Completion

Completions come from the device's own PowerShell: commands, parameters, paths on its disks and variables of the
session. Tab completes the word at the caret and walks the matches; typing something else starts over. Ctrl+Space
lists every match under the command box, with a filter: Enter picks the first one left, a click picks any. When more
than 500 match, the terminal says how many instead, so type a few more letters. Completions aren't available while a
command runs.

## Questions a command asks

A command can ask for input: `Read-Host`, a confirmation such as `[Y] Yes [N] No`, fields, or a credential. The next
line you type is the answer. Passwords and other secrets (`Read-Host -AsSecureString`, a credential's password) show
as one `*` per character, and answers never go into the history of commands.

A question that gets no answer for 5 minutes goes on as if Enter was pressed: an empty answer, or the default choice,
and the terminal says so. For a confirmation whose default is Yes, the action goes ahead.

## When the session ends

A session ends when:

- you choose **Close session**, or leave the device's pages (the device's overview and its terminal keep it);
- the command `exit` runs, and the terminal shows its exit code;
- nothing is typed and no command runs for 10 minutes ("Closed after 10 minutes without use.");
- the browser's connection to the server drops, which closes every terminal it opened.

Closing a session stops the command it runs. Choose **New session** to start another. While a device is offline
its terminal says so, and a command typed then is refused with the reason.

## What the terminal can't do

The terminal shows lines of text, not a console window: commands that read single keys or need a nested prompt (the
debugger, `$host.EnterNestedPrompt()`) say they aren't possible here. Colors are left out.

## What is recorded

Each visit to a device's terminal and each terminal call (open, a line sent, completions, close) is recorded in the
server's traces with the user, the device and whether it was accepted, and why not. **What is typed is never
recorded**, neither in traces nor in the server's logs, at any log level. The traces go wherever the server's
telemetry goes: the optional dashboard of the [Docker Compose files](docker-compose.md#the-telemetry-dashboard), or an OpenTelemetry
collector set with `OTEL_EXPORTER_OTLP_ENDPOINT`.
