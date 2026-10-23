# Running the tests

The test projects use [xUnit v3](https://xunit.net/docs/getting-started/v3/whats-new), which builds each
project as an executable. Run them with `dotnet run`, not `dotnet test`:

```
dotnet run --project tests/StealthDesk.Shared.Tests
dotnet run --project tests/StealthDesk.Server.Tests
dotnet run --project tests/StealthDesk.Agent.Tests
dotnet run --project tests/StealthDesk.Web.Client.Tests
```

The gateway and device storage tests run against a real PostgreSQL started in a Docker container by
[Testcontainers](https://dotnet.testcontainers.org/), so Docker must be running; a local PostgreSQL
installation is not needed. Agent tests that call Windows APIs are skipped on other systems. Web client pages are
tested with [bUnit](https://bunit.dev/), which renders them without a browser. CI runs every
test project on each pull request and on every push to `main`, with a separate job for the Windows tests.

## UI tests

`tests/StealthDesk.Web.UITests` drives a real browser with [Playwright](https://playwright.dev/dotnet/) through
registration, sign-in, account settings, two-factor, passkeys and the external providers. Each test starts the built
server as its own process on a fresh PostgreSQL database, so build first and keep Docker running:

```
dotnet build StealthDesk.slnx
dotnet run --project tests/StealthDesk.Web.UITests
```

On Windows they use the installed Edge; elsewhere they install Playwright's Chromium on the first run.
`STEALTHDESK_UI_BROWSER` picks another browser channel (e.g. `chrome`) and `STEALTHDESK_UI_HEADED=1` shows the
window. Each test's last screen and the server's log land in `TestResults/StealthDesk.Web.UITests/`.

They take a few minutes, so CI leaves them out; the **UI tests** workflow runs them when started from the Actions
tab, and on pull requests that change them.
