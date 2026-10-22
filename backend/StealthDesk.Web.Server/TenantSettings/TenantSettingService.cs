using StealthDesk.Contracts.Agents;
using StealthDesk.Contracts.TenantSettings;

namespace StealthDesk.Web.Server.TenantSettings;

/// <summary>
/// A tenant's settings: its own values, and the server's defaults for those it hasn't set. Only known settings are
/// kept, each checked and stored the one way it's read back.
/// </summary>
public sealed class TenantSettingService(StealthDeskDb db, IOptionsMonitor<TenantDefaultsOptions> defaults, AgentDownloads.AgentDownloadCatalog downloads)
{
  public async Task<IResult> ListAsync(Guid tenant, CancellationToken cancellationToken)
  {
    if (!await TenantExistsAsync(tenant, cancellationToken))
    {
      return Results.NotFound();
    }

    var own = await OwnAsync(tenant, cancellationToken);
    return Results.Ok(new TenantSettingList { Items = [.. TenantSettingNames.All.Select(x => ToContract(x, own))] });
  }

  public async Task<IResult> GetAsync(Guid tenant, string name, CancellationToken cancellationToken)
  {
    if (!TenantSettingRules.Exists(name) || !await TenantExistsAsync(tenant, cancellationToken))
    {
      return Results.NotFound();
    }

    return Results.Ok(ToContract(name, await OwnAsync(tenant, cancellationToken)));
  }

  /// <summary>Sets one value; an empty one goes back to the server's default.</summary>
  public Task<IResult> SetAsync(Guid tenant, string name, string? value, CancellationToken cancellationToken) =>
    SetManyAsync(tenant, new Dictionary<string, string?> { [name] = value }, single: name, cancellationToken);

  /// <summary>Sets several values, every one checked before any is saved.</summary>
  public Task<IResult> SetManyAsync(Guid tenant, IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken) =>
    SetManyAsync(tenant, values, single: null, cancellationToken);

  public Task<IResult> DeleteAsync(Guid tenant, string name, CancellationToken cancellationToken) =>
    SetAsync(tenant, name, null, cancellationToken);

  /// <summary>The instance install commands name for the tenant, if any, made from <paramref name="serverHost"/> when the tenant names none.</summary>
  public async Task<IResult> DeploymentOptionsAsync(Guid tenant, string serverHost, CancellationToken cancellationToken)
  {
    if (!await TenantExistsAsync(tenant, cancellationToken))
    {
      return Results.NotFound();
    }

    var own = await OwnAsync(tenant, cancellationToken);
    var append = Effective(TenantSettingNames.AppendInstanceName, own) == "true";
    var name = Effective(TenantSettingNames.InstanceName, own) ?? InstanceNames.FromHost(serverHost);
    return Results.Ok(new DeploymentOptions(append ? name : null, downloads.WindowsAgent()));
  }

  private async Task<IResult> SetManyAsync(Guid tenant, IReadOnlyDictionary<string, string?> values, string? single, CancellationToken cancellationToken)
  {
    if (values.Keys.FirstOrDefault(x => !TenantSettingRules.Exists(x)) is { } unknown)
    {
      // An unknown name in the route is a missing resource; in a body it's a mistake in the request.
      return single is not null ? Results.NotFound() : Invalid($"There is no setting named '{unknown}'.");
    }

    var normalized = new Dictionary<string, string?>();
    foreach (var (name, value) in values)
    {
      if (string.IsNullOrWhiteSpace(value))
      {
        normalized[name] = null;
        continue;
      }

      var result = TenantSettingRules.Normalize(name, value);
      if (!result.Succeeded)
      {
        return Invalid(result.Error!);
      }

      normalized[name] = result.Value;
    }

    if (!await TenantExistsAsync(tenant, cancellationToken))
    {
      return Results.NotFound();
    }

    var rows = await db.TenantSettings.IgnoreQueryFilters().Where(x => x.TenantId == tenant).ToListAsync(cancellationToken);
    foreach (var (name, value) in normalized)
    {
      var row = rows.FirstOrDefault(x => x.Name == name);
      if (value is null)
      {
        if (row is not null)
        {
          db.TenantSettings.Remove(row);
        }
      }
      else if (row is null)
      {
        db.TenantSettings.Add(new TenantSettingRecord { Id = Guid.NewGuid(), TenantId = tenant, Name = name, Value = value });
      }
      else
      {
        row.Value = value;
      }
    }

    try
    {
      await db.SaveChangesAsync(cancellationToken);
    }
    catch (DbUpdateException)
    {
      // Someone saved the same setting at the same moment; theirs stays, and this one can be tried again.
      return Results.Problem("The setting changed while saving. Try again.", statusCode: StatusCodes.Status409Conflict);
    }

    var own = await OwnAsync(tenant, cancellationToken);
    return single is not null
      ? Results.Ok(ToContract(single, own))
      : Results.Ok(new TenantSettingList { Items = [.. TenantSettingNames.All.Select(x => ToContract(x, own))] });
  }

  private Task<Dictionary<string, string>> OwnAsync(Guid tenant, CancellationToken cancellationToken) =>
    db.TenantSettings.IgnoreQueryFilters().AsNoTracking()
      .Where(x => x.TenantId == tenant)
      .ToDictionaryAsync(x => x.Name, x => x.Value, cancellationToken);

  private Task<bool> TenantExistsAsync(Guid tenant, CancellationToken cancellationToken) =>
    db.Tenants.AnyAsync(x => x.Id == tenant, cancellationToken);

  private string? Effective(string name, IReadOnlyDictionary<string, string> own) =>
    own.TryGetValue(name, out var value) ? value : TenantSettingRules.DefaultOf(name, defaults.CurrentValue);

  private TenantSetting ToContract(string name, IReadOnlyDictionary<string, string> own) =>
    new(name, own.GetValueOrDefault(name), TenantSettingRules.DefaultOf(name, defaults.CurrentValue));

  private static IResult Invalid(string reason) => Results.Problem(reason, statusCode: StatusCodes.Status400BadRequest);
}
