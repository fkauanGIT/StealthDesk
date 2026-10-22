using StealthDesk.Contracts.TenantSettings;

namespace StealthDesk.Web.Server.Persistence;

/// <summary>A tenant's own value for one setting; a setting without a row uses the server's default.</summary>
public class TenantSettingRecord
{
  public const int NameMax = 50;
  public const int ValueMax = TenantSettingNames.ValueMax;

  public Guid Id { get; set; }

  public Guid TenantId { get; set; }
  public TenantRecord? Tenant { get; set; }

  public string Name { get; set; } = string.Empty;
  public string Value { get; set; } = string.Empty;
}
