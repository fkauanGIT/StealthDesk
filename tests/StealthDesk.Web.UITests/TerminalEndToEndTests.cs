using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Playwright;
using StealthDesk.Contracts;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Users;
using StealthDesk.Web.UITests.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace StealthDesk.Web.UITests;

/// <summary>
/// The terminal end to end: a real agent running PowerShell, the server, and a browser. Runs against a server already
/// running (STEALTHDESK_UI_SERVER), starting the agent executable STEALTHDESK_UI_AGENT names, which runs on Windows only.
/// </summary>
public class TerminalEndToEndTests(TerminalEndToEndTests.Deployment deployment) : IClassFixture<TerminalEndToEndTests.Deployment>
{
  private const string Password = "Terminal-Test-1";
  private const string AdminEmail = "terminal-admin@example.com";
  private const string OperatorEmail = "terminal-reader@example.com";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task ATerminalOnARealAgent_RunsCommands_AnswersAQuestion_AndCompletes()
  {
    await deployment.ReadyAsync();
    await using var app = await SignedInAsync(AdminEmail);

    await app.GoAsync($"devices/{deployment.Device}");
    await app.Page.GetByRole(AriaRole.Link, new() { Name = "Terminal", Exact = true }).ClickAsync();
    var screen = app.Page.Locator(".sd-terminal-screen");
    var input = app.Page.GetByRole(AriaRole.Textbox, new() { Name = "Command" });
    // The first session loads PowerShell on the device.
    await Expect(screen).ToContainTextAsync("PS ", new() { Timeout = 60_000 });

    await RunAsync(input, "'answer: ' + (6 * 7)");
    await Expect(screen).ToContainTextAsync("answer: 42");
    await RunAsync(input, "$name = Read-Host 'Your name'; \"Hello, $name\"");
    await Expect(screen).ToContainTextAsync("Your name: ");
    await RunAsync(input, "Ada");
    await Expect(screen).ToContainTextAsync("Hello, Ada");
    await input.FillAsync("Get-Chi");
    await input.PressAsync("Tab");
    await Expect(input).ToHaveValueAsync("Get-ChildItem");
    await app.ScreenshotAsync("terminal");
  }

  [Fact]
  public async Task WithoutThePermission_NoTerminalIsOffered_AndTheHubRefuses()
  {
    await deployment.ReadyAsync();
    await using var app = await SignedInAsync(OperatorEmail);

    await app.GoAsync($"devices/{deployment.Device}");
    await Expect(app.Page.Locator("h1")).ToHaveTextAsync(deployment.MachineName, new() { IgnoreCase = true });
    await Expect(app.Page.Locator(".device-status")).ToHaveTextAsync("Online");
    await Expect(app.Page.GetByRole(AriaRole.Link, new() { Name = "Terminal", Exact = true })).ToHaveCountAsync(0);
    await app.GoAsync($"devices/{deployment.Device}/terminal");
    await Expect(app.Page.GetByText("You may not use the terminal on this device.")).ToBeVisibleAsync();
    await app.ScreenshotAsync("no-terminal");

    await using var hub = await deployment.DashboardAsync(OperatorEmail);
    var opened = await hub.InvokeAsync<GatewayReply<Guid>>(nameof(IDashboardHub.OpenTerminal), deployment.Device, Cancel);
    Assert.Equal("You may not use the terminal on this device.", opened.Error);
  }

  private static async Task<UiApp> SignedInAsync(string email)
  {
    var app = await UiApp.AtDeploymentAsync();
    await app.GoAsync("account/sign-in");
    await app.SignInAsync(email, Password);
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
    return app;
  }

  private static async Task RunAsync(ILocator input, string line)
  {
    await input.FillAsync(line);
    await input.PressAsync("Enter");
    await Expect(input).ToHaveValueAsync(string.Empty);
  }

  /// <summary>
  /// The deployment both tests share: its administrator, a user who may read devices but not use their terminal, and
  /// a real agent online, started once and stopped when the tests are done.
  /// </summary>
  public sealed class Deployment : IAsyncLifetime
  {
    private static readonly string? AgentPath = Environment.GetEnvironmentVariable("STEALTHDESK_UI_AGENT") is { Length: > 0 } path ? path : null;

    private readonly StringBuilder _agentLog = new();
    private Process? _agent;
    private Exception? _failed;

    public Guid Device { get; } = Guid.NewGuid();

    public string MachineName { get; } = Environment.MachineName;

    public async ValueTask InitializeAsync()
    {
      if (UiServer.Address is null || AgentPath is null || !OperatingSystem.IsWindows())
      {
        return;
      }

      try
      {
        await StartAsync();
      }
      catch (Exception ex)
      {
        _failed = ex;
      }
    }

    /// <summary>Skips without a server and an agent; fails, with the agent's log, when they couldn't be set up.</summary>
    public Task ReadyAsync()
    {
      Assert.SkipWhen(UiServer.Address is null, "Runs against a running server: set STEALTHDESK_UI_SERVER.");
      Assert.SkipWhen(AgentPath is null, "Starts a real agent: set STEALTHDESK_UI_AGENT to its executable.");
      Assert.SkipUnless(OperatingSystem.IsWindows(), "The agent runs on Windows only.");
      if (_failed is not null)
      {
        throw new InvalidOperationException($"The deployment couldn't be set up. The agent wrote:\n{AgentLog}", _failed);
      }

      return Task.CompletedTask;
    }

    /// <summary>A connection to the dashboard hub as <paramref name="email"/>, as a browser has.</summary>
    public async Task<HubConnection> DashboardAsync(string email)
    {
      var cookie = await CookieAsync(email);
      var hub = new HubConnectionBuilder()
        .WithUrl(new Uri(UiServer.Address!, Routes.Dashboard), options => options.Headers["Cookie"] = cookie)
        .Build();
      await hub.StartAsync(Cancel);
      return hub;
    }

    public async ValueTask DisposeAsync()
    {
      if (_agent is { HasExited: false })
      {
        _agent.Kill(entireProcessTree: true);
        await _agent.WaitForExitAsync();
      }

      _agent?.Dispose();
    }

    private string AgentLog
    {
      get
      {
        lock (_agentLog)
        {
          return _agentLog.ToString();
        }
      }
    }

    private async Task StartAsync()
    {
      using var anonymous = Client();
      var registered = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email = AdminEmail, password = Password });
      Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
      using var admin = Client(await CookieAsync(AdminEmail));
      var me = (await admin.GetFromJsonAsync<StealthDesk.Contracts.Accounts.CurrentUser>(Routes.CurrentUser))!;

      var created = await admin.PostAsJsonAsync($"{Routes.Users}?tenantId={me.TenantId}", new CreateUserRequest(OperatorEmail, Password: Password));
      Assert.Equal(HttpStatusCode.Created, created.StatusCode);
      var reader = (await created.Content.ReadFromJsonAsync<TenantUser>())!;
      var granted = await admin.PostAsJsonAsync(
        $"{Routes.PermissionAssignments}?tenantId={me.TenantId}",
        new CreatePermissionAssignmentRequest(PermissionPrincipalKind.User, reader.Id, PermissionNames.DeviceRead, PermissionEffect.Allow, PermissionScopeKind.Tenant, me.TenantId));
      Assert.True(granted.IsSuccessStatusCode, await granted.Content.ReadAsStringAsync());

      var key = await admin.PostAsJsonAsync(Routes.InstallerKeys, new CreateInstallerKeyRequest(InstallerKeyType.Persistent, "Terminal end to end"));
      Assert.Equal(HttpStatusCode.Created, key.StatusCode);
      StartAgent((await key.Content.ReadFromJsonAsync<CreatedInstallerKey>())!.FullKey);

      for (var attempt = 0; ; attempt++)
      {
        using var response = await admin.GetAsync($"{Routes.Devices}/{Device}");
        if (response.IsSuccessStatusCode && (await response.Content.ReadFromJsonAsync<StealthDesk.Contracts.Devices.DeviceSummary>())!.IsOnline)
        {
          return;
        }

        if (_agent!.HasExited || attempt == 120)
        {
          throw new TimeoutException("The agent never came online.");
        }

        await Task.Delay(500);
      }
    }

    // An instance of its own, so it never touches an agent installed on the machine.
    private void StartAgent(string installerKey)
    {
      var start = new ProcessStartInfo(AgentPath!)
      {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      };
      foreach (var argument in new[]
      {
        "run", "--instance", $"e2e-{Device:N}"[..16], "--server", UiServer.Address!.ToString(),
        "--installer-key", installerKey, "--device-id", Device.ToString(),
      })
      {
        start.ArgumentList.Add(argument);
      }

      _agent = Process.Start(start) ?? throw new InvalidOperationException("The agent didn't start.");
      _agent.OutputDataReceived += (_, e) => Keep(e.Data);
      _agent.ErrorDataReceived += (_, e) => Keep(e.Data);
      _agent.BeginOutputReadLine();
      _agent.BeginErrorReadLine();
    }

    private void Keep(string? line)
    {
      lock (_agentLog)
      {
        _agentLog.AppendLine(line);
      }
    }

    private static async Task<string> CookieAsync(string email)
    {
      using var client = Client();
      var response = await client.PostAsJsonAsync($"{Routes.Auth}/login?useCookies=true", new { email, password = Password });
      Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      return string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
    }

    private static HttpClient Client(string? cookie = null)
    {
      var client = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = UiServer.Address };
      if (cookie is not null)
      {
        client.DefaultRequestHeaders.Add("Cookie", cookie);
      }

      return client;
    }
  }
}
