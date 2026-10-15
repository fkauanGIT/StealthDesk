using System.Net;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Agents;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Contracts.TenantSettings;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Tenant settings: the server's defaults until a tenant sets its own, only for that tenant; values checked and
/// stored one way; and the install options they resolve to.
/// </summary>
public class TenantSettingApiTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task ANewTenant_UsesTheServersDefaults()
  {
    using var server = (await ServerHost.OnPostgresAsync())
      .With("TenantDefaults:AppendInstanceName", "true")
      .With("TenantDefaults:InstanceName", "lab");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var settings = await ListAsync(admin, tenant);

    Assert.Equal(TenantSettingNames.All, settings.Select(x => x.Name));
    Assert.All(settings, x => Assert.Null(x.Value));
    Assert.Equal("true", Setting(settings, TenantSettingNames.AppendInstanceName).DefaultValue);
    Assert.Equal("lab", Setting(settings, TenantSettingNames.InstanceName).DefaultValue);
  }

  [Fact]
  public async Task ATenantsValue_OverridesTheDefault_OnlyForThatTenant()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("Accounts:EnablePublicRegistration", "true");
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var (other, otherTenant) = await RegisterAsync(server, "other@example.com");

    var set = await admin.PutAsJsonAsync(One(TenantSettingNames.AppendInstanceName, tenant), new SetTenantSettingRequest(" TRUE "), Cancel);
    var named = await admin.PutAsJsonAsync(One(TenantSettingNames.InstanceName, tenant), new SetTenantSettingRequest("office"), Cancel);

    Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    Assert.Equal("true", (await set.Content.ReadFromJsonAsync<TenantSetting>(Cancel))!.Value);
    Assert.Equal(HttpStatusCode.OK, named.StatusCode);
    Assert.Equal("true", Setting(await ListAsync(admin, tenant), TenantSettingNames.AppendInstanceName).Value);
    var others = await ListAsync(other, otherTenant);
    Assert.All(others, x => Assert.Null(x.Value));
    Assert.Equal("false", Setting(others, TenantSettingNames.AppendInstanceName).DefaultValue);
    Assert.Equal(HttpStatusCode.Forbidden, (await other.PutAsJsonAsync(One(TenantSettingNames.AppendInstanceName, tenant), new SetTenantSettingRequest("false"), Cancel)).StatusCode);
  }

  [Theory]
  [InlineData(TenantSettingNames.AppendInstanceName, "maybe")]
  [InlineData(TenantSettingNames.InstanceName, "default")]
  [InlineData(TenantSettingNames.InstanceName, "has spaces")]
  [InlineData(TenantSettingNames.InstanceName, "-starts-with-a-dash")]
  [InlineData(TenantSettingNames.InstanceName, "a-name-that-is-far-longer-than-forty-characters")]
  public async Task AnInvalidValue_IsRefused_AndNothingIsSaved(string name, string value)
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var response = await admin.PutAsJsonAsync(One(name, tenant), new SetTenantSettingRequest(value), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.TenantSettings.AnyAsync()));
  }

  [Fact]
  public async Task AnUnknownSetting_IsNotFound_AndRefusedInABatch()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var single = await admin.PutAsJsonAsync(One("theme", tenant), new SetTenantSettingRequest("dark"), Cancel);
    var read = await admin.GetAsync(One("theme", tenant), Cancel);
    var batch = await admin.PutAsJsonAsync(All(tenant), Batch((TenantSettingNames.AppendInstanceName, "true"), ("theme", "dark")), Cancel);

    Assert.Equal(HttpStatusCode.NotFound, single.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
    Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.TenantSettings.AnyAsync()));
  }

  [Fact]
  public async Task ABatch_IsCheckedWhole_ThenSavedTogether()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var refused = await admin.PutAsJsonAsync(All(tenant), Batch((TenantSettingNames.AppendInstanceName, "true"), (TenantSettingNames.InstanceName, "bad name")), Cancel);
    var nothingSaved = await server.WithDbAsync(db => db.TenantSettings.AnyAsync());
    var saved = await admin.PutAsJsonAsync(All(tenant), Batch((TenantSettingNames.AppendInstanceName, "false"), (TenantSettingNames.InstanceName, "branch-2")), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    Assert.False(nothingSaved);
    Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
    var settings = (await saved.Content.ReadFromJsonAsync<TenantSettingList>(Cancel))!.Items;
    Assert.Equal("false", Setting(settings, TenantSettingNames.AppendInstanceName).Value);
    Assert.Equal("branch-2", Setting(settings, TenantSettingNames.InstanceName).Value);
  }

  [Fact]
  public async Task AnEmptyValue_OrDeleting_GoesBackToTheDefault()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    await admin.PutAsJsonAsync(One(TenantSettingNames.AppendInstanceName, tenant), new SetTenantSettingRequest("true"), Cancel);
    await admin.PutAsJsonAsync(One(TenantSettingNames.InstanceName, tenant), new SetTenantSettingRequest("office"), Cancel);

    var emptied = await admin.PutAsJsonAsync(One(TenantSettingNames.AppendInstanceName, tenant), new SetTenantSettingRequest("  "), Cancel);
    var deleted = await admin.DeleteAsync(One(TenantSettingNames.InstanceName, tenant), Cancel);

    Assert.Equal(HttpStatusCode.OK, emptied.StatusCode);
    Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
    Assert.All(await ListAsync(admin, tenant), x => Assert.Null(x.Value));
    Assert.False(await server.WithDbAsync(db => db.TenantSettings.AnyAsync()));
  }

  [Fact]
  public async Task ReadingAndChanging_TakeTheirPermissions()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (_, tenant) = await RegisterAsync(server, "admin@example.com");
    var reader = await TestAccounts.CreateUserAsync(server, "reader@example.com", tenant);
    await TestPermissions.AssignAsync(server, reader, PermissionNames.TenantSettingsRead, PermissionScopeKind.Tenant, tenant);
    await TestAccounts.CreateUserAsync(server, "nobody@example.com", tenant);
    using var readerClient = await ClientForAsync(server, "reader@example.com");
    using var nobody = await ClientForAsync(server, "nobody@example.com");

    Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync(All(tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await readerClient.GetAsync(One(TenantSettingNames.InstanceName, tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.PutAsJsonAsync(One(TenantSettingNames.InstanceName, tenant), new SetTenantSettingRequest("x"), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await readerClient.DeleteAsync(One(TenantSettingNames.InstanceName, tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync(All(tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await nobody.GetAsync(Routes.DeploymentOptions, Cancel)).StatusCode);
  }

  [Fact]
  public async Task AServerAccount_ChangesTheTenantItNames()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var account = await admin.PostAsJsonAsync(Routes.ServerServiceAccounts, new CreateServiceAccountRequest("Provisioner", AccessMode: ServiceAccountAccessMode.Unrestricted), Cancel);
    var accountId = (await account.Content.ReadFromJsonAsync<ServiceAccount>(Cancel))!.Id;
    var credential = await admin.PostAsJsonAsync(Routes.ServiceAccountCredentials(Routes.ServerServiceAccounts, accountId), new CreateServiceAccountCredentialRequest("Primary", null), Cancel);
    using var script = TestAccounts.Client(server);
    script.DefaultRequestHeaders.Add(ServiceAccountHeader.Name, (await credential.Content.ReadFromJsonAsync<CreatedServiceAccountCredential>(Cancel))!.ApiKey);

    var withoutTenant = await script.GetAsync(Routes.TenantSettings, Cancel);
    var unknownTenant = await script.PutAsJsonAsync(One(TenantSettingNames.InstanceName, Guid.NewGuid()), new SetTenantSettingRequest("x"), Cancel);
    var set = await script.PutAsJsonAsync(One(TenantSettingNames.InstanceName, tenant), new SetTenantSettingRequest("provisioned"), Cancel);

    Assert.Equal(HttpStatusCode.Forbidden, withoutTenant.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, unknownTenant.StatusCode);
    Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    Assert.Equal("provisioned", Setting(await ListAsync(admin, tenant), TenantSettingNames.InstanceName).Value);
  }

  [Fact]
  public async Task InstallOptions_NameAnInstance_OnlyWhenTheTenantAppendsOne()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var off = await OptionsAsync(admin);
    await admin.PutAsJsonAsync(One(TenantSettingNames.AppendInstanceName, tenant), new SetTenantSettingRequest("true"), Cancel);
    var fromHost = await OptionsAsync(admin);
    await admin.PutAsJsonAsync(One(TenantSettingNames.InstanceName, tenant), new SetTenantSettingRequest("office"), Cancel);
    var named = await OptionsAsync(admin);

    Assert.Null(off.InstanceName);
    Assert.Equal("localhost", fromHost.InstanceName);
    Assert.Equal("office", named.InstanceName);
  }

  [Fact]
  public void AnInvalidDefaultInstanceName_StopsTheServerFromStarting()
  {
    using var server = ServerHost.InMemory().With("TenantDefaults:InstanceName", "default");

    Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => server.CreateClient());
  }

  [Theory]
  [InlineData("desk.example.com", "desk-example-com")]
  [InlineData("localhost", "localhost")]
  [InlineData("192.168.0.10", "192-168-0-10")]
  [InlineData("default", null)]
  [InlineData("...", null)]
  public void AnInstanceName_FromTheServersAddress_IsAlwaysValid(string host, string? expected)
  {
    var name = InstanceNames.FromHost(host);

    Assert.Equal(expected, name);
    Assert.Null(InstanceNames.Validate(name));
  }

  private static TenantSetting Setting(IEnumerable<TenantSetting> settings, string name) => settings.Single(x => x.Name == name);

  private static string All(Guid tenant) => $"{Routes.TenantSettings}?tenantId={tenant}";

  private static string One(string name, Guid tenant) => $"{Routes.TenantSetting(name)}?tenantId={tenant}";

  private static SetTenantSettingsRequest Batch(params (string Name, string? Value)[] values) =>
    new(values.ToDictionary(x => x.Name, x => x.Value));

  private static async Task<IReadOnlyList<TenantSetting>> ListAsync(HttpClient client, Guid tenant) =>
    (await client.GetFromJsonAsync<TenantSettingList>(All(tenant), Cancel))!.Items;

  private static async Task<DeploymentOptions> OptionsAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<DeploymentOptions>(Routes.DeploymentOptions, Cancel))!;

  private static async Task<(HttpClient Client, Guid TenantId)> RegisterAsync(ServerHost server, string email)
  {
    using var anonymous = TestAccounts.Client(server);
    var response = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var client = await ClientForAsync(server, email);
    return (client, (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!.TenantId);
  }

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }
}
