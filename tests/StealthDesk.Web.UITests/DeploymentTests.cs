using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Users;
using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>
/// A deployment as users run it, at STEALTHDESK_UI_SERVER (the container image, maybe behind a reverse proxy), from
/// a browser and an agent over the network. Skipped without that variable. STEALTHDESK_UI_PHASE picks the test: none
/// for a first look at a new deployment; "before" and then "after" around an upgrade, sharing what they need through
/// the file at STEALTHDESK_UI_STATE.
/// </summary>
public class DeploymentTests
{
  private const string Password = "Deployment-Test-1";
  private const string AdminEmail = "admin@example.com";
  private const string UserEmail = "operator@example.com";
  private const string Machine = "PC-DEPLOYED";

  private static readonly string? Phase = Environment.GetEnvironmentVariable("STEALTHDESK_UI_PHASE") is { Length: > 0 } phase ? phase : null;
  private static readonly string? StateFile = Environment.GetEnvironmentVariable("STEALTHDESK_UI_STATE");

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task ANewDeployment_TakesItsAdministrator_AndShowsAnAgentOnline()
  {
    SkipUnlessPhase(null);
    await using var app = await UiApp.AtDeploymentAsync();

    await app.RegisterAsync(AdminEmail, Password);
    using var api = await SignedInAsync(app.Server, AdminEmail);
    await using var agent = await UiAgent.StartAsync(app.Server, Machine, await CreateKeyAsync(api));

    // The list keeps the devices it shows up to date, but a device that joined after it loaded needs a reload.
    await app.Page.ReloadAsync();
    await Expect(StatusOf(app, Machine)).ToHaveTextAsync("Online");
    await app.SignOutAsync();
    await app.SignInAsync(AdminEmail, Password);
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
    await app.ScreenshotAsync("signed-in-again");
  }

  [Fact]
  public async Task BeforeAnUpgrade_TheDeploymentHoldsUsersADeviceAndAPermission()
  {
    SkipUnlessPhase("before");
    var server = UiServer.Existing();

    // Through the API alone: this phase only puts data in place, and the version upgraded from may have a web client
    // the browser can't run (the one before this test existed did).
    using (var anonymous = Client(server))
    {
      var registered = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email = AdminEmail, password = Password }, Cancel);
      Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
    }

    using var api = await SignedInAsync(server, AdminEmail);
    var created = await api.PostAsJsonAsync(Routes.Users, new CreateUserRequest("operator", UserEmail, Password), Cancel);
    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    var user = (await created.Content.ReadFromJsonAsync<TenantUser>(Cancel))!;
    var granted = await api.PostAsJsonAsync(
      Routes.PermissionAssignments,
      new CreatePermissionAssignmentRequest(PermissionPrincipalKind.User, user.Id, PermissionNames.DeviceRead, PermissionEffect.Allow, PermissionScopeKind.Tenant),
      Cancel);
    Assert.True(granted.IsSuccessStatusCode, $"Granting failed: {granted.StatusCode}");

    AgentIdentity identity;
    await using (var agent = await UiAgent.StartAsync(server, Machine, await CreateKeyAsync(api)))
    {
      var devices = await api.GetFromJsonAsync<List<StealthDesk.Contracts.Devices.DeviceSummary>>(Routes.Devices, Cancel);
      Assert.Contains(devices!, x => x.Id == agent.DeviceId && x.IsOnline);
      identity = agent.Identity;
    }

    // The session the after phase checks: the cookie this client signed in with.
    var cookie = api.DefaultRequestHeaders.GetValues("Cookie").Single();
    await File.WriteAllTextAsync(StateFile!, JsonSerializer.Serialize(new UpgradeState(cookie, user.Id, identity)), Cancel);
  }

  [Fact]
  public async Task AfterAnUpgrade_EverythingIsStillThere_AndSigningInStillWorks()
  {
    SkipUnlessPhase("after");
    var state = JsonSerializer.Deserialize<UpgradeState>(await File.ReadAllTextAsync(StateFile!, Cancel))!;
    var server = UiServer.Existing();

    // A browser session from before the upgrade still holds: the keys that protect it were kept.
    using (var earlier = Client(server))
    {
      earlier.DefaultRequestHeaders.Add("Cookie", state.Cookie);
      var me = await earlier.GetAsync(Routes.CurrentUser, Cancel);
      Assert.Equal(HttpStatusCode.OK, me.StatusCode);
      Assert.Equal(AdminEmail, (await me.Content.ReadFromJsonAsync<CurrentUser>(Cancel))!.Email);
    }

    using var api = await SignedInAsync(server, AdminEmail);
    var users = (await api.GetFromJsonAsync<TenantUserList>(Routes.Users, Cancel))!.Items;
    var user = Assert.Single(users, x => x.Id == state.UserId);
    Assert.Contains(PermissionNames.DeviceRead, user.Permissions);

    await using var app = await UiApp.AtDeploymentAsync();
    await app.GoAsync("account/sign-in");
    await app.SignInAsync(AdminEmail, Password);
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
    await Expect(StatusOf(app, Machine)).ToHaveTextAsync("Offline");

    // The same device comes back on its own key, with no installer key: it is the same row, online.
    await using var agent = await UiAgent.ReturnAsync(server, Machine, state.Agent);
    await Expect(StatusOf(app, Machine)).ToHaveTextAsync("Online");
    await Expect(app.Page.Locator("tbody tr").Filter(new LocatorFilterOptions { HasText = Machine })).ToHaveCountAsync(1);
    await app.ScreenshotAsync("after-upgrade");
  }

  private sealed record UpgradeState(string Cookie, Guid UserId, AgentIdentity Agent);

  private static void SkipUnlessPhase(string? phase)
  {
    Assert.SkipWhen(UiServer.Address is null, "Runs against a deployment: set STEALTHDESK_UI_SERVER.");
    Assert.SkipUnless(Phase == phase, $"Runs in the {phase ?? "first look"} phase; this is {Phase ?? "the first look"}.");
    Assert.SkipWhen(phase is not null && string.IsNullOrEmpty(StateFile), "Needs STEALTHDESK_UI_STATE, the file the upgrade phases share.");
  }

  private static ILocator StatusOf(UiApp app, string machine) =>
    app.Page.Locator("tbody tr").Filter(new LocatorFilterOptions { HasText = machine }).Locator(".device-status");

  // A test deployment behind a reverse proxy has a certificate of its own making.
  private static HttpClient Client(UiServer server) =>
    new(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator, UseCookies = false })
    {
      BaseAddress = server.BaseAddress,
    };

  private static async Task<HttpClient> SignedInAsync(UiServer server, string email)
  {
    var client = Client(server);
    var response = await client.PostAsJsonAsync($"{Routes.Auth}/login?useCookies=true", new { email, password = Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0])));
    return client;
  }

  private static async Task<string> CreateKeyAsync(HttpClient api)
  {
    var response = await api.PostAsJsonAsync(Routes.InstallerKeys, new CreateInstallerKeyRequest(InstallerKeyType.Persistent, "Deployment test"), Cancel);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<CreatedInstallerKey>(Cancel))!.FullKey;
  }
}
