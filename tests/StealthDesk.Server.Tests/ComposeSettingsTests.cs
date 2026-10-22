using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Configuration;
using StealthDesk.Web.Server.Persistence;

namespace StealthDesk.Server.Tests;

/// <summary>
/// The Docker Compose files name settings by hand, where a typo is silently ignored. Every setting in .env.example
/// and every secret must be one the server reads, every setting the server has must be listed, each commented-out
/// value must be the real default, and every password or secret must be available as a Docker secret.
/// </summary>
public partial class ComposeSettingsTests
{
  private const string Prefix = "StealthDesk_";

  /// <summary>Read outside an options section.</summary>
  private static readonly string[] RootSettings =
  [
    "POSTGRES_HOST", "POSTGRES_PORT", "POSTGRES_USER", "POSTGRES_PASSWORD", "POSTGRES_DB", DatabaseSetup.ApplyMigrationsOnStart,
    ConfigurationSources.EnableDockerSecrets, "OTEL_EXPORTER_OTLP_ENDPOINT",
  ];

  /// <summary>Examples of what to write, not defaults.</summary>
  private static readonly string[] Examples =
  [
    "KeyProtection:EncryptKeys", "KeyProtection:Certificate:Path", "KeyProtection:PreviousCertificates:0:Path",
    "Bootstrap:AdminEmail",
  ];

  /// <summary>A rotation adds these as secrets: an empty one would be a previous certificate without contents.</summary>
  private static readonly string[] NotSecretsByDefault =
    ["KeyProtection:PreviousCertificates:0:Password", "KeyProtection:PreviousCertificates:0:Base64"];

  private static readonly string Folder = Path.Combine(Repository.Root, "docker-compose");

  [Fact]
  public void EverySettingInTheExample_IsOneTheServerReads()
  {
    var unknown = ExampleSettings().Select(x => x.Key).Where(x => !IsServerSetting(x)).ToList();

    Assert.Empty(unknown);
  }

  [Fact]
  public void EverySettingTheServerHas_IsInTheExample()
  {
    var listed = ExampleSettings().Select(x => x.Key).Concat(SetByCompose()).ToHashSet(StringComparer.Ordinal);

    var missing = Sections().SelectMany(x => Leaves(x.Value, x.Key)).Where(x => !listed.Contains(x)).ToList();

    Assert.Empty(missing);
  }

  [Fact]
  public void EachCommentedOutValue_IsTheDefault()
  {
    var defaults = new ConfigurationBuilder()
      .AddJsonFile(Path.Combine(Repository.Root, "StealthDesk.Web.Server", "appsettings.json"))
      .Build();

    var wrong = ExampleSettings()
      // An unknown key is reported by the test above.
      .Where(x => x.CommentedOut && !Examples.Contains(x.Key) && x.Key.Contains(':', StringComparison.Ordinal) && IsServerSetting(x.Key))
      .Select(x => (x.Key, x.Value, Default: DefaultOf(defaults, x.Key)))
      .Where(x => !string.Equals(x.Value, x.Default, StringComparison.OrdinalIgnoreCase))
      .Select(x => $"{x.Key}: '{x.Value}' in the example, '{x.Default}' by default")
      .ToList();

    Assert.Empty(wrong);
  }

  [Fact]
  public void EveryComposeVariableInTheExample_IsUsedByTheComposeFile()
  {
    var compose = File.ReadAllText(Path.Combine(Folder, "docker-compose.yml"));

    var unused = ExampleLines()
      .Where(x => !x.Name.StartsWith(Prefix, StringComparison.Ordinal))
      .Select(x => x.Name)
      .Where(x => !compose.Contains("${" + x, StringComparison.Ordinal))
      .ToList();

    Assert.Empty(unused);
  }

  [Fact]
  public void EverySettingTheComposeFilesSet_IsOneTheServerReads()
  {
    var unknown = SetByCompose().Where(x => !IsServerSetting(x)).ToList();

    Assert.Empty(unknown);
  }

  [Fact]
  public void EverySecretTheServerGets_IsOneItReads()
  {
    var unknown = ServerSecrets().Select(ToKey).Where(x => !IsServerSetting(x)).ToList();

    Assert.Empty(unknown);
  }

  [Fact]
  public void EveryDeclaredSecret_HasAnExampleFile_AndTheOtherWayAround()
  {
    var declared = SecretFiles().Order().ToList();
    var examples = Directory.GetFiles(Path.Combine(Folder, "secrets.example")).Select(x => Path.GetFileName(x)).Order().ToList();

    Assert.Equal(declared, examples);
  }

  [Fact]
  public void EveryPasswordAndSecret_CanComeFromADockerSecret()
  {
    var secrets = ServerSecrets().Select(ToKey).ToHashSet(StringComparer.Ordinal);

    var missing = ExampleSettings()
      .Select(x => x.Key)
      .Where(x => SensitiveName().IsMatch(x) && !NotSecretsByDefault.Contains(x) && !secrets.Contains(x))
      .ToList();

    Assert.Empty(missing);
  }

  private sealed record ExampleLine(string Name, string Value, bool CommentedOut);

  private sealed record ExampleSetting(string Key, string Value, bool CommentedOut);

  private static IEnumerable<ExampleLine> ExampleLines() =>
    File.ReadLines(Path.Combine(Folder, ".env.example"))
      .Select(x => ExampleLinePattern().Match(x))
      .Where(x => x.Success)
      .Select(x => new ExampleLine(x.Groups["name"].Value, x.Groups["value"].Value, x.Groups["comment"].Success));

  private static IEnumerable<ExampleSetting> ExampleSettings() =>
    ExampleLines()
      .Where(x => x.Name.StartsWith(Prefix, StringComparison.Ordinal))
      .Select(x => new ExampleSetting(ToKey(x.Name[Prefix.Length..]), x.Value, x.CommentedOut));

  /// <summary>Settings the compose files give the server themselves, in either file.</summary>
  private static IEnumerable<string> SetByCompose() =>
    new[] { "docker-compose.yml", "docker-compose.secrets.yml" }
      .SelectMany(x => ComposeSetting().Matches(File.ReadAllText(Path.Combine(Folder, x))))
      .Select(x => ToKey(x.Groups["name"].Value))
      .Distinct();

  private static string ToKey(string name) => name.Replace("__", ":", StringComparison.Ordinal);

  /// <summary>The secrets the server service mounts, which it reads as settings.</summary>
  private static IEnumerable<string> ServerSecrets()
  {
    var compose = File.ReadAllText(Path.Combine(Folder, "docker-compose.secrets.yml"));
    var server = compose[compose.IndexOf("\n  server:", StringComparison.Ordinal)..compose.IndexOf("\n  database:", StringComparison.Ordinal)];
    var list = server[server.IndexOf("    secrets:", StringComparison.Ordinal)..];
    return SecretListItem().Matches(list).Select(x => x.Groups["name"].Value);
  }

  private static IEnumerable<string> SecretFiles() =>
    SecretFile().Matches(File.ReadAllText(Path.Combine(Folder, "docker-compose.secrets.yml"))).Select(x => x.Groups["name"].Value);

  /// <summary>Every options class the server binds, by its section.</summary>
  private static Dictionary<string, Type> Sections() =>
    typeof(Program).Assembly.GetTypes()
      .Select(x => (Type: x, Section: x.GetField("Section", BindingFlags.Public | BindingFlags.Static)))
      .Where(x => x.Section is { IsLiteral: true } && x.Type.Name.EndsWith("Options", StringComparison.Ordinal))
      .ToDictionary(x => (string)x.Section!.GetRawConstantValue()!, x => x.Type, StringComparer.Ordinal);

  private static bool IsServerSetting(string key)
  {
    if (RootSettings.Contains(key, StringComparer.Ordinal))
    {
      return true;
    }

    var parts = key.Split(':');
    if (!Sections().TryGetValue(parts[0], out var type))
    {
      return false;
    }

    foreach (var part in parts.Skip(1))
    {
      if (ElementOf(type) is { } element)
      {
        if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
          return false;
        }

        type = element;
        continue;
      }

      // Exact case: the README and the example are read by people, who copy the name.
      var property = type.GetProperty(part, BindingFlags.Public | BindingFlags.Instance);
      if (property is not { CanWrite: true })
      {
        return false;
      }

      type = property.PropertyType;
    }

    return IsValue(type);
  }

  /// <summary>Each setting under an options type, with a list shown by its first item.</summary>
  private static IEnumerable<string> Leaves(Type type, string path)
  {
    if (IsValue(type))
    {
      yield return path;
      yield break;
    }

    if (ElementOf(type) is { } element)
    {
      foreach (var leaf in Leaves(element, path + ":0"))
      {
        yield return leaf;
      }

      yield break;
    }

    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(x => x.CanWrite))
    {
      foreach (var leaf in Leaves(property.PropertyType, $"{path}:{property.Name}"))
      {
        yield return leaf;
      }
    }
  }

  private static string DefaultOf(IConfiguration defaults, string key)
  {
    var parts = key.Split(':');
    var type = Sections()[parts[0]];
    var options = defaults.GetSection(parts[0]).Get(type) ?? Activator.CreateInstance(type);

    object? value = options;
    foreach (var part in parts.Skip(1))
    {
      value = value switch
      {
        null => null,
        IList list => int.Parse(part, CultureInfo.InvariantCulture) < list.Count ? list[int.Parse(part, CultureInfo.InvariantCulture)] : null,
        _ => value.GetType().GetProperty(part)!.GetValue(value),
      };
    }

    return value switch
    {
      null => "",
      bool flag => flag ? "true" : "false",
      TimeSpan span => span.ToString("c", CultureInfo.InvariantCulture),
      IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
      _ => value.ToString() ?? "",
    };
  }

  private static Type? ElementOf(Type type) =>
    type == typeof(string) ? null
    : type.IsArray ? type.GetElementType()
    : type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type) ? type.GetGenericArguments()[0]
    : null;

  private static bool IsValue(Type type)
  {
    type = Nullable.GetUnderlyingType(type) ?? type;
    return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(TimeSpan) || type == typeof(Guid) ||
      type == typeof(decimal);
  }

  [GeneratedRegex(@"^(?<comment>#)?(?<name>[A-Za-z][A-Za-z0-9_]*)=(?<value>.*)$")]
  private static partial Regex ExampleLinePattern();

  [GeneratedRegex(@"^      StealthDesk_(?<name>[A-Za-z0-9_]+):", RegexOptions.Multiline)]
  private static partial Regex ComposeSetting();

  [GeneratedRegex(@"(Password|Secret|Base64)$")]
  private static partial Regex SensitiveName();

  [GeneratedRegex(@"^      - (?<name>\S+)\r?$", RegexOptions.Multiline)]
  private static partial Regex SecretListItem();

  [GeneratedRegex(@"^    file: \./secrets/(?<name>\S+)\r?$", RegexOptions.Multiline)]
  private static partial Regex SecretFile();
}
