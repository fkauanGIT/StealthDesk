using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServerSettings;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Contracts.Tenants;
using StealthDesk.Contracts.TenantSettings;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Email;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Server administration: tenants listed, created, renamed and deleted with everything in them; the alert every
/// signed-in user sees; and a test email to the administrator's own address. None of it for anyone else.
/// </summary>
public class ServerAdministrationTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task ATenantsLife_ListedWithWhatItHolds()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, own) = await RegisterAsync(server, "admin@example.com");

    var created = await admin.PostAsJsonAsync(Routes.Tenants, new CreateTenantRequest("  Branch office "), Cancel);
    var branch = (await created.Content.ReadFromJsonAsync<TenantSummary>(Cancel))!;
    await TestAccounts.CreateUserAsync(server, "clerk@example.com", branch.Id);
    var renamed = await admin.PutAsJsonAsync(Routes.Tenant(branch.Id), new RenameTenantRequest("Branch"), Cancel);
    var listed = await ListAsync(admin);
    var one = await admin.GetFromJsonAsync<TenantSummary>(Routes.Tenant(branch.Id), Cancel);
    var deleted = await admin.DeleteAsync(Routes.Tenant(branch.Id), Cancel);
    var missing = await admin.GetAsync(Routes.Tenant(branch.Id), Cancel);

    Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    Assert.Equal("Branch office", branch.Name);
    Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    Assert.Equal("Branch", listed.Single(x => x.Id != own).Name);
    Assert.Equal(2, listed.Count);
    Assert.Equal(1, one!.UserCount);
    Assert.Equal(1, listed.Single(x => x.Id == own).UserCount);
    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  [InlineData("too long")]
  public async Task ATenantNeedsAName(string name)
  {
    name = name == "too long" ? new string('x', CreateTenantRequest.NameMax + 1) : name;
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, own) = await RegisterAsync(server, "admin@example.com");

    var created = await admin.PostAsJsonAsync(Routes.Tenants, new CreateTenantRequest(name), Cancel);
    var renamed = await admin.PutAsJsonAsync(Routes.Tenant(own), new RenameTenantRequest(name), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
    Assert.Single(await ListAsync(admin));
  }

  [Fact]
  public async Task DeletingATenant_RemovesEverythingInIt_AndNothingElse()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    var (admin, own) = await RegisterAsync(server, "admin@example.com");
    var (owner, doomed) = await RegisterAsync(server, "owner@example.com");
    var ownerId = (await owner.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!.Id;

    // Something of every kind, in the tenant to delete and in the one that stays.
    foreach (var tenant in new[] { own, doomed })
    {
      await server.WithDbAsync(async db =>
      {
        db.Devices.Add(new DeviceRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = "PC" });
        db.UserGroups.Add(new UserGroupRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = "Support" });
        db.TenantSettings.Add(new TenantSettingRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = TenantSettingNames.InstanceName, Value = "office" });
        db.ServiceAccounts.Add(new ServiceAccountRecord { Id = Guid.NewGuid(), Kind = ServiceAccountKind.Tenant, TenantId = tenant, Name = "Sync" });
        return await db.SaveChangesAsync();
      });
      await TestInstallerKeys.CreateAsync(server, tenant);
    }

    // The owner also holds a server-wide permission, which belongs to no tenant.
    await TestPermissions.AssignAsync(server, (await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(x => x.Id == ownerId))), PermissionNames.ServerTenantsRead, PermissionScopeKind.Server);

    var deleted = await admin.DeleteAsync(Routes.Tenant(doomed), Cancel);
    var ownerAfter = await owner.GetAsync(Routes.CurrentUser, Cancel);

    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, ownerAfter.StatusCode);
    var left = await server.WithDbAsync(async db => new
    {
      Users = await db.Users.IgnoreQueryFilters().CountAsync(x => x.TenantId == doomed),
      Devices = await db.Devices.IgnoreQueryFilters().CountAsync(x => x.TenantId == doomed),
      Groups = await db.UserGroups.IgnoreQueryFilters().CountAsync(x => x.TenantId == doomed),
      Settings = await db.TenantSettings.IgnoreQueryFilters().CountAsync(x => x.TenantId == doomed),
      Accounts = await db.ServiceAccounts.CountAsync(x => x.TenantId == doomed),
      Keys = await db.InstallerKeys.IgnoreQueryFilters().CountAsync(x => x.TenantId == doomed),
      Assignments = await db.PermissionAssignments.IgnoreQueryFilters().CountAsync(x => x.OwningTenantId == doomed || x.PrincipalId == ownerId),
      Log = await db.AuthorizationChanges.CountAsync(x => x.OwningTenantId == doomed),
      OwnDevices = await db.Devices.IgnoreQueryFilters().CountAsync(x => x.TenantId == own),
      OwnKeys = await db.InstallerKeys.IgnoreQueryFilters().CountAsync(x => x.TenantId == own),
      OwnAccounts = await db.ServiceAccounts.CountAsync(x => x.TenantId == own),
      OwnLog = await db.AuthorizationChanges.CountAsync(x => x.OwningTenantId == own),
    });
    Assert.Equal(0, left.Users + left.Devices + left.Groups + left.Settings + left.Accounts + left.Keys + left.Assignments + left.Log);
    Assert.Equal(1, left.OwnDevices);
    Assert.Equal(1, left.OwnKeys);
    Assert.Equal(1, left.OwnAccounts);
    Assert.True(left.OwnLog > 0);
    Assert.Single(await ListAsync(admin));
  }

  [Fact]
  public async Task TheTenantSignedInTo_CantBeDeletedFromItsOwnSession()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, own) = await RegisterAsync(server, "admin@example.com");

    var deleted = await admin.DeleteAsync(Routes.Tenant(own), Cancel);

    Assert.Equal(HttpStatusCode.Conflict, deleted.StatusCode);
    Assert.True(await server.WithDbAsync(db => db.Tenants.AnyAsync(x => x.Id == own)));
  }

  [Fact]
  public async Task OnlyTheServersAdministrators_ManageTenantsAndSettings()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    var (_, own) = await RegisterAsync(server, "admin@example.com");
    var (tenantAdmin, theirs) = await RegisterAsync(server, "owner@example.com");

    var responses = new[]
    {
      await tenantAdmin.GetAsync(Routes.Tenants, Cancel),
      await tenantAdmin.GetAsync(Routes.Tenant(theirs), Cancel),
      await tenantAdmin.PostAsJsonAsync(Routes.Tenants, new CreateTenantRequest("Mine"), Cancel),
      await tenantAdmin.PutAsJsonAsync(Routes.Tenant(theirs), new RenameTenantRequest("Renamed"), Cancel),
      await tenantAdmin.DeleteAsync(Routes.Tenant(own), Cancel),
      await tenantAdmin.PutAsJsonAsync(Routes.ServerAlert, new ServerAlert("Hello", AlertSeverity.Info, true, false, true), Cancel),
      await tenantAdmin.PostAsync(Routes.ServerTestEmail, null, Cancel),
    };

    Assert.All(responses, x => Assert.Equal(HttpStatusCode.Forbidden, x.StatusCode));
    Assert.Equal(2, await server.WithDbAsync(db => db.Tenants.CountAsync()));
  }

  [Fact]
  public async Task TheAlert_IsSetByAdministrators_AndShownToEveryone()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    var (admin, _) = await RegisterAsync(server, "admin@example.com");
    var (someone, _) = await RegisterAsync(server, "owner@example.com");
    using var anonymous = TestAccounts.Client(server);

    var none = await someone.GetAsync(Routes.ServerAlert, Cancel);
    var emptyShown = await admin.PutAsJsonAsync(Routes.ServerAlert, new ServerAlert("  ", AlertSeverity.Info, true, false, true), Cancel);
    var unknownSeverity = await admin.PutAsJsonAsync(Routes.ServerAlert, new ServerAlert("Hi", (AlertSeverity)9, true, false, true), Cancel);
    var tooLong = await admin.PutAsJsonAsync(Routes.ServerAlert, new ServerAlert(new string('x', ServerAlert.MessageMax + 1), AlertSeverity.Info, true, false, true), Cancel);
    var set = await admin.PutAsJsonAsync(Routes.ServerAlert, new ServerAlert(" Maintenance at 18:00 ", AlertSeverity.Warning, IsDismissible: true, ComesBack: false, IsEnabled: true), Cancel);
    var seen = await someone.GetFromJsonAsync<ServerAlert>(Routes.ServerAlert, Cancel);
    var turnedOff = await admin.PutAsJsonAsync(Routes.ServerAlert, new ServerAlert(string.Empty, AlertSeverity.Info, false, false, IsEnabled: false), Cancel);
    var signedOut = await anonymous.GetAsync(Routes.ServerAlert, Cancel);

    Assert.Equal(HttpStatusCode.NoContent, none.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, emptyShown.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, unknownSeverity.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    Assert.Equal(new ServerAlert("Maintenance at 18:00", AlertSeverity.Warning, true, false, true), seen);
    Assert.Equal(HttpStatusCode.OK, turnedOff.StatusCode);
    Assert.False((await someone.GetFromJsonAsync<ServerAlert>(Routes.ServerAlert, Cancel))!.IsEnabled);
    Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    Assert.Equal(1, await server.WithDbAsync(db => db.ServerAlerts.CountAsync()));
  }

  [Fact]
  public async Task TheTestEmail_GoesToTheAdministratorsOwnAddress()
  {
    var emails = new CapturedEmails();
    using var server = (await ServerHost.OnPostgresAsync()).WithCapturedEmails(emails);
    var (admin, _) = await RegisterAsync(server, "admin@example.com");

    var response = await admin.PostAsync(Routes.ServerTestEmail, null, Cancel);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("admin@example.com", (await response.Content.ReadFromJsonAsync<TestEmailResult>(Cancel))!.To);
    Assert.Contains("test message", emails.To("admin@example.com").HtmlBody);
  }

  [Fact]
  public async Task ATestEmailThatCantBeSent_SaysWhy()
  {
    using var server = (await ServerHost.OnPostgresAsync())
      .With("Email:DisableSending", "false")
      .WithServices(services => services.AddSingleton<IEmailTransport>(new FailingTransport()));
    var (admin, _) = await RegisterAsync(server, "admin@example.com");

    var response = await admin.PostAsync(Routes.ServerTestEmail, null, Cancel);

    Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    Assert.Contains("The mail server is down.", await response.Content.ReadAsStringAsync(Cancel));
  }

  [Theory]
  [InlineData(true, null, EmailDeliveryStatus.Disabled)]
  [InlineData(false, null, EmailDeliveryStatus.Failed)]
  public async Task TheTransport_TellsWhatBecameOfAMessage(bool disabled, string? host, EmailDeliveryStatus expected)
  {
    var options = new EmailOptions { DisableSending = disabled, SmtpHost = host };
    var transport = new EmailTransport(new FixedOptions<EmailOptions>(options), new FakeEnvironment(), NullLogger<EmailTransport>.Instance);

    var delivery = await transport.TrySendAsync(new EmailMessage("admin@example.com", "Hi", "<p>Hi</p>"), Cancel);

    Assert.Equal(expected, delivery.Status);
  }

  private static async Task<IReadOnlyList<TenantSummary>> ListAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<TenantList>(Routes.Tenants, Cancel))!.Items;

  private static async Task<(HttpClient Client, Guid TenantId)> RegisterAsync(ServerHost server, string email)
  {
    using var anonymous = TestAccounts.Client(server);
    var response = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return (client, (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!.TenantId);
  }

  private sealed class FailingTransport : IEmailTransport
  {
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<EmailDelivery> TrySendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
      Task.FromResult(new EmailDelivery(EmailDeliveryStatus.Failed, "The mail server is down."));
  }

  private sealed class FixedOptions<T>(T value) : IOptionsMonitor<T>
  {
    public T CurrentValue => value;

    public T Get(string? name) => value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
  }

  private sealed class FakeEnvironment : IHostEnvironment
  {
    public string EnvironmentName { get; set; } = "Production";
    public string ApplicationName { get; set; } = "Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
  }
}
