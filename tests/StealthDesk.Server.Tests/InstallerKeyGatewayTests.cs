using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.InstallerKeys;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Agents joining with installer keys: a new device needs a valid one and joins its tenant, a usage-based key runs
/// out, a known device keeps its own signing key, and a reinstalled agent brings a new one only with a key whose
/// creator may install there.
/// </summary>
public class InstallerKeyGatewayTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task AValidKey_JoinsItsTenant_AndRecordsTheInstall()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await TestTenants.CreateAsync(server, "First");
    var second = await TestTenants.CreateAsync(server, "Second");
    var key = await TestInstallerKeys.CreateAsync(server, second);
    await using var agent = await TestAgent.ConnectAsync(server, withInstallerKey: false);
    var deviceId = Guid.NewGuid();

    var joined = await agent.ReportAsync(TestAgent.Report(deviceId) with { InstallerKey = key });
    var later = await agent.ReportAsync(TestAgent.Report(deviceId));

    Assert.True(joined.Accepted, joined.Error);
    Assert.Equal(second, joined.Value!.TenantId);
    Assert.True(later.Accepted, later.Error);
    var usage = await server.WithDbAsync(db => db.InstallerKeyUsages.SingleAsync());
    Assert.Equal(TestInstallerKeys.IdOf(key), usage.InstallerKeyId);
    Assert.Equal(deviceId, usage.DeviceId);
    Assert.Equal(second, usage.TenantId);
    Assert.Equal(agent.Keys.PublicKey, (await server.WithDbAsync(db => db.Devices.SingleAsync())).PublicKey);
  }

  [Fact]
  public async Task ADeviceWithoutAnId_GetsOne_AndItsInstallNamesIt()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);

    var reply = await agent.ReportAsync(TestAgent.Report(Guid.Empty));

    Assert.True(reply.Accepted, reply.Error);
    Assert.NotEqual(Guid.Empty, reply.Value!.DeviceId);
    Assert.Equal(reply.Value.DeviceId, (await server.WithDbAsync(db => db.InstallerKeyUsages.SingleAsync())).DeviceId);
  }

  [Theory]
  [InlineData(null, "Unknown device.")]
  [InlineData("", "Unknown device.")]
  [InlineData("not-a-key", "Invalid installer key.")]
  [InlineData("wrong-secret", "Invalid installer key.")]
  [InlineData("unknown-id", "Invalid installer key.")]
  public async Task ANewDevice_IsRefused_WithoutAValidKey(string? presented, string reason)
  {
    using var server = await ServerHost.OnPostgresAsync();
    var key = await TestInstallerKeys.CreateAsync(server, await TestTenants.EnsureAsync(server));
    await using var agent = await TestAgent.ConnectAsync(server, withInstallerKey: false);
    var sent = presented switch
    {
      "wrong-secret" => $"{TestInstallerKeys.IdOf(key)}:{new string('x', 64)}",
      "unknown-id" => $"{Guid.NewGuid()}:{key.Split(':')[1]}",
      _ => presented,
    };

    var reply = await agent.ReportAsync(TestAgent.Report(Guid.NewGuid()) with { InstallerKey = sent });

    Assert.False(reply.Accepted);
    Assert.Equal(reason, reply.Error);
    Assert.False(await server.WithDbAsync(db => db.Devices.AnyAsync()));
    Assert.False(await server.WithDbAsync(db => db.InstallerKeyUsages.AnyAsync()));
  }

  [Fact]
  public async Task AUsageBasedKey_StopsAfterItsCount_AndIsRemovedBySystem()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenant = await TestTenants.EnsureAsync(server);
    var key = await TestInstallerKeys.CreateAsync(server, tenant, InstallerKeyType.UsageBased, allowedUses: 2);
    await using var agent = await TestAgent.ConnectAsync(server, withInstallerKey: false);

    var replies = new List<GatewayReply<ReportReceipt>>();
    foreach (var machine in new[] { "PC-1", "PC-2", "PC-3" })
    {
      replies.Add(await agent.ReportAsync(TestAgent.Report(Guid.NewGuid(), machine) with { InstallerKey = key }));
    }

    Assert.True(replies[0].Accepted, replies[0].Error);
    Assert.True(replies[1].Accepted, replies[1].Error);
    Assert.False(replies[2].Accepted);
    Assert.Equal("Invalid installer key.", replies[2].Error);
    Assert.Equal(2, await server.WithDbAsync(db => db.Devices.CountAsync()));
    Assert.False(await server.WithDbAsync(db => db.InstallerKeys.AnyAsync()));
    var removal = await server.WithDbAsync(db => db.AuthorizationChanges.SingleAsync(x => x.Action == AuthorizationChangeActions.InstallerKeyDeleted));
    Assert.Equal(AuthorizationChangeActors.System, removal.ActorKind);
    Assert.Equal(TestInstallerKeys.IdOf(key), removal.TargetId);
    Assert.Equal(tenant, removal.OwningTenantId);
  }

  [Theory]
  [InlineData(InstallerKeyType.TimeBased)]
  [InlineData(InstallerKeyType.UsageBased)]
  public async Task AnExpiredKey_IsRefused_AndRemoved(InstallerKeyType type)
  {
    using var server = await ServerHost.OnPostgresAsync();
    var key = await TestInstallerKeys.CreateAsync(
      server, await TestTenants.EnsureAsync(server), type, allowedUses: 5, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
    await using var agent = await TestAgent.ConnectAsync(server, withInstallerKey: false);

    var reply = await agent.ReportAsync(TestAgent.Report(Guid.NewGuid()) with { InstallerKey = key });

    Assert.False(reply.Accepted);
    Assert.Equal("Invalid installer key.", reply.Error);
    Assert.False(await server.WithDbAsync(db => db.InstallerKeys.AnyAsync()));
  }

  [Fact]
  public async Task APersistentKey_KeepsWorking()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var key = await TestInstallerKeys.CreateAsync(server, await TestTenants.EnsureAsync(server));
    await using var agent = await TestAgent.ConnectAsync(server, withInstallerKey: false);

    for (var i = 0; i < 4; i++)
    {
      var reply = await agent.ReportAsync(TestAgent.Report(Guid.NewGuid(), $"PC-{i}") with { InstallerKey = key });
      Assert.True(reply.Accepted, reply.Error);
    }

    Assert.Equal(4, await server.WithDbAsync(db => db.InstallerKeyUsages.CountAsync()));
  }

  [Fact]
  public async Task ARegisteredDevice_KeepsWorking_AfterItsKeyIsRevoked_EvenStillSendingIt()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await using var agent = await TestAgent.ConnectAsync(server);
    var deviceId = Guid.NewGuid();
    Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId))).Accepted);

    await server.WithDbAsync(async db => await db.InstallerKeys.ExecuteDeleteAsync());
    var reply = await agent.ReportAsync(TestAgent.Report(deviceId) with { CpuLoad = 0.7 });

    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal(0.7, (await server.WithDbAsync(db => db.Devices.SingleAsync())).CpuLoad);
  }

  [Fact]
  public async Task AKnownDevice_SigningWithItsOwnKey_UsesNoneOfItsInstallerKey()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var key = await TestInstallerKeys.CreateAsync(server, await TestTenants.EnsureAsync(server), InstallerKeyType.UsageBased, allowedUses: 2);
    await using var agent = await TestAgent.ConnectAsync(server, withInstallerKey: false);
    var deviceId = Guid.NewGuid();

    for (var i = 0; i < 3; i++)
    {
      Assert.True((await agent.ReportAsync(TestAgent.Report(deviceId) with { InstallerKey = key })).Accepted);
    }

    Assert.Equal(1, await server.WithDbAsync(db => db.InstallerKeyUsages.CountAsync()));
  }

  [Fact]
  public async Task AReinstalledAgent_BringsANewSigningKey_WithAKeyFromSomeoneWhoMayInstallThere()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenant = await TestTenants.EnsureAsync(server);
    var installer = await TestAccounts.CreateUserAsync(server, "installer@example.com", tenant);
    await TestPermissions.AssignAsync(server, installer, PermissionNames.AgentInstall, PermissionScopeKind.Tenant, tenant);
    var key = await TestInstallerKeys.CreateAsync(server, tenant, creatorId: installer.Id);
    var deviceId = Guid.NewGuid();
    await using var before = await TestAgent.ConnectAsync(server);
    Assert.True((await before.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    await using var reinstalled = await TestAgent.ConnectAsync(server, withInstallerKey: false);

    var reply = await reinstalled.ReportAsync(TestAgent.Report(deviceId, "REINSTALLED") with { InstallerKey = key });
    // The old agent, its installer key long gone, can no longer report as the device.
    var oldKey = await before.ReportAsync(TestAgent.Report(deviceId) with { InstallerKey = string.Empty });

    Assert.True(reply.Accepted, reply.Error);
    Assert.Equal(deviceId, reply.Value!.DeviceId);
    var device = await server.WithDbAsync(db => db.Devices.SingleAsync());
    Assert.Equal(reinstalled.Keys.PublicKey, device.PublicKey);
    Assert.Equal("REINSTALLED", device.Name);
    Assert.False(oldKey.Accepted);
    Assert.Equal("Signature verification failed.", oldKey.Error);
  }

  [Theory]
  [InlineData("no-permission")]
  [InlineData("denied")]
  [InlineData("deleted-user")]
  public async Task AReinstall_IsRefused_WhenTheKeysCreatorMayNotInstallThere(string creator)
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenant = await TestTenants.EnsureAsync(server);
    var deviceId = Guid.NewGuid();
    await using var before = await TestAgent.ConnectAsync(server);
    Assert.True((await before.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var user = await TestAccounts.CreateUserAsync(server, "installer@example.com", tenant);
    if (creator == "denied")
    {
      await TestPermissions.AssignAsync(server, user, PermissionNames.AgentInstall, PermissionScopeKind.Tenant, tenant);
      await TestPermissions.AssignAsync(server, user, PermissionNames.AgentInstall, PermissionScopeKind.Tenant, tenant, PermissionEffect.Deny);
    }

    var key = await TestInstallerKeys.CreateAsync(server, tenant, creatorId: creator == "deleted-user" ? Guid.NewGuid() : user.Id);
    await using var reinstalled = await TestAgent.ConnectAsync(server, withInstallerKey: false);

    var reply = await reinstalled.ReportAsync(TestAgent.Report(deviceId, "HIJACKED") with { InstallerKey = key });

    Assert.False(reply.Accepted);
    Assert.Equal("The installer key's creator may not install an agent on this device.", reply.Error);
    var device = await server.WithDbAsync(db => db.Devices.SingleAsync());
    Assert.Equal(before.Keys.PublicKey, device.PublicKey);
    Assert.Equal("OFFICE-PC", device.Name);
    Assert.False(await server.WithDbAsync(db => db.InstallerKeyUsages.AnyAsync(x => x.InstallerKeyId == TestInstallerKeys.IdOf(key))));
  }

  [Fact]
  public async Task AKeyOfAnotherTenant_NeverTakesOverADevice()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var deviceId = Guid.NewGuid();
    await using var before = await TestAgent.ConnectAsync(server);
    Assert.True((await before.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var other = await TestTenants.CreateAsync(server, "Other");
    var key = await TestInstallerKeys.CreateAsync(server, other, creatorKind: InstallerKeyCreatorKind.ServerServiceAccount, creatorId: await ServerAccountAsync(server, enabled: true));
    await using var reinstalled = await TestAgent.ConnectAsync(server, withInstallerKey: false);

    var reply = await reinstalled.ReportAsync(TestAgent.Report(deviceId) with { InstallerKey = key });

    Assert.False(reply.Accepted);
    Assert.Equal("The device belongs to another tenant.", reply.Error);
    Assert.Equal(before.Keys.PublicKey, (await server.WithDbAsync(db => db.Devices.SingleAsync())).PublicKey);
    Assert.False(await server.WithDbAsync(db => db.InstallerKeyUsages.AnyAsync(x => x.InstallerKeyId == TestInstallerKeys.IdOf(key))), "The refused install used the key.");
  }

  [Theory]
  [InlineData(true, true)]
  [InlineData(false, false)]
  public async Task AServerAccountsKey_ReinstallsWhileTheAccountIsEnabled(bool enabled, bool accepted)
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenant = await TestTenants.EnsureAsync(server);
    var deviceId = Guid.NewGuid();
    await using var before = await TestAgent.ConnectAsync(server);
    Assert.True((await before.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var key = await TestInstallerKeys.CreateAsync(server, tenant, creatorKind: InstallerKeyCreatorKind.ServerServiceAccount, creatorId: await ServerAccountAsync(server, enabled));
    await using var reinstalled = await TestAgent.ConnectAsync(server, withInstallerKey: false);

    var reply = await reinstalled.ReportAsync(TestAgent.Report(deviceId) with { InstallerKey = key });

    Assert.Equal(accepted, reply.Accepted);
  }

  [Fact]
  public async Task ATenantAccountsId_NeverPassesForAServerAccount()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var tenant = await TestTenants.EnsureAsync(server);
    var deviceId = Guid.NewGuid();
    await using var before = await TestAgent.ConnectAsync(server);
    Assert.True((await before.ReportAsync(TestAgent.Report(deviceId))).Accepted);
    var tenantAccount = await server.WithDbAsync(async db =>
    {
      var account = new ServiceAccountRecord { Id = Guid.NewGuid(), Kind = ServiceAccountKind.Tenant, TenantId = tenant, Name = "Deployer" };
      db.ServiceAccounts.Add(account);
      await db.SaveChangesAsync();
      return account.Id;
    });
    var key = await TestInstallerKeys.CreateAsync(server, tenant, creatorKind: InstallerKeyCreatorKind.ServerServiceAccount, creatorId: tenantAccount);
    await using var reinstalled = await TestAgent.ConnectAsync(server, withInstallerKey: false);

    var reply = await reinstalled.ReportAsync(TestAgent.Report(deviceId) with { InstallerKey = key });

    Assert.False(reply.Accepted);
  }

  [Fact]
  public async Task TwoInstallsAtOnce_NeverTakeMoreUsesThanTheKeyHas()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var key = await TestInstallerKeys.CreateAsync(server, await TestTenants.EnsureAsync(server), InstallerKeyType.UsageBased, allowedUses: 1);
    var id = TestInstallerKeys.IdOf(key);

    async Task<bool> RedeemAsync()
    {
      await using var scope = server.Services.CreateAsyncScope();
      return await scope.ServiceProvider.GetRequiredService<InstallerKeyService>().RedeemAsync(id, Guid.NewGuid(), null, Cancel);
    }

    var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(RedeemAsync, Cancel)));

    Assert.Equal(1, results.Count(x => x));
    Assert.False(await server.WithDbAsync(db => db.InstallerKeys.AnyAsync()));
  }

  private static Task<Guid> ServerAccountAsync(ServerHost server, bool enabled) => server.WithDbAsync(async db =>
  {
    var account = new ServiceAccountRecord { Id = Guid.NewGuid(), Kind = ServiceAccountKind.Server, Name = $"Deployer {Guid.NewGuid():N}", IsEnabled = enabled };
    db.ServiceAccounts.Add(account);
    await db.SaveChangesAsync();
    return account.Id;
  });
}
