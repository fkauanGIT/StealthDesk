using System.Net;
using Microsoft.Extensions.Options;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.PersonalAccessTokens;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.Bootstrap;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Settings a production server is run with: each invalid one stops the server with its key in the message, outside
/// development the in-memory database and the development password are refused, Docker secrets are read when
/// turned on, and a first administrator can come from configuration.
/// </summary>
public class ServerConfigurationTests
{
  private const string AdminEmail = "owner@example.com";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Theory]
  [InlineData("Accounts:BearerTokenLifetime", "00:00:00", "Accounts:BearerTokenLifetime")]
  [InlineData("Accounts:BearerTokenLifetime", "an hour", "Accounts:BearerTokenLifetime")]
  [InlineData("Accounts:RefreshTokenLifetime", "-1.00:00:00", "Accounts:RefreshTokenLifetime")]
  [InlineData("Accounts:DefaultTheme", "7", "Accounts:DefaultTheme")]
  [InlineData("Accounts:EnablePublicRegistration", "sometimes", "Accounts:EnablePublicRegistration")]
  [InlineData("Email:SmtpPort", "0", "Email:SmtpPort")]
  [InlineData("Email:SmtpPort", "70000", "Email:SmtpPort")]
  [InlineData("Email:SenderAddress", "not an address", "Email:SenderAddress")]
  [InlineData("Gateway:ClockTolerance", "-00:01:00", "Gateway:ClockTolerance")]
  [InlineData("Gateway:AgentCallTimeout", "00:00:00", "Gateway:AgentCallTimeout")]
  [InlineData("ServiceAccounts:AuthFailureLimit", "0", "ServiceAccounts:AuthFailureLimit")]
  [InlineData("ServiceAccounts:AuthFailureWindowMinutes", "0", "ServiceAccounts:AuthFailureWindowMinutes")]
  [InlineData("InstallerKeys:UsageHistoryDays", "ninety", "InstallerKeys:UsageHistoryDays")]
  [InlineData("AuthorizationLogs:RetentionDays", "a year", "AuthorizationLogs:RetentionDays")]
  [InlineData("TenantDefaults:InstanceName", "not valid!", "TenantDefaults:InstanceName")]
  [InlineData("Bootstrap:AdminEmail", AdminEmail, "Bootstrap:AdminPassword")]
  [InlineData("Bootstrap:AdminTokenSecret", "too-short", "Bootstrap:AdminTokenId")]
  [InlineData("EnableDockerSecrets", "true", "EnableDockerSecrets")]
  [InlineData("ApplyMigrationsOnStart", "sometimes", "ApplyMigrationsOnStart")]
  [InlineData("ReverseProxy:TrustedProxies:0", "proxy.local", "ReverseProxy:TrustedProxies holds 'proxy.local'")]
  [InlineData("ReverseProxy:TrustedNetworks:0", "10.0.0.0/33", "ReverseProxy:TrustedNetworks holds '10.0.0.0/33'")]
  [InlineData("ReverseProxy:DockerGatewayAddress", "gateway", "ReverseProxy:DockerGatewayAddress is 'gateway'")]
  [InlineData("ReverseProxy:TrustCloudflare", "maybe", "ReverseProxy:TrustCloudflare")]
  public void AnInvalidSetting_StopsTheServer_NamingItsKey(string key, string value, string named)
  {
    using var server = ServerHost.InMemory().With(key, value);

    // Docker secrets are on, but where they would be mounted there is nothing.
    if (key == "EnableDockerSecrets")
    {
      server.With("DockerSecretsDirectory", Path.Combine(Path.GetTempPath(), $"no-secrets-{Guid.NewGuid():N}"));
    }

    Assert.Contains(named, server.RefusalToStart(), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("Bootstrap:AdminEmail", "not an address", "Bootstrap:AdminEmail")]
  [InlineData("Bootstrap:AdminPassword", "short", "Bootstrap:AdminPassword")]
  public void ABootstrapAdministratorTheServerCantCreate_StopsIt(string key, string value, string named)
  {
    using var server = ServerHost.InMemory()
      .With("Bootstrap:AdminEmail", AdminEmail)
      .With("Bootstrap:AdminPassword", TestAccounts.Password)
      .With(key, value);

    Assert.Contains(named, server.RefusalToStart(), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("abc")]
  [InlineData("70000")]
  public void AnInvalidDatabasePort_StopsTheServer(string port)
  {
    using var server = PostgresSettings().With("POSTGRES_PORT", port);

    Assert.Contains("POSTGRES_PORT", server.RefusalToStart(), StringComparison.Ordinal);
  }

  [Fact]
  public void OutsideDevelopment_TheInMemoryDatabaseIsRefused()
  {
    using var server = ServerHost.InMemory().InEnvironment("Production");

    Assert.Contains("UseInMemoryDatabase", server.RefusalToStart(), StringComparison.Ordinal);
  }

  [Fact]
  public void OutsideDevelopment_TheDevelopmentPasswordIsRefused()
  {
    using var server = PostgresSettings().With("POSTGRES_PASSWORD", "password").InEnvironment("Production");

    Assert.Contains("POSTGRES_PASSWORD", server.RefusalToStart(), StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(true)]
  [InlineData(false)]
  public void DockerSecrets_AreReadOnlyWhenTurnedOn(bool turnedOn)
  {
    var directory = Directory.CreateTempSubdirectory("stealthdesk-secrets-");
    try
    {
      // Named like a variable: the file name is the key, its content the value.
      File.WriteAllText(Path.Combine(directory.FullName, "Accounts__EnablePublicRegistration"), "true");
      using var server = ServerHost.InMemory()
        .With("EnableDockerSecrets", turnedOn ? "true" : "false")
        .With("DockerSecretsDirectory", directory.FullName);

      var accounts = server.Services.GetRequiredService<IOptions<AccountOptions>>().Value;

      Assert.Equal(turnedOn, accounts.EnablePublicRegistration);
    }
    finally
    {
      directory.Delete(recursive: true);
    }
  }

  [Fact]
  public async Task TheBootstrapAdministrator_SignsIn_WithTheFirstUsersPermissions_AndItsTokenWorks()
  {
    var token = Guid.NewGuid();
    var tokenSecret = new string('t', BootstrapOptions.MinimumSecretLength);
    using var server = WithBootstrapAdministrator(ServerHost.InMemory(), token, tokenSecret);
    using var client = TestAccounts.Client(server);

    var signIn = await TestAccounts.SignInAsync(client, AdminEmail);
    using var script = WithToken(server, $"{token}:{tokenSecret}");
    var tenants = await script.GetAsync(Routes.Tenants, Cancel);

    Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    Assert.Equal(HttpStatusCode.OK, tenants.StatusCode);
    var admin = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync());
    Assert.True(admin.EmailConfirmed);
    Assert.Equal(await FirstRegisteredUsersPermissionsAsync(), await PermissionsOfAsync(server, admin.Id));
    var stored = await server.WithDbAsync(db => db.PersonalAccessTokens.IgnoreQueryFilters().SingleAsync());
    Assert.Equal(PersonalAccessTokenMode.InheritOwner, stored.Mode);
    Assert.Null(stored.ExpiresAt);
    Assert.True(await server.WithDbAsync(db => db.AuthorizationChanges.AnyAsync(x =>
      x.Action == AuthorizationChangeActions.PersonalAccessTokenCreated && x.TargetId == token && x.ActorId == null)));
  }

  // A later start finds users and leaves everything as it was, even with other bootstrap settings.
  [Fact]
  public async Task ALaterStart_NeitherCreatesNorChangesTheBootstrapAdministrator()
  {
    var token = Guid.NewGuid();
    var tokenSecret = new string('t', BootstrapOptions.MinimumSecretLength);
    using var server = WithBootstrapAdministrator(ServerHost.InMemory(), token, tokenSecret);
    _ = server.Services;

    using var restarted = server.Restarted()
      .With("Bootstrap:AdminEmail", "someone-else@example.com")
      .With("Bootstrap:AdminPassword", "Another-Passw0rd")
      .With("Bootstrap:AdminTokenId", Guid.NewGuid().ToString());
    using var client = TestAccounts.Client(restarted);
    using var script = WithToken(restarted, $"{token}:{tokenSecret}");

    Assert.Equal(HttpStatusCode.OK, (await TestAccounts.SignInAsync(client, AdminEmail)).StatusCode);
    Assert.NotEqual(HttpStatusCode.OK, (await TestAccounts.SignInAsync(client, AdminEmail, "Another-Passw0rd")).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await script.GetAsync(Routes.Tenants, Cancel)).StatusCode);
    Assert.Equal(1, await restarted.WithDbAsync(db => db.Users.IgnoreQueryFilters().CountAsync()));
    Assert.Equal(token, (await restarted.WithDbAsync(db => db.PersonalAccessTokens.IgnoreQueryFilters().SingleAsync())).Id);
  }

  // With the server's own service account, automation has both keys on the first start.
  [Fact]
  public async Task TheBootstrapServiceAccount_WorksAlongsideTheAdministrator()
  {
    var credential = Guid.NewGuid();
    var secret = new string('s', BootstrapOptions.MinimumSecretLength);
    using var server = WithBootstrapAdministrator(ServerHost.InMemory(), Guid.NewGuid(), new string('t', BootstrapOptions.MinimumSecretLength))
      .With("Bootstrap:ServerServiceAccountName", "Provisioning")
      .With("Bootstrap:ServerServiceAccountCredentialId", credential.ToString())
      .With("Bootstrap:ServerServiceAccountCredentialSecret", secret);
    using var script = TestAccounts.Client(server);
    script.DefaultRequestHeaders.Add(ServiceAccountHeader.Name, $"{credential}:{secret}");

    var tenants = await script.GetAsync(Routes.Tenants, Cancel);

    Assert.Equal(HttpStatusCode.OK, tenants.StatusCode);
  }

  private static ServerHost WithBootstrapAdministrator(ServerHost server, Guid token, string tokenSecret) =>
    server
      .With("Bootstrap:AdminEmail", AdminEmail)
      .With("Bootstrap:AdminPassword", TestAccounts.Password)
      .With("Bootstrap:AdminTokenId", token.ToString())
      .With("Bootstrap:AdminTokenSecret", tokenSecret);

  // PostgreSQL settings the server never gets to use: each test stops it before it connects.
  private static ServerHost PostgresSettings() =>
    ServerHost.InMemory()
      .With("UseInMemoryDatabase", "false")
      .With("POSTGRES_HOST", "localhost")
      .With("POSTGRES_USER", "stealthdesk")
      .With("POSTGRES_PASSWORD", "a-real-password");

  private static HttpClient WithToken(ServerHost server, string token)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add(PersonalAccessTokenHeader.Name, token);
    return client;
  }

  // What the first person to register gets, on a server of its own.
  private static async Task<IReadOnlyList<string>> FirstRegisteredUsersPermissionsAsync()
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);
    var registered = await client.PostAsJsonAsync($"{Routes.Auth}/register", new { email = "first@example.com", password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
    var user = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync());
    return await PermissionsOfAsync(server, user.Id);
  }

  private static Task<IReadOnlyList<string>> PermissionsOfAsync(ServerHost server, Guid userId) => server.WithDbAsync(async db =>
    (IReadOnlyList<string>)(await db.PermissionAssignments.IgnoreQueryFilters()
      .Where(x => x.PrincipalId == userId)
      .Select(x => x.Permission + " " + x.ScopeKind + " " + x.Effect)
      .ToListAsync())
    .Order(StringComparer.Ordinal)
    .ToList());
}

/// <summary>
/// Variables are read from the test process, which every server in the run shares, so these run alone.
/// </summary>
[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

[Collection(nameof(ProcessEnvironment))]
public class EnvironmentVariableTests
{
  [Fact]
  public void APrefixedVariable_SetsTheSetting_OverTheSettingsFile()
  {
    using var variable = new TemporaryVariable("StealthDesk_Accounts__EnablePublicRegistration", "true");
    using var server = ServerHost.InMemory();

    var accounts = server.Services.GetRequiredService<IOptions<AccountOptions>>().Value;

    Assert.True(accounts.EnablePublicRegistration);
  }

  [Fact]
  public void AnInvalidPrefixedVariable_StopsTheServer_NamingTheSetting()
  {
    using var variable = new TemporaryVariable("StealthDesk_Email__SmtpPort", "0");
    using var server = ServerHost.InMemory();

    Assert.Contains("Email:SmtpPort", server.RefusalToStart(), StringComparison.Ordinal);
  }

  private sealed class TemporaryVariable : IDisposable
  {
    private readonly string _name;

    public TemporaryVariable(string name, string value)
    {
      _name = name;
      Environment.SetEnvironmentVariable(name, value);
    }

    public void Dispose() => Environment.SetEnvironmentVariable(_name, null);
  }
}
