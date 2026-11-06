using System.Net;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.TenantSettings;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>Each user's own choice of whether the device's user is told about their sessions, and the tenant's override.</summary>
public class NotifyOnRemoteControlPreferenceTests
{
  private const string BeforeThePreference = "20261105174612_AddDeviceDesktopPreviewReadPermission";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task ANewAccount_IsNotifiedByDefault_AndItsChoiceFollowsItToAnotherSession()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await TestAccounts.CreateUserAsync(server, "admin@example.com");
    using var first = await ClientForAsync(server, "admin@example.com");

    var before = await ProfileAsync(first);
    var chosen = await first.PutAsJsonAsync(Routes.AccountNotifyOnRemoteControl, new NotifyOnRemoteControlUpdate(false), Cancel);
    using var second = await ClientForAsync(server, "admin@example.com");

    Assert.True(before.NotifyOnRemoteControl);
    Assert.False(before.NotifyOnRemoteControlOverriddenByTenant);
    Assert.Equal(HttpStatusCode.NoContent, chosen.StatusCode);
    Assert.False((await ProfileAsync(second)).NotifyOnRemoteControl);
  }

  [Fact]
  public async Task ASignedOutRequest_IsRefused_AndChangesNothing()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "admin@example.com");
    using var anonymous = TestAccounts.Client(server);
    using var client = await ClientForAsync(server, "admin@example.com");

    var signedOut = await anonymous.PutAsJsonAsync(Routes.AccountNotifyOnRemoteControl, new NotifyOnRemoteControlUpdate(false), Cancel);

    Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    Assert.True((await ProfileAsync(client)).NotifyOnRemoteControl);
  }

  [Fact]
  public async Task ATenantsOverride_WinsOverTheUsersOwnChoice_ButCanStillBeChangedForWhenItsLifted()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "admin@example.com");
    using var client = await ClientForAsync(server, "admin@example.com");
    await server.WithDbAsync(db =>
    {
      db.TenantSettings.Add(new TenantSettingRecord
      {
        Id = Guid.NewGuid(),
        TenantId = user.TenantId,
        Name = TenantSettingNames.NotifyUserOnRemoteControl,
        Value = "false",
      });
      return db.SaveChangesAsync();
    });

    var overridden = await ProfileAsync(client);
    await client.PutAsJsonAsync(Routes.AccountNotifyOnRemoteControl, new NotifyOnRemoteControlUpdate(true), Cancel);
    var stillOverridden = await ProfileAsync(client);

    Assert.False(overridden.NotifyOnRemoteControl);
    Assert.True(overridden.NotifyOnRemoteControlOverriddenByTenant);
    // The tenant's "false" still wins even though the user just chose "true"...
    Assert.False(stillOverridden.NotifyOnRemoteControl);
    Assert.True(stillOverridden.NotifyOnRemoteControlOverriddenByTenant);

    await server.WithDbAsync(db => db.TenantSettings.Where(x => x.TenantId == user.TenantId).ExecuteDeleteAsync());
    var afterTheOverrideLifts = await ProfileAsync(client);

    // ...but once the tenant lifts its override, the user's own choice from before takes effect.
    Assert.True(afterTheOverrideLifts.NotifyOnRemoteControl);
    Assert.False(afterTheOverrideLifts.NotifyOnRemoteControlOverriddenByTenant);
  }

  // Accounts from before this preference existed keep being notified, as they always were.
  [Fact]
  public async Task ExistingAccounts_KeepBeingNotified_WhenThePreferenceArrives()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "admin@example.com");

    await MigrateAsync(server, BeforeThePreference);
    await MigrateAsync(server, null);
    var stored = await server.WithDbAsync(db => db.Database
      .SqlQuery<bool>($"SELECT \"NotifyOnRemoteControl\" AS \"Value\" FROM users WHERE \"Id\" = {user.Id}")
      .SingleAsync(Cancel));

    Assert.True(stored);
  }

  private static async Task MigrateAsync(ServerHost server, string? target)
  {
    await using var scope = server.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<StealthDeskDb>().GetService<IMigrator>().MigrateAsync(target, Cancel);
  }

  private static async Task<AccountProfile> ProfileAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<AccountProfile>(Routes.AccountProfile, Cancel))!;

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }
}
