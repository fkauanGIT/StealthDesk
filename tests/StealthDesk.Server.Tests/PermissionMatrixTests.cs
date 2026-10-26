using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.DeviceGroups;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Invites;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.PersonalAccessTokens;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.ServerSettings;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Contracts.Tenants;
using StealthDesk.Contracts.TenantSettings;
using StealthDesk.Contracts.UserGroups;
using StealthDesk.Contracts.Users;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Dashboard;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Every route that needs a permission, called by a user who holds everything else the call needs: refused without
/// that permission, leaving nothing in the authorization log, and the same call succeeds once it is granted. A change
/// the log keeps is then in it, made by that user. In between, someone of another tenant holding every tenant
/// permission there tries the same call and gets nothing of this tenant.
/// </summary>
public class PermissionMatrixTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public void EveryRouteThatNeedsAPermission_IsInTheMatrix()
  {
    using var server = ServerHost.InMemory();
    var routes = server.Services.GetRequiredService<EndpointDataSource>().Endpoints
      .OfType<RouteEndpoint>()
      .Where(x => NeedsPermission(x.Metadata))
      .Select(x => Key(x.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Single(), x.RoutePattern.RawText!))
      .ToHashSet();
    var covered = Rows.Select(x => Key(x.Method.Method, x.Route)).ToHashSet();

    Assert.True(
      routes.SetEquals(covered),
      $"Not in the matrix:\n{string.Join("\n", routes.Except(covered))}\nIn the matrix but needing no permission:\n{string.Join("\n", covered.Except(routes))}");
  }

  // A change the log keeps but no row makes would go unchecked.
  [Fact]
  public void EveryAuthorizationLogAction_IsMadeByARow()
  {
    var actions = typeof(AuthorizationChangeActions).GetFields(BindingFlags.Public | BindingFlags.Static)
      .Where(x => x.IsLiteral)
      .Select(x => (string)x.GetValue(null)!);

    Assert.Empty(actions.Except(Rows.Select(x => x.Logs).OfType<string>()));
  }

  [Fact]
  public async Task EveryRoute_NeedsItsPermission_AndKeepsOtherTenantsOut()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var world = await World.CreateAsync(server);
    var failures = new List<string>();

    // One server for every row keeps the run short; each row has its own caller and targets, so none leans on another.
    foreach (var row in Rows)
    {
      try
      {
        await CheckAsync(world, row);
      }
      catch (Exception error)
      {
        failures.Add($"{row}: {error.Message}");
      }
    }

    Assert.True(failures.Count == 0, $"{failures.Count} of {Rows.Count} rows failed:\n{string.Join("\n", failures)}");
  }

  // Scripts reach the API with a personal access token or a service account key instead of a session. Those of the
  // other tenant, with all its administrator can do there, read nothing of this one through any route.
  [Fact]
  public async Task CredentialsOfAnotherTenant_ReadNothingHere()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var world = await World.CreateAsync(server);
    var credentials = new Dictionary<string, HttpClient>
    {
      ["personal access token"] = await OutsiderTokenAsync(world),
      ["service account key"] = await OutsiderServiceAccountAsync(world),
    };
    var failures = new List<string>();

    // Proof the credentials work: they read their own tenant.
    foreach (var (kind, client) in credentials)
    {
      using var own = await client.GetAsync($"{Routes.Users}?tenantId={world.OtherTenant}", Cancel);
      Assert.True(own.IsSuccessStatusCode, $"the {kind} can't read its own tenant: {(int)own.StatusCode}");
    }

    foreach (var row in Rows.Where(x => x.Method == HttpMethod.Get && PermissionCatalog.Find(x.Permission)!.AllowsTenantScope))
    {
      var call = await row.Prepare(world, await world.CallerAsync([]));
      foreach (var (kind, client) in credentials)
      {
        using var response = await client.SendAsync(call.ToRequest(), Cancel);
        var body = await response.Content.ReadAsStringAsync(Cancel);
        var reachedNothing = response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest
          || (response.IsSuccessStatusCode && !world.Names(body, except: call.Text));
        if (!reachedNothing)
        {
          failures.Add($"{row} with the other tenant's {kind}: {(int)response.StatusCode} {body}");
        }
      }
    }

    Assert.True(failures.Count == 0, string.Join("\n", failures));
  }

  private static async Task<HttpClient> OutsiderTokenAsync(World world)
  {
    using var response = await world.Outsider.Client.PostAsJsonAsync(Routes.PersonalAccessTokens, new CreatePersonalAccessTokenRequest("Outsider", PersonalAccessTokenMode.InheritOwner), Cancel);
    Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
    var token = (await response.Content.ReadFromJsonAsync<CreatedPersonalAccessToken>(Cancel))!;

    var client = TestAccounts.Client(world.Server);
    client.DefaultRequestHeaders.Add(PersonalAccessTokenHeader.Name, token.PlainTextToken);
    return client;
  }

  private static async Task<HttpClient> OutsiderServiceAccountAsync(World world)
  {
    var outsider = world.Outsider.Client;
    var inTheirs = $"?tenantId={world.OtherTenant}";
    using var created = await outsider.PostAsJsonAsync(Routes.ServiceAccounts + inTheirs, new CreateServiceAccountRequest("Outsider"), Cancel);
    Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync(Cancel));
    var account = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Cancel)).RootElement.GetProperty("id").GetGuid();

    foreach (var permission in PermissionCatalog.All.Where(x => x.AllowsTenantScope))
    {
      using var granted = await outsider.PostAsJsonAsync(
        Routes.PermissionAssignments + inTheirs,
        new CreatePermissionAssignmentRequest(PermissionPrincipalKind.ServiceAccount, account, permission.Name, PermissionEffect.Allow, PermissionScopeKind.Tenant, world.OtherTenant),
        Cancel);
      Assert.True(granted.IsSuccessStatusCode, $"{permission.Name}: {await granted.Content.ReadAsStringAsync(Cancel)}");
    }

    using var key = await outsider.PostAsJsonAsync(Routes.ServiceAccountCredentials(Routes.ServiceAccounts, account) + inTheirs, new CreateServiceAccountCredentialRequest("Outsider", null), Cancel);
    Assert.True(key.IsSuccessStatusCode, await key.Content.ReadAsStringAsync(Cancel));
    var credential = (await key.Content.ReadFromJsonAsync<CreatedServiceAccountCredential>(Cancel))!;

    var client = TestAccounts.Client(world.Server);
    client.DefaultRequestHeaders.Add(ServiceAccountHeader.Name, credential.ApiKey);
    return client;
  }

  // The hub itself only needs sign-in; each method that needs a permission checks it.
  [Fact]
  public async Task DashboardSubscription_RefusesWithoutDeviceRead_AndWorksWithIt()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var world = await World.CreateAsync(server);
    var caller = await world.CallerAsync([]);
    await using var dashboard = await TestDashboard.ConnectAsync(server, caller.Cookie);

    var refused = await SubscribeAsync(dashboard, world.Device);
    await world.GrantAsync(caller.User, PermissionNames.DeviceRead);
    var allowed = await SubscribeAsync(dashboard, world.Device);

    Assert.Empty(refused);
    Assert.Equal([world.Device], allowed);
    // The other one, GetDeviceActions, has its own tests: DeviceActionsTests.
    Assert.Equal(
      [nameof(DashboardHub.GetDeviceActions), nameof(DashboardHub.SubscribeToDevices)],
      typeof(DashboardHub).GetMethods().Where(x => x.GetCustomAttribute<ChecksPermissionAttribute>() is not null).Select(x => x.Name).Order());
  }

  private static async Task<IReadOnlyList<Guid>> SubscribeAsync(TestDashboard dashboard, Guid device)
  {
    var reply = await dashboard.Connection.InvokeAsync<GatewayReply<IReadOnlyList<Guid>>>(nameof(IDashboardHub.SubscribeToDevices), new[] { device }, Cancel);
    Assert.True(reply.Accepted, reply.Error);
    return reply.Value!;
  }

  private static async Task CheckAsync(World world, Row row)
  {
    var caller = await world.CallerAsync(row.Needs);
    var call = await row.Prepare(world, caller);
    var logged = await world.LogCountAsync();

    using (var refused = await caller.Client.SendAsync(call.ToRequest(), Cancel))
    {
      var body = await refused.Content.ReadAsStringAsync(Cancel);
      switch (row.Refusal)
      {
        case Refusal.Forbidden:
          Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"without {row.Permission}: {(int)refused.StatusCode} {body}");
          break;
        case Refusal.NotFound:
          Assert.True(refused.StatusCode == HttpStatusCode.NotFound, $"without {row.Permission}: {(int)refused.StatusCode} {body}");
          break;
        case Refusal.Hidden:
          Assert.True(refused.IsSuccessStatusCode && !body.Contains(call.Shows!.Value.ToString(), StringComparison.OrdinalIgnoreCase), $"without {row.Permission}: {(int)refused.StatusCode} {body}");
          break;
      }
    }

    Assert.True(await world.LogCountAsync() == logged, "a refused call wrote to the authorization log");

    await CheckOutsiderAsync(world, row, call);
    Assert.True(await world.LogCountAsync() == logged, "the other tenant's calls wrote to the authorization log");

    await world.GrantAsync(caller.User, row.Permission);
    var actor = row.LoggedBySystem ? (Guid?)null : caller.User.Id;
    var entries = row.Logs is { } expected ? await world.LogCountAsync(expected, actor) : 0;
    using var allowed = await caller.Client.SendAsync(call.ToRequest(), Cancel);
    var allowedBody = await allowed.Content.ReadAsStringAsync(Cancel);
    Assert.True(allowed.IsSuccessStatusCode, $"with {row.Permission}: {(int)allowed.StatusCode} {allowedBody}");
    if (call.Shows is { } shown)
    {
      Assert.True(allowedBody.Contains(shown.ToString(), StringComparison.OrdinalIgnoreCase), $"with {row.Permission}, {shown} is missing: {allowedBody}");
    }

    if (row.Logs is { } action)
    {
      Assert.True(
        await world.LogCountAsync(action, actor) > entries,
        $"no new {action} entry made by {(row.LoggedBySystem ? "the server" : "the caller")}");
    }
  }

  // Server permissions reach every tenant by design, so only tenant permissions are tried from the other tenant. The
  // call that then succeeds for this tenant's caller shows the outsider changed nothing, e.g. a deleted item is still
  // there to delete.
  private static async Task CheckOutsiderAsync(World world, Row row, Call call)
  {
    if (!PermissionCatalog.Find(row.Permission)!.AllowsTenantScope)
    {
      return;
    }

    var ours = $"tenantId={world.Tenant}";
    if (call.Path.Contains(ours, StringComparison.Ordinal))
    {
      using var aimed = await world.Outsider.Client.SendAsync(call.ToRequest(), Cancel);
      Assert.True(aimed.StatusCode == HttpStatusCode.Forbidden, $"from the other tenant, aimed at this one: {(int)aimed.StatusCode} {await aimed.Content.ReadAsStringAsync(Cancel)}");
    }

    var own = call with { Path = call.Path.Replace(ours, $"tenantId={world.OtherTenant}", StringComparison.Ordinal) };
    // Refused, or answered without anything of this tenant beyond the ids the call itself named: e.g. an empty list
    // for one of its users, or a batch delete reporting the id as failed. Either way nothing changes, which the log
    // and the caller's later call show.
    if (world.Names(own))
    {
      using var reaching = await world.Outsider.Client.SendAsync(own.ToRequest(), Cancel);
      var body = await reaching.Content.ReadAsStringAsync(Cancel);
      Assert.True(
        reaching.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.BadRequest
          || (reaching.IsSuccessStatusCode && !world.Names(body, except: own.Text)),
        $"from the other tenant, naming this one's items: {(int)reaching.StatusCode} {body}");
    }
    else if (own.Method == HttpMethod.Get)
    {
      using var listed = await world.Outsider.Client.SendAsync(own.ToRequest(), Cancel);
      var body = await listed.Content.ReadAsStringAsync(Cancel);
      Assert.True(listed.IsSuccessStatusCode, $"the other tenant's own {own.Path}: {(int)listed.StatusCode} {body}");
      Assert.False(world.Names(body), $"the other tenant's own {own.Path} shows this tenant's items: {body}");
    }
  }

  private static bool NeedsPermission(EndpointMetadataCollection metadata) =>
    metadata.GetOrderedMetadata<ChecksPermissionAttribute>().Count > 0
    || metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>()
      .Any(x => x.Policy is not null && PermissionPolicies.PermissionOf(x.Policy) is not null);

  private static string Key(string method, string route) => $"{method} {route}";

  private enum Refusal
  {
    Forbidden,

    // A single device the caller may not read is answered as if it didn't exist.
    NotFound,

    // A list answers what the caller may read, so a refusal leaves the target out.
    Hidden,
  }

  private sealed record Call(HttpMethod Method, string Path, object? Body = null, Guid? Shows = null)
  {
    public HttpRequestMessage ToRequest() => new(Method, Path) { Content = Body is null ? null : JsonContent.Create(Body, Body.GetType()) };

    public string Text => Path + (Body is null ? string.Empty : JsonSerializer.Serialize(Body, Body.GetType()));
  }

  private sealed record Caller(UserRecord User, HttpClient Client, string Cookie);

  /// <param name="Route">The route as the server maps it, e.g. <c>/api/v1/users/{id:guid}</c>.</param>
  private sealed record Row(HttpMethod Method, string Route, string Permission, Func<World, Caller, Task<Call>> Prepare)
  {
    /// <summary>Other permissions the call needs to succeed, held by the caller from the start.</summary>
    public string[] Needs { get; init; } = [];

    /// <summary>The authorization log action the change writes, if the log keeps it.</summary>
    public string? Logs { get; init; }

    /// <summary>The server writes the entry as its own, e.g. the permissions a new account gets from presets.</summary>
    public bool LoggedBySystem { get; init; }

    public Refusal Refusal { get; init; } = Refusal.Forbidden;

    public override string ToString() => $"{Method} {Route} ({Permission})";
  }

  private static Row Get(string route, string permission, Func<World, Caller, Task<string>> path) =>
    new(HttpMethod.Get, route, permission, async (w, c) => new Call(HttpMethod.Get, await path(w, c)));

  private static Row Send(HttpMethod method, string route, string permission, Func<World, Caller, Task<(string Path, object? Body)>> prepare) =>
    new(method, route, permission, async (w, c) =>
    {
      var (path, body) = await prepare(w, c);
      return new Call(method, path, body);
    });

  private static Task<string> Path(string path) => Task.FromResult(path);

  private static Task<(string, object?)> To(string path, object? body = null) => Task.FromResult((path, body));

  private static string Unique(string name) => $"{name} {Guid.NewGuid():N}"[..Math.Min(name.Length + 9, 60)];

  private static readonly IReadOnlyList<Row> Rows =
  [
    // Authorization logs: a tenant's own log, every tenant's log through the server permission, and server entries.
    Get("/api/v1/authorization-logs", PermissionNames.TenantAuthorizationLogsRead, (w, _) => Path(w.InTenant(Routes.AuthorizationLogs))),
    Get("/api/v1/authorization-logs", PermissionNames.ServerAuthorizationLogsRead, (w, _) => Path($"{Routes.AuthorizationLogs}?tenantId={w.OtherTenant}")),
    Get("/api/v1/authorization-logs/server", PermissionNames.ServerAuthorizationLogsRead, (_, _) => Path(Routes.ServerAuthorizationLogs)),

    Get("/api/v1/deployment-options", PermissionNames.AgentInstall, (w, _) => Path(w.InTenant(Routes.DeploymentOptions))),

    // Devices: a list leaves out what the caller may not read, and a single device is not found.
    new(HttpMethod.Get, "/api/v1/devices", PermissionNames.DeviceRead, (w, _) => Task.FromResult(new Call(HttpMethod.Get, Routes.Devices, Shows: w.Device)))
    {
      Refusal = Refusal.Hidden,
    },
    new(HttpMethod.Get, "/api/v1/devices/{id:guid}", PermissionNames.DeviceRead, (w, _) => Task.FromResult(new Call(HttpMethod.Get, Routes.Device(w.Device))))
    {
      Refusal = Refusal.NotFound,
    },

    // Device groups.
    Get("/api/v1/device-groups", PermissionNames.TenantDeviceGroupsRead, (w, _) => Path(w.InTenant(Routes.DeviceGroups))),
    Send(HttpMethod.Post, "/api/v1/device-groups", PermissionNames.TenantDeviceGroupsWrite, (w, _) => To(w.InTenant(Routes.DeviceGroups), new DeviceGroupRequest(Unique("Group"))))
      with { Logs = AuthorizationChangeActions.DeviceGroupCreated },
    Get("/api/v1/device-groups/{id:guid}", PermissionNames.TenantDeviceGroupsRead, async (w, _) => w.InTenant(Routes.DeviceGroup(await w.DeviceGroupAsync()))),
    Send(HttpMethod.Put, "/api/v1/device-groups/{id:guid}", PermissionNames.TenantDeviceGroupsWrite, async (w, _) => (w.InTenant(Routes.DeviceGroup(await w.DeviceGroupAsync())), new DeviceGroupRequest(Unique("Renamed"))))
      with { Logs = AuthorizationChangeActions.DeviceGroupUpdated },
    Send(HttpMethod.Delete, "/api/v1/device-groups/{id:guid}", PermissionNames.TenantDeviceGroupsWrite, async (w, _) => (w.InTenant(Routes.DeviceGroup(await w.DeviceGroupAsync())), null))
      with { Logs = AuthorizationChangeActions.DeviceGroupDeleted },
    Send(HttpMethod.Post, "/api/v1/device-groups/{id:guid}/members", PermissionNames.DeviceGroupAssignDevices, async (w, _) => (w.InTenant(Routes.DeviceGroupMembers(await w.DeviceGroupAsync())), new DeviceGroupMembersRequest([w.Device])))
      with { Logs = AuthorizationChangeActions.DeviceGroupMembersAdded },
    Send(HttpMethod.Post, "/api/v1/device-groups/{id:guid}/members", PermissionNames.DeviceGroupAssignDevices, async (w, _) => (w.InTenant(Routes.DeviceGroupMembers(await w.DeviceGroupAsync())), new DeviceGroupMembersRequest([w.Device], FromGroupId: await w.DeviceGroupAsync(withDevice: true))))
      with { Logs = AuthorizationChangeActions.DeviceGroupMembersMoved },
    Send(HttpMethod.Delete, "/api/v1/device-groups/{id:guid}/members", PermissionNames.DeviceGroupAssignDevices, async (w, _) => (w.InTenant(Routes.DeviceGroupMembers(await w.DeviceGroupAsync(withDevice: true))), new DeviceGroupMembersRequest([w.Device])))
      with { Logs = AuthorizationChangeActions.DeviceGroupMembersRemoved },

    Get("/api/v1/effective-permissions/{principalId:guid}", PermissionNames.TenantPermissionsRead, async (w, _) => w.InTenant($"{Routes.EffectivePermission((await w.SubjectAsync()).Id)}?principalKind={PermissionPrincipalKind.User}&permission={PermissionNames.DeviceRead}&scopeKind={PermissionScopeKind.Tenant}&scopeId={w.Tenant}")),

    // Installer keys: the caller's own, which is all installer-key.read shows without manage-all.
    Get("/api/v1/installer-keys", PermissionNames.InstallerKeyRead, (w, _) => Path(w.InTenant(Routes.InstallerKeys))),
    Send(HttpMethod.Post, "/api/v1/installer-keys", PermissionNames.InstallerKeyWrite, (w, _) => To(w.InTenant(Routes.InstallerKeys), new CreateInstallerKeyRequest(InstallerKeyType.Persistent, "Matrix")))
      with { Logs = AuthorizationChangeActions.InstallerKeyCreated },
    Send(HttpMethod.Put, "/api/v1/installer-keys/{id:guid}", PermissionNames.InstallerKeyWrite, async (w, c) => (w.InTenant(Routes.InstallerKey(await w.InstallerKeyAsync(c))), new RenameInstallerKeyRequest("Renamed")))
      with { Logs = AuthorizationChangeActions.InstallerKeyRenamed },
    Send(HttpMethod.Delete, "/api/v1/installer-keys/{id:guid}", PermissionNames.InstallerKeyWrite, async (w, c) => (w.InTenant(Routes.InstallerKey(await w.InstallerKeyAsync(c))), null))
      with { Logs = AuthorizationChangeActions.InstallerKeyDeleted },
    Get("/api/v1/installer-keys/{id:guid}/usages", PermissionNames.InstallerKeyRead, async (w, c) => w.InTenant(Routes.InstallerKeyUsages(await w.InstallerKeyAsync(c)))),

    // Invites.
    Get("/api/v1/invites", PermissionNames.TenantUsersRead, (w, _) => Path(w.InTenant(Routes.Invites))),
    Send(HttpMethod.Post, "/api/v1/invites", PermissionNames.TenantUsersWrite, (w, _) => To(w.InTenant(Routes.Invites), new CreateInviteRequest($"invitee-{Guid.NewGuid():N}@example.com"))),
    Send(HttpMethod.Delete, "/api/v1/invites/{id:guid}", PermissionNames.TenantUsersWrite, async (w, _) => (w.InTenant(Routes.Invite(await w.InviteAsync())), null)),

    // Permission assignments, granted to a user with no permissions of their own.
    Get("/api/v1/permission-assignments", PermissionNames.TenantPermissionsRead, async (w, _) => w.InTenant($"{Routes.PermissionAssignments}?principalKind={PermissionPrincipalKind.User}&principalId={(await w.SubjectAsync()).Id}")),
    Send(HttpMethod.Post, "/api/v1/permission-assignments", PermissionNames.TenantPermissionsWrite, async (w, _) => (w.InTenant(Routes.PermissionAssignments), w.GrantRequest((await w.SubjectAsync()).Id)))
      with { Logs = AuthorizationChangeActions.PermissionAssignmentCreated },
    Send(HttpMethod.Put, "/api/v1/permission-assignments/{id:guid}", PermissionNames.TenantPermissionsWrite, async (w, _) => (w.InTenant(Routes.PermissionAssignment(await w.AssignmentAsync())), new UpdatePermissionAssignmentRequest(PermissionNames.TenantUsersRead, PermissionEffect.Allow, PermissionScopeKind.Tenant, w.Tenant, "Changed")))
      with { Logs = AuthorizationChangeActions.PermissionAssignmentUpdated },
    Send(HttpMethod.Delete, "/api/v1/permission-assignments/{id:guid}", PermissionNames.TenantPermissionsWrite, async (w, _) => (w.InTenant(Routes.PermissionAssignment(await w.AssignmentAsync())), null))
      with { Logs = AuthorizationChangeActions.PermissionAssignmentDeleted },
    Send(HttpMethod.Post, "/api/v1/permission-assignments/batch-delete", PermissionNames.TenantPermissionsWrite, async (w, _) => (w.InTenant(Routes.PermissionAssignmentsBatchDelete), new DeletePermissionAssignmentsRequest([await w.AssignmentAsync()])))
      with { Logs = AuthorizationChangeActions.PermissionAssignmentDeleted },
    Get("/api/v1/permission-assignments/catalog", PermissionNames.TenantPermissionsRead, (w, _) => Path(w.InTenant(Routes.PermissionCatalog))),
    Get("/api/v1/permission-assignments/presets", PermissionNames.TenantPermissionsRead, (w, _) => Path(w.InTenant(Routes.PermissionPresets))),
    Send(HttpMethod.Post, "/api/v1/permission-assignments/presets/apply", PermissionNames.TenantPermissionsWrite, async (w, _) => (w.InTenant(Routes.ApplyPermissionPresets), new ApplyPermissionPresetsRequest(PermissionPrincipalKind.User, (await w.SubjectAsync()).Id, [PermissionPresets.SelfService])))
      with { Logs = AuthorizationChangeActions.PermissionAssignmentCreated },

    // Personal access tokens: the caller's own, then another user's.
    Get("/api/v1/personal-access-tokens", PermissionNames.PersonalAccessTokenSelfRead, (_, _) => Path(Routes.PersonalAccessTokens)),
    Send(HttpMethod.Post, "/api/v1/personal-access-tokens", PermissionNames.PersonalAccessTokenSelfWrite, (_, _) => To(Routes.PersonalAccessTokens, new CreatePersonalAccessTokenRequest("Matrix")))
      with { Logs = AuthorizationChangeActions.PersonalAccessTokenCreated },
    Send(HttpMethod.Put, "/api/v1/personal-access-tokens/{id:guid}", PermissionNames.PersonalAccessTokenSelfWrite, async (w, c) => (Routes.PersonalAccessToken(await w.TokenAsync(c.User.Id)), new RenamePersonalAccessTokenRequest("Renamed"))),
    Send(HttpMethod.Delete, "/api/v1/personal-access-tokens/{id:guid}", PermissionNames.PersonalAccessTokenSelfWrite, async (w, c) => (Routes.PersonalAccessToken(await w.TokenAsync(c.User.Id)), null))
      with { Logs = AuthorizationChangeActions.PersonalAccessTokenDeleted },
    Get("/api/v1/users/{userId:guid}/personal-access-tokens", PermissionNames.PersonalAccessTokenOthersRead, async (w, _) => w.InTenant(Routes.UserPersonalAccessTokens((await w.SubjectAsync()).Id))),
    Send(HttpMethod.Post, "/api/v1/users/{userId:guid}/personal-access-tokens", PermissionNames.PersonalAccessTokenOthersWrite, async (w, _) => (w.InTenant(Routes.UserPersonalAccessTokens((await w.SubjectAsync()).Id)), new CreatePersonalAccessTokenRequest("Matrix")))
      with { Logs = AuthorizationChangeActions.PersonalAccessTokenCreated },
    Send(HttpMethod.Put, "/api/v1/users/{userId:guid}/personal-access-tokens/{id:guid}", PermissionNames.PersonalAccessTokenOthersWrite, async (w, _) =>
    {
      var owner = await w.SubjectAsync();
      return (w.InTenant(Routes.UserPersonalAccessToken(owner.Id, await w.TokenAsync(owner.Id))), new RenamePersonalAccessTokenRequest("Renamed"));
    }),
    Send(HttpMethod.Delete, "/api/v1/users/{userId:guid}/personal-access-tokens/{id:guid}", PermissionNames.PersonalAccessTokenOthersWrite, async (w, _) =>
    {
      var owner = await w.SubjectAsync();
      return (w.InTenant(Routes.UserPersonalAccessToken(owner.Id, await w.TokenAsync(owner.Id))), null);
    }) with { Logs = AuthorizationChangeActions.PersonalAccessTokenDeleted },

    // Server settings.
    Send(HttpMethod.Put, "/api/v1/server/alert", PermissionNames.ServerSettingsWrite, (_, _) => To(Routes.ServerAlert, new ServerAlert("Matrix", AlertSeverity.Info, IsDismissible: true, ComesBack: false, IsEnabled: false))),
    Send(HttpMethod.Post, "/api/v1/server/test-email", PermissionNames.ServerSettingsWrite, (_, _) => To(Routes.ServerTestEmail)),

    // Tenant service accounts, then server ones: the same routes under another base and permissions.
    .. ServiceAccountRows(Routes.ServiceAccounts, "/api/v1/service-accounts", PermissionNames.ServiceAccountRead, PermissionNames.ServiceAccountWrite, PermissionNames.ServiceAccountRotateCredentials, inTenant: true),
    .. ServiceAccountRows(Routes.ServerServiceAccounts, "/api/v1/service-accounts/server", PermissionNames.ServerServiceAccountsRead, PermissionNames.ServerServiceAccountsWrite, PermissionNames.ServerServiceAccountsRotateCredentials, inTenant: false),

    // Tenant settings.
    Get("/api/v1/tenant-settings", PermissionNames.TenantSettingsRead, (w, _) => Path(w.InTenant(Routes.TenantSettings))),
    Send(HttpMethod.Put, "/api/v1/tenant-settings", PermissionNames.TenantSettingsWrite, (w, _) => To(w.InTenant(Routes.TenantSettings), new SetTenantSettingsRequest(new Dictionary<string, string?> { [TenantSettingNames.InstanceName] = "matrix" }))),
    Get("/api/v1/tenant-settings/{name}", PermissionNames.TenantSettingsRead, (w, _) => Path(w.InTenant(Routes.TenantSetting(TenantSettingNames.InstanceName)))),
    Send(HttpMethod.Put, "/api/v1/tenant-settings/{name}", PermissionNames.TenantSettingsWrite, (w, _) => To(w.InTenant(Routes.TenantSetting(TenantSettingNames.InstanceName)), new SetTenantSettingRequest("matrix"))),
    Send(HttpMethod.Delete, "/api/v1/tenant-settings/{name}", PermissionNames.TenantSettingsWrite, (w, _) => To(w.InTenant(Routes.TenantSetting(TenantSettingNames.InstanceName)))),

    // Tenants, reached only through server permissions.
    Get("/api/v1/tenants", PermissionNames.ServerTenantsRead, (_, _) => Path(Routes.Tenants)),
    Send(HttpMethod.Post, "/api/v1/tenants", PermissionNames.ServerTenantsWrite, (_, _) => To(Routes.Tenants, new CreateTenantRequest(Unique("Tenant")))),
    Get("/api/v1/tenants/{id:guid}", PermissionNames.ServerTenantsRead, (w, _) => Path(Routes.Tenant(w.OtherTenant))),
    Send(HttpMethod.Put, "/api/v1/tenants/{id:guid}", PermissionNames.ServerTenantsWrite, async (w, _) => (Routes.Tenant(await TestTenants.CreateAsync(w.Server, Unique("Tenant"))), new RenameTenantRequest(Unique("Renamed")))),
    Send(HttpMethod.Delete, "/api/v1/tenants/{id:guid}", PermissionNames.ServerTenantsDelete, async (w, _) => (Routes.Tenant(await TestTenants.CreateAsync(w.Server, Unique("Tenant"))), null)),

    // User groups.
    Get("/api/v1/user-groups", PermissionNames.TenantUserGroupsRead, (w, _) => Path(w.InTenant(Routes.UserGroups))),
    Send(HttpMethod.Post, "/api/v1/user-groups", PermissionNames.TenantUserGroupsWrite, (w, _) => To(w.InTenant(Routes.UserGroups), new UserGroupRequest(Unique("Group"))))
      with { Logs = AuthorizationChangeActions.UserGroupCreated },
    Get("/api/v1/user-groups/{id:guid}", PermissionNames.TenantUserGroupsRead, async (w, _) => w.InTenant(Routes.UserGroup(await w.UserGroupAsync()))),
    Send(HttpMethod.Put, "/api/v1/user-groups/{id:guid}", PermissionNames.TenantUserGroupsWrite, async (w, _) => (w.InTenant(Routes.UserGroup(await w.UserGroupAsync())), new UserGroupRequest(Unique("Renamed"))))
      with { Logs = AuthorizationChangeActions.UserGroupUpdated },
    Send(HttpMethod.Delete, "/api/v1/user-groups/{id:guid}", PermissionNames.TenantUserGroupsWrite, async (w, _) => (w.InTenant(Routes.UserGroup(await w.UserGroupAsync())), null))
      with { Logs = AuthorizationChangeActions.UserGroupDeleted },
    Send(HttpMethod.Post, "/api/v1/user-groups/{id:guid}/members", PermissionNames.UserGroupAssignUsers, async (w, _) => (w.InTenant(Routes.UserGroupMembers(await w.UserGroupAsync())), new UserGroupMembersRequest([(await w.SubjectAsync()).Id])))
      with { Logs = AuthorizationChangeActions.UserGroupMembersAdded },
    Send(HttpMethod.Delete, "/api/v1/user-groups/{id:guid}/members", PermissionNames.UserGroupAssignUsers, async (w, _) =>
    {
      var member = (await w.SubjectAsync()).Id;
      return (w.InTenant(Routes.UserGroupMembers(await w.UserGroupAsync(member))), new UserGroupMembersRequest([member]));
    }) with { Logs = AuthorizationChangeActions.UserGroupMembersRemoved },

    // Users.
    Get("/api/v1/users", PermissionNames.TenantUsersRead, (w, _) => Path(w.InTenant(Routes.Users))),
    Send(HttpMethod.Post, "/api/v1/users", PermissionNames.TenantUsersWrite, (w, _) =>
    {
      var email = $"created-{Guid.NewGuid():N}@example.com";
      return To(w.InTenant(Routes.Users), new CreateUserRequest(email, email, TestAccounts.Password, [PermissionPresets.SelfService]));
    }) with
    {
      // Giving a preset grants what it holds, which takes managing the tenant's permissions.
      Needs = [PermissionNames.TenantPermissionsWrite],
      Logs = AuthorizationChangeActions.PermissionAssignmentsSeeded,
      LoggedBySystem = true,
    },
    Send(HttpMethod.Delete, "/api/v1/users/{id:guid}", PermissionNames.TenantUsersDelete, async (w, _) => (w.InTenant(Routes.User((await w.SubjectAsync()).Id)), null)),
    Send(HttpMethod.Post, "/api/v1/users/{id:guid}/reset-password", PermissionNames.TenantUsersWrite, async (w, _) => (w.InTenant(Routes.UserPasswordReset((await w.SubjectAsync()).Id)), null)),
  ];

  private static IEnumerable<Row> ServiceAccountRows(string accounts, string route, string read, string write, string rotate, bool inTenant)
  {
    string At(World w, string path) => inTenant ? w.InTenant(path) : path;

    yield return Get(route, read, (w, _) => Path(At(w, accounts)));
    yield return Send(HttpMethod.Post, route, write, (w, _) => To(At(w, accounts), new CreateServiceAccountRequest(Unique("Account"))))
      with { Logs = AuthorizationChangeActions.ServiceAccountCreated };
    yield return Get($"{route}/{{id:guid}}", read, async (w, _) => At(w, Routes.ServiceAccount(accounts, await w.ServiceAccountAsync(At(w, accounts)))));
    yield return Send(HttpMethod.Put, $"{route}/{{id:guid}}", write, async (w, _) => (At(w, Routes.ServiceAccount(accounts, await w.ServiceAccountAsync(At(w, accounts)))), new UpdateServiceAccountRequest(Unique("Renamed"), null, IsEnabled: true)))
      with { Logs = AuthorizationChangeActions.ServiceAccountUpdated };
    yield return Send(HttpMethod.Delete, $"{route}/{{id:guid}}", write, async (w, _) => (At(w, Routes.ServiceAccount(accounts, await w.ServiceAccountAsync(At(w, accounts)))), null))
      with { Logs = AuthorizationChangeActions.ServiceAccountDeleted };
    yield return Send(HttpMethod.Post, $"{route}/{{id:guid}}/credentials", rotate, async (w, _) => (At(w, Routes.ServiceAccountCredentials(accounts, await w.ServiceAccountAsync(At(w, accounts)))), new CreateServiceAccountCredentialRequest("Key", null)))
      with { Logs = AuthorizationChangeActions.ServiceAccountCredentialCreated };
    yield return Send(HttpMethod.Delete, $"{route}/{{id:guid}}/credentials/{{credentialId:guid}}", rotate, async (w, _) =>
    {
      var account = await w.ServiceAccountAsync(At(w, accounts));
      var credential = await w.CredentialAsync(At(w, Routes.ServiceAccountCredentials(accounts, account)), revoke: null);
      return (At(w, Routes.ServiceAccountCredential(accounts, account, credential)), null);
    }) with { Logs = AuthorizationChangeActions.ServiceAccountCredentialRevoked };
    yield return Send(HttpMethod.Delete, $"{route}/{{id:guid}}/credentials/{{credentialId:guid}}/purge", rotate, async (w, _) =>
    {
      var account = await w.ServiceAccountAsync(At(w, accounts));
      var credential = await w.CredentialAsync(At(w, Routes.ServiceAccountCredentials(accounts, account)), revoke: id => At(w, Routes.ServiceAccountCredential(accounts, account, id)));
      return (At(w, Routes.ServiceAccountCredentialPurge(accounts, account, credential)), null);
    }) with { Logs = AuthorizationChangeActions.ServiceAccountCredentialDeleted };
  }

  /// <summary>
  /// A tenant with a device, an administrator holding every permission who makes the targets the calls act on, the
  /// way a user would, and another tenant whose administrator holds every tenant permission there. Remembers the id
  /// of everything made in the first tenant, to spot it where it must not be.
  /// </summary>
  private sealed class World
  {
    private readonly HashSet<string> _ours = [];
    private HttpClient _admin = null!;

    private World(ServerHost server) => Server = server;

    public ServerHost Server { get; }

    public Guid Tenant { get; private set; }

    public Guid OtherTenant { get; private set; }

    public Guid Device { get; private set; }

    public Caller Outsider { get; private set; } = null!;

    public static async Task<World> CreateAsync(ServerHost server)
    {
      var world = new World(server)
      {
        Tenant = await TestTenants.CreateAsync(server),
        OtherTenant = await TestTenants.CreateAsync(server, "Other tenant"),
      };
      world.Device = await server.WithDbAsync(async db =>
      {
        var device = new DeviceRecord { Id = Guid.NewGuid(), TenantId = world.Tenant, Name = "MATRIX-PC" };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device.Id;
      });
      world.Remember(world.Device);
      world._admin = (await world.CallerAsync([.. PermissionCatalog.All.Select(x => x.Name)])).Client;
      world.Outsider = await world.CallerAsync([.. PermissionCatalog.All.Where(x => x.AllowsTenantScope).Select(x => x.Name)], world.OtherTenant);
      return world;
    }

    /// <summary>Whether this text names anything made in the first tenant.</summary>
    /// <param name="except">Text whose ids don't count, e.g. a request's, which an answer may repeat.</param>
    public bool Names(string text, string except = "") =>
      _ours.Any(id => text.Contains(id, StringComparison.OrdinalIgnoreCase) && !except.Contains(id, StringComparison.OrdinalIgnoreCase));

    public bool Names(Call call) => Names(call.Text);

    public string InTenant(string path) => $"{path}{(path.Contains('?') ? '&' : '?')}tenantId={Tenant}";

    /// <summary>A new user of the tenant (the first one unless given) holding these permissions, signed in.</summary>
    public async Task<Caller> CallerAsync(IEnumerable<string> permissions, Guid? tenant = null)
    {
      var email = $"caller-{Guid.NewGuid():N}@example.com";
      var user = await TestAccounts.CreateUserAsync(Server, email, tenant ?? Tenant, confirmed: true);
      if (tenant is null)
      {
        Remember(user.Id);
      }

      foreach (var permission in permissions)
      {
        await GrantAsync(user, permission);
      }

      var cookie = await TestAccounts.SignInForCookieAsync(Server, email);
      var client = TestAccounts.Client(Server);
      client.DefaultRequestHeaders.Add("Cookie", cookie);
      return new Caller(user, client, cookie);
    }

    /// <summary>At tenant scope, or at server scope for a permission that has only that one.</summary>
    public Task GrantAsync(UserRecord user, string permission) =>
      PermissionCatalog.Find(permission)!.AllowsTenantScope
        ? TestPermissions.AssignAsync(Server, user, permission, PermissionScopeKind.Tenant, user.TenantId)
        : TestPermissions.AssignAsync(Server, user, permission, PermissionScopeKind.Server);

    /// <summary>A user of the tenant with no permissions, for calls that act on someone.</summary>
    public async Task<UserRecord> SubjectAsync()
    {
      var subject = await TestAccounts.CreateUserAsync(Server, $"subject-{Guid.NewGuid():N}@example.com", Tenant);
      Remember(subject.Id);
      return subject;
    }

    public Task<int> LogCountAsync() => Server.WithDbAsync(db => db.AuthorizationChanges.CountAsync());

    public Task<int> LogCountAsync(string action, Guid? actor) =>
      Server.WithDbAsync(db => db.AuthorizationChanges.CountAsync(x => x.Action == action && x.ActorId == actor));

    public CreatePermissionAssignmentRequest GrantRequest(Guid principal) =>
      new(PermissionPrincipalKind.User, principal, PermissionNames.TenantUsersRead, PermissionEffect.Allow, PermissionScopeKind.Tenant, Tenant);

    public async Task<Guid> DeviceGroupAsync(bool withDevice = false)
    {
      var group = await CreateAsync(InTenant(Routes.DeviceGroups), new DeviceGroupRequest(Unique("Group")));
      if (withDevice)
      {
        await CreateAsync(InTenant(Routes.DeviceGroupMembers(group)), new DeviceGroupMembersRequest([Device]), readId: false);
      }

      return group;
    }

    public async Task<Guid> UserGroupAsync(Guid? member = null)
    {
      var group = await CreateAsync(InTenant(Routes.UserGroups), new UserGroupRequest(Unique("Group")));
      if (member is { } id)
      {
        await CreateAsync(InTenant(Routes.UserGroupMembers(group)), new UserGroupMembersRequest([id]), readId: false);
      }

      return group;
    }

    public async Task<Guid> InstallerKeyAsync(Caller owner) =>
      Remember(TestInstallerKeys.IdOf(await TestInstallerKeys.CreateAsync(Server, Tenant, creatorId: owner.User.Id)));

    public Task<Guid> InviteAsync() => CreateAsync(InTenant(Routes.Invites), new CreateInviteRequest($"invitee-{Guid.NewGuid():N}@example.com"));

    public async Task<Guid> AssignmentAsync() => await CreateAsync(InTenant(Routes.PermissionAssignments), GrantRequest((await SubjectAsync()).Id));

    public Task<Guid> TokenAsync(Guid owner) => CreateAsync(InTenant(Routes.UserPersonalAccessTokens(owner)), new CreatePersonalAccessTokenRequest("Matrix"));

    public Task<Guid> ServiceAccountAsync(string accounts) => CreateAsync(accounts, new CreateServiceAccountRequest(Unique("Account")));

    /// <param name="revoke">The route that revokes a credential, to revoke it before it is used; null keeps it active.</param>
    public async Task<Guid> CredentialAsync(string credentials, Func<Guid, string>? revoke)
    {
      var credential = await CreateAsync(credentials, new CreateServiceAccountCredentialRequest("Key", null));
      if (revoke is not null)
      {
        using var response = await _admin.DeleteAsync(revoke(credential), Cancel);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Cancel));
      }

      return credential;
    }

    // Creates through the API as the administrator and answers the new item's id, wherever the response puts it.
    private async Task<Guid> CreateAsync(string path, object body, bool readId = true)
    {
      using var response = await _admin.PostAsync(path, JsonContent.Create(body, body.GetType()), Cancel);
      var text = await response.Content.ReadAsStringAsync(Cancel);
      Assert.True(response.IsSuccessStatusCode, $"setup POST {path}: {(int)response.StatusCode} {text}");
      return readId ? Remember(IdOf(JsonDocument.Parse(text).RootElement)) : Guid.Empty;
    }

    private Guid Remember(Guid id)
    {
      _ours.Add(id.ToString());
      return id;
    }

    private static Guid IdOf(JsonElement created) =>
      created.TryGetProperty("id", out var id)
        ? id.GetGuid()
        : created.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Object).Select(x => x.Value.GetProperty("id").GetGuid()).First();
  }
}
