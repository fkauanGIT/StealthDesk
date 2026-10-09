using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.AuthorizationLogs;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Server.Tests;

/// <summary>Writing the authorization change log: what an entry holds, the entry registration leaves, and retention.</summary>
public class AuthorizationChangeTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Registration_LeavesOneEntry_WithThePresetsGranted()
  {
    using var server = await ServerHost.OnPostgresAsync();
    using var client = TestAccounts.Client(server);

    var response = await client.PostAsJsonAsync($"{Routes.Auth}/register", new { email = "first@example.com", password = TestAccounts.Password }, Cancel);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var user = await server.WithDbAsync(db => db.Users.SingleAsync());
    var granted = await server.WithDbAsync(db => db.PermissionAssignments.CountAsync(x => x.PrincipalId == user.Id));
    var entry = Assert.Single(await server.WithDbAsync(db => db.AuthorizationChanges.ToListAsync()));
    Assert.Equal(AuthorizationChangeActions.PermissionAssignmentsSeeded, entry.Action);
    Assert.Equal(AuthorizationChangeActors.System, entry.ActorKind);
    Assert.Null(entry.ActorId);
    Assert.Equal(AuthorizationChangeTargets.User, entry.TargetKind);
    Assert.Equal(user.Id, entry.TargetId);
    Assert.Equal(user.TenantId, entry.OwningTenantId);
    Assert.Null(entry.BeforeJson);
    Assert.InRange(entry.CreatedAt, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);

    var after = JsonSerializer.Deserialize<PermissionSeedSummary>(entry.AfterJson!, JsonSerializerOptions.Web)!;
    Assert.Equal(granted, after.Count);
    Assert.Equal(PermissionPresets.FirstUser.Concat(PermissionPresets.TenantCreator).Concat(PermissionPresets.Baseline).Distinct().Order(), after.Presets.Order());
  }

  [Fact]
  public async Task SeedingPermissionsAlreadyHeld_LeavesNoEntry()
  {
    using var server = ServerHost.InMemory();
    var user = await TestAccounts.CreateUserAsync(server, "ana@example.com");

    await SeedAsync(server, user, PermissionPresets.Baseline);
    await SeedAsync(server, user, PermissionPresets.Baseline);

    var entry = Assert.Single(await server.WithDbAsync(db => db.AuthorizationChanges.ToListAsync()));
    Assert.Contains("\"count\":2", entry.AfterJson);
  }

  [Fact]
  public async Task AnEntryThatCannotBeSaved_LeavesNoPermissionBehind()
  {
    using var server = (await ServerHost.OnPostgresAsync())
      .WithServices(services => services.AddSingleton<IAuthorizationChangeFactory, UnsavableEntries>());
    var user = await TestAccounts.CreateUserAsync(server, "ana@example.com");

    await Assert.ThrowsAsync<DbUpdateException>(() => SeedAsync(server, user, PermissionPresets.Baseline));

    Assert.False(await server.WithDbAsync(db => db.PermissionAssignments.AnyAsync(x => x.PrincipalId == user.Id)));
    Assert.False(await server.WithDbAsync(db => db.AuthorizationChanges.AnyAsync()));
  }

  [Theory]
  [InlineData(null, AuthorizationChangeActors.System)]
  [InlineData(PermissionPrincipalKind.User, AuthorizationChangeActors.User)]
  [InlineData(PermissionPrincipalKind.ServiceAccount, AuthorizationChangeActors.ServiceAccount)]
  public void Entry_NamesWhoMadeTheChange(PermissionPrincipalKind? kind, string expected)
  {
    var actor = kind is { } k ? new Principal(k, Guid.NewGuid(), Guid.NewGuid()) : null;

    var entry = Factory().Create(AuthorizationChangeActions.PermissionAssignmentsSeeded, actor, AuthorizationChangeTargets.User, Guid.NewGuid(), null);

    Assert.Equal(expected, entry.ActorKind);
    Assert.Equal(actor?.Id, entry.ActorId);
  }

  [Fact]
  public void Entry_TakesTheRequestsAddressTraceAndTime()
  {
    var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    var context = new DefaultHttpContext();
    context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:10.0.0.5");
    using var activity = new Activity("test").SetIdFormat(ActivityIdFormat.W3C).Start();

    var entry = Factory(context, clock).Create("action", null, "Target", Guid.NewGuid(), null);

    Assert.Equal("10.0.0.5", entry.IpAddress);
    Assert.Equal(activity.TraceId.ToString(), entry.CorrelationId);
    Assert.Equal(clock.GetUtcNow(), entry.CreatedAt);
  }

  [Fact]
  public void Entry_OutsideARequest_HasNoAddressOrTrace()
  {
    Activity.Current = null;

    var entry = Factory().Create("action", null, "Target", Guid.Empty, null);

    Assert.Null(entry.IpAddress);
    Assert.Null(entry.CorrelationId);
    Assert.Null(entry.TargetId);
  }

  [Fact]
  public void Snapshots_AreCamelCaseJson_WithEnumNamesAndNoNulls()
  {
    var entry = Factory().Create(
      "action",
      null,
      "Target",
      null,
      null,
      before: new { PermissionName = "device.read", Effect = PermissionEffect.Deny, ScopeId = (Guid?)null });

    Assert.Equal("{\"permissionName\":\"device.read\",\"effect\":\"Deny\"}", entry.BeforeJson);
    Assert.Null(entry.AfterJson);
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public async Task Cleanup_RemovesEntriesOlderThanTheRetention(bool postgres)
  {
    using var server = postgres ? await ServerHost.OnPostgresAsync() : ServerHost.InMemory();
    var now = DateTimeOffset.UtcNow;
    var kept = await AddEntriesAsync(server, now.AddDays(-400), now.AddDays(-366), now.AddDays(-10));

    var removed = await server.Services.GetRequiredService<AuthorizationLogCleanup>().CleanAsync(Cancel);

    Assert.Equal(2, removed);
    Assert.Equal([kept[2]], await server.WithDbAsync(db => db.AuthorizationChanges.Select(x => x.Id).ToListAsync()));
  }

  [Fact]
  public async Task Cleanup_WithRetentionZero_KeepsEverything()
  {
    using var server = ServerHost.InMemory().With("AuthorizationLogs:RetentionDays", "0");
    await AddEntriesAsync(server, DateTimeOffset.UtcNow.AddYears(-10));

    var removed = await server.Services.GetRequiredService<AuthorizationLogCleanup>().CleanAsync(Cancel);

    Assert.Equal(0, removed);
    Assert.Equal(1, await server.WithDbAsync(db => db.AuthorizationChanges.CountAsync()));
  }

  private static AuthorizationChangeFactory Factory(HttpContext? context = null, TimeProvider? clock = null) =>
    new(new HttpContextAccessor { HttpContext = context }, clock ?? TimeProvider.System);

  private static async Task SeedAsync(ServerHost server, UserRecord user, IEnumerable<string> presets)
  {
    await using var scope = server.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<PermissionSeeder>().SeedAsync(user.Id, user.TenantId, presets, Cancel);
  }

  // An action longer than its column: PostgreSQL refuses the whole save.
  private sealed class UnsavableEntries : IAuthorizationChangeFactory
  {
    public AuthorizationChangeRecord Create(string action, Principal? actor, string targetKind, Guid? targetId, Guid? owningTenantId, object? before = null, object? after = null) =>
      new() { Action = new string('x', AuthorizationChangeRecord.ActionMax + 1), ActorKind = AuthorizationChangeActors.System, TargetKind = targetKind };
  }

  private static Task<Guid[]> AddEntriesAsync(ServerHost server, params DateTimeOffset[] times) => server.WithDbAsync(async db =>
  {
    var entries = times.Select(x => new AuthorizationChangeRecord
    {
      Id = Guid.NewGuid(),
      Action = "action",
      ActorKind = AuthorizationChangeActors.System,
      TargetKind = "Target",
      CreatedAt = x,
    }).ToList();
    db.AuthorizationChanges.AddRange(entries);
    await db.SaveChangesAsync();
    return entries.Select(x => x.Id).ToArray();
  });
}
