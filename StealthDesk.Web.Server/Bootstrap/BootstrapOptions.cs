using System.Net.Mail;

namespace StealthDesk.Web.Server.Bootstrap;

/// <summary>
/// What a new server is given on its first start, so it can be set up without anyone opening the browser: the first
/// administrator, with a personal access token for scripts, and a server service account with its first credential.
/// Each part is optional, and each needs all its settings or none.
/// </summary>
public sealed class BootstrapOptions
{
  public const string Section = "Bootstrap";
  public const int MinimumSecretLength = 32;

  /// <summary>The first administrator's email, also their user name. Created only while the server has no users.</summary>
  public string? AdminEmail { get; set; }

  /// <summary>Must meet the password rules.</summary>
  public string? AdminPassword { get; set; }

  /// <summary>The administrator's token id: the part of the token before the colon.</summary>
  public Guid? AdminTokenId { get; set; }

  /// <summary>The token's secret: the part after the colon, at least <see cref="MinimumSecretLength"/> characters.</summary>
  public string? AdminTokenSecret { get; set; }

  public string? ServerServiceAccountName { get; set; }

  public string? ServerServiceAccountDescription { get; set; }

  /// <summary>A fixed id for the account, so automation can know it without asking; a new one when unset.</summary>
  public Guid? ServerServiceAccountId { get; set; }

  /// <summary>The credential's id: the part of the key before the colon.</summary>
  public Guid? ServerServiceAccountCredentialId { get; set; }

  /// <summary>The credential's secret: the part after the colon, at least <see cref="MinimumSecretLength"/> characters.</summary>
  public string? ServerServiceAccountCredentialSecret { get; set; }

  public bool HasAdmin => !string.IsNullOrWhiteSpace(AdminEmail);

  public bool HasAdminToken => AdminTokenId is not null;

  public bool HasServerServiceAccount => !string.IsNullOrWhiteSpace(ServerServiceAccountName);

  public static OptionsBuilder<BootstrapOptions> AddTo(IServiceCollection services, IConfiguration configuration) =>
    services.AddOptions<BootstrapOptions>()
      .Bind(configuration.GetSection(Section))
      .Validate(x => Together(x.AdminEmail, x.AdminPassword), $"Set {Key(nameof(AdminEmail))} and {Key(nameof(AdminPassword))} together, or neither.")
      .Validate(x => string.IsNullOrWhiteSpace(x.AdminEmail) || MailAddress.TryCreate(x.AdminEmail, out _), $"{Key(nameof(AdminEmail))} isn't an email address.")
      .Validate(x => Together(x.AdminTokenId, x.AdminTokenSecret), $"Set {Key(nameof(AdminTokenId))} and {Key(nameof(AdminTokenSecret))} together, or neither.")
      .Validate(x => !x.HasAdminToken || x.HasAdmin, $"{Key(nameof(AdminTokenId))} belongs to the bootstrap administrator: set {Key(nameof(AdminEmail))} too.")
      .Validate(x => LongEnough(x.AdminTokenSecret), $"{Key(nameof(AdminTokenSecret))} must be at least {MinimumSecretLength} characters.")
      .Validate(
        x => Together(x.ServerServiceAccountName, x.ServerServiceAccountCredentialId, x.ServerServiceAccountCredentialSecret),
        $"Set {Key(nameof(ServerServiceAccountName))}, {Key(nameof(ServerServiceAccountCredentialId))} and {Key(nameof(ServerServiceAccountCredentialSecret))} together, or none of them.")
      .Validate(x => LongEnough(x.ServerServiceAccountCredentialSecret), $"{Key(nameof(ServerServiceAccountCredentialSecret))} must be at least {MinimumSecretLength} characters.")
      .ValidateOnStart();

  private static string Key(string name) => $"{Section}:{name}";

  private static bool Together(params object?[] values)
  {
    var set = values.Count(x => x is not null && (x is not string text || !string.IsNullOrWhiteSpace(text)));
    return set == 0 || set == values.Length;
  }

  private static bool LongEnough(string? secret) => string.IsNullOrEmpty(secret) || secret.Length >= MinimumSecretLength;
}
