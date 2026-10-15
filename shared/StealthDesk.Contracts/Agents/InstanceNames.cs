using System.Text.RegularExpressions;

namespace StealthDesk.Contracts.Agents;

/// <summary>
/// Rules for an agent's instance name (<c>--instance</c>), which becomes a folder name. Shared so the server checks a
/// name the same way before putting it in an install command.
/// </summary>
public static partial class InstanceNames
{
  /// <summary>The instance an agent runs as when none is given.</summary>
  public const string Default = "default";

  public const int MaxLength = 40;

  /// <summary>Null when <paramref name="name"/> is acceptable (or absent); otherwise why it isn't.</summary>
  public static string? Validate(string? name)
  {
    if (string.IsNullOrWhiteSpace(name))
    {
      return null;
    }

    var trimmed = name.Trim();
    if (trimmed.Equals(Default, StringComparison.OrdinalIgnoreCase))
    {
      return $"'{Default}' is used when no instance is given; pick another name.";
    }

    if (trimmed.Length > MaxLength || !AllowedName().IsMatch(trimmed))
    {
      return $"Instance names use 1 to {MaxLength} letters, digits, '-' or '_', starting with a letter or digit.";
    }

    return null;
  }

  /// <summary>
  /// A valid instance name made from a server's host, e.g. <c>desk.example.com</c> becomes <c>desk-example-com</c>;
  /// null when nothing usable is left.
  /// </summary>
  public static string? FromHost(string host)
  {
    var name = Disallowed().Replace(host.Trim(), "-").Trim('-', '_');
    if (name.Length > MaxLength)
    {
      name = name[..MaxLength].TrimEnd('-', '_');
    }

    return Validate(name) is null && name.Length > 0 ? name : null;
  }

  [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9_-]*$")]
  private static partial Regex AllowedName();

  [GeneratedRegex("[^A-Za-z0-9_-]+")]
  private static partial Regex Disallowed();
}
