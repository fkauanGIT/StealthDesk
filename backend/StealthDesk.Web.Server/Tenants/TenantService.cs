using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServiceAccounts;
using StealthDesk.Contracts.Tenants;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Tenants;

/// <summary>The server's tenants, for its administrators: listed, created, renamed and deleted with all they hold.</summary>
public sealed class TenantService(StealthDeskDb db, TimeProvider clock)
{
  public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken) =>
    await db.Tenants.AsNoTracking()
      .OrderBy(x => x.Name)
      .Select(x => new TenantSummary(
        x.Id,
        x.Name,
        x.CreatedAt,
        db.Users.IgnoreQueryFilters().Count(u => u.TenantId == x.Id),
        db.Devices.IgnoreQueryFilters().Count(d => d.TenantId == x.Id)))
      .ToListAsync(cancellationToken);

  public async Task<IResult> GetAsync(Guid id, CancellationToken cancellationToken) =>
    (await ListAsync(cancellationToken)).FirstOrDefault(x => x.Id == id) is { } tenant
      ? Results.Ok(tenant)
      : Results.NotFound();

  public async Task<IResult> CreateAsync(string? name, CancellationToken cancellationToken)
  {
    if (NameRefusal(name) is { } invalid)
    {
      return invalid;
    }

    var tenant = new TenantRecord { Id = Guid.NewGuid(), Name = name!.Trim(), CreatedAt = clock.GetUtcNow() };
    db.Tenants.Add(tenant);
    await db.SaveChangesAsync(cancellationToken);
    return Results.Created(Routes.Tenant(tenant.Id), new TenantSummary(tenant.Id, tenant.Name, tenant.CreatedAt, 0, 0));
  }

  public async Task<IResult> RenameAsync(Guid id, string? name, CancellationToken cancellationToken)
  {
    if (NameRefusal(name) is { } invalid)
    {
      return invalid;
    }

    if (await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } tenant)
    {
      return Results.NotFound();
    }

    tenant.Name = name!.Trim();
    await db.SaveChangesAsync(cancellationToken);
    return await GetAsync(id, cancellationToken);
  }

  /// <summary>
  /// Deletes the tenant with everything in it: users, devices, groups, keys, settings, service accounts, its
  /// permissions (also those its members held server-wide) and its authorization log.
  /// </summary>
  public async Task<IResult> DeleteAsync(Principal actor, Guid id, CancellationToken cancellationToken)
  {
    // Deleting the tenant one is signed in to would delete oneself, and could leave the server without an administrator.
    if (actor.TenantId == id)
    {
      return Results.Problem("You can't delete the tenant you're signed in to.", statusCode: StatusCodes.Status409Conflict);
    }

    if (await db.Tenants.FirstOrDefaultAsync(x => x.Id == id, cancellationToken) is not { } tenant)
    {
      return Results.NotFound();
    }

    var users = await db.Users.IgnoreQueryFilters().Where(x => x.TenantId == id).Select(x => x.Id).ToListAsync(cancellationToken);
    var groups = await db.UserGroups.IgnoreQueryFilters().Where(x => x.TenantId == id).Select(x => x.Id).ToListAsync(cancellationToken);
    var accounts = await db.ServiceAccounts.Where(x => x.Kind == ServiceAccountKind.Tenant && x.TenantId == id).Select(x => x.Id).ToListAsync(cancellationToken);
    var tokens = await db.PersonalAccessTokens.Where(x => users.Contains(x.UserId)).Select(x => x.Id).ToListAsync(cancellationToken);

    db.PermissionAssignments.RemoveRange(await db.PermissionAssignments.IgnoreQueryFilters()
      .Where(x => x.OwningTenantId == id
        || (x.PrincipalKind == PermissionPrincipalKind.User && users.Contains(x.PrincipalId))
        || (x.PrincipalKind == PermissionPrincipalKind.UserGroup && groups.Contains(x.PrincipalId))
        || (x.PrincipalKind == PermissionPrincipalKind.ServiceAccount && accounts.Contains(x.PrincipalId))
        || (x.PrincipalKind == PermissionPrincipalKind.PersonalAccessToken && tokens.Contains(x.PrincipalId)))
      .ToListAsync(cancellationToken));
    db.AuthorizationChanges.RemoveRange(await db.AuthorizationChanges.Where(x => x.OwningTenantId == id).ToListAsync(cancellationToken));

    // PostgreSQL removes the rest by cascade; the in-memory database has no cascades for rows it isn't tracking.
    if (!db.Database.IsRelational())
    {
      db.Users.RemoveRange(await db.Users.IgnoreQueryFilters().Where(x => x.TenantId == id).ToListAsync(cancellationToken));
      db.Devices.RemoveRange(await db.Devices.IgnoreQueryFilters().Where(x => x.TenantId == id).ToListAsync(cancellationToken));
      db.UserGroups.RemoveRange(await db.UserGroups.IgnoreQueryFilters().Include(x => x.Members).Where(x => x.TenantId == id).ToListAsync(cancellationToken));
      db.DeviceGroups.RemoveRange(await db.DeviceGroups.IgnoreQueryFilters().Include(x => x.Members).Where(x => x.TenantId == id).ToListAsync(cancellationToken));
      db.TenantInvites.RemoveRange(await db.TenantInvites.IgnoreQueryFilters().Where(x => x.TenantId == id).ToListAsync(cancellationToken));
      db.InstallerKeys.RemoveRange(await db.InstallerKeys.IgnoreQueryFilters().Include(x => x.Usages).Where(x => x.TenantId == id).ToListAsync(cancellationToken));
      db.TenantSettings.RemoveRange(await db.TenantSettings.IgnoreQueryFilters().Where(x => x.TenantId == id).ToListAsync(cancellationToken));
      db.ServiceAccounts.RemoveRange(await db.ServiceAccounts.Include(x => x.Credentials).Where(x => x.TenantId == id).ToListAsync(cancellationToken));
      db.PersonalAccessTokens.RemoveRange(await db.PersonalAccessTokens.Where(x => users.Contains(x.UserId)).ToListAsync(cancellationToken));
    }

    db.Tenants.Remove(tenant);
    await db.SaveChangesAsync(cancellationToken);
    return Results.NoContent();
  }

  private static IResult? NameRefusal(string? name) =>
    string.IsNullOrWhiteSpace(name) || name.Trim().Length > CreateTenantRequest.NameMax
      ? Results.Problem($"Enter a name of at most {CreateTenantRequest.NameMax} characters.", statusCode: StatusCodes.Status400BadRequest)
      : null;
}
