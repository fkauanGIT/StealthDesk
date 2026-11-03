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
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.Users;
using StealthDesk.Web.UITests.Infrastructure;
using static Microsoft.Playwright.Assertions;

namespace StealthDesk.Web.UITests;

/// <summary>
/// Remote control end to end: a real agent and the server, driven from a browser. Runs against a running server
/// (STEALTHDESK_UI_SERVER) with the agent executable STEALTHDESK_UI_AGENT names. The agent here runs as a plain
/// process, so no desktop client is started (that needs the local system); the browser-to-hub-to-agent path, the
/// permission checks, and the page's states are what this proves. Seeing the screen and sending a key are checked by
/// hand on Windows, since the CI runner may have no capturable display.
/// </summary>
public class RemoteControlEndToEndTests(RemoteControlEndToEndTests.Deployment deployment) : IClassFixture<RemoteControlEndToEndTests.Deployment>
{
  private const string Password = "Remote-Test-1";
  private const string AdminEmail = "remote-admin@example.com";
  private const string OperatorEmail = "remote-reader@example.com";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task WithThePermission_TheActionOpensTheDesktop_AndWithoutAClient_SaysNoSession()
  {
    await deployment.ReadyAsync();
    await using var app = await SignedInAsync(AdminEmail);

    await app.GoAsync($"devices/{deployment.Device}");
    await Expect(app.Page.GetByRole(AriaRole.Link, new() { Name = "Remote control", Exact = true })).ToHaveCountAsync(1);
    await app.Page.GetByRole(AriaRole.Link, new() { Name = "Remote control", Exact = true }).ClickAsync();

    // The agent is online but has no desktop client (it isn't the local system here), so it offers no session.
    await Expect(app.Page.GetByText("no desktop session", new() { Exact = false })).ToBeVisibleAsync(new() { Timeout = 30_000 });
    await app.ScreenshotAsync("remote-control-no-session");

    // The hub reaches the agent and comes back with an empty session list, not a refusal.
    await using var hub = await deployment.DashboardAsync(AdminEmail);
    var sessions = await hub.InvokeAsync<GatewayReply<IReadOnlyList<RemoteDesktopSession>>>(nameof(IDashboardHub.GetDesktopSessions), deployment.Device, Cancel);
    Assert.True(sessions.Accepted, sessions.Error);
    Assert.Empty(sessions.Value!);
  }

  [Fact]
  public async Task WithoutThePermission_NoActionIsOffered_AndTheHubRefuses()
  {
    await deployment.ReadyAsync();
    await using var app = await SignedInAsync(OperatorEmail);

    await app.GoAsync($"devices/{deployment.Device}");
    await Expect(app.Page.Locator("h1")).ToHaveTextAsync(deployment.MachineName, new() { IgnoreCase = true });
    await Expect(app.Page.GetByRole(AriaRole.Link, new() { Name = "Remote control", Exact = true })).ToHaveCountAsync(0);
    await app.GoAsync($"devices/{deployment.Device}/remote-control");
    await Expect(app.Page.GetByText("Not allowed")).ToBeVisibleAsync();
    await app.ScreenshotAsync("no-remote-control");

    await using var hub = await deployment.DashboardAsync(OperatorEmail);
    var sessions = await hub.InvokeAsync<GatewayReply<IReadOnlyList<RemoteDesktopSession>>>(nameof(IDashboardHub.GetDesktopSessions), deployment.Device, Cancel);
    Assert.False(sessions.Accepted);
    var started = await hub.InvokeAsync<GatewayReply<RemoteControlTicket>>(nameof(IDashboardHub.StartRemoteControl), deployment.Device, 1, Cancel);
    Assert.False(started.Accepted);
  }

  private static async Task<UiApp> SignedInAsync(string email)
  {
    var app = await UiApp.AtDeploymentAsync();
    await app.GoAsync("account/sign-in");
    await app.SignInAsync(email, Password);
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
    return app;
  }

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

      var key = await admin.PostAsJsonAsync(Routes.InstallerKeys, new CreateInstallerKeyRequest(InstallerKeyType.Persistent, "Remote control end to end"));
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
          throw new TimeoutException($"The agent never came online. It wrote:\n{AgentLog}");
        }

        await Task.Delay(500);
      }
    }

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
        "run", "--instance", $"rc-{Device:N}"[..16], "--server", UiServer.Address!.ToString(),
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
