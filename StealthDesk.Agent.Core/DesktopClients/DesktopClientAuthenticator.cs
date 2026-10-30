using Microsoft.Extensions.Logging;

namespace StealthDesk.Agent.Core.DesktopClients;

/// <summary>
/// Decides whether the process that connected to the pipe is the desktop client: the program installed next to the
/// agent, signed like the agent. There is no shared secret; the proof is what the operating system says about the
/// process. A program that keeps failing is turned away for a while without being looked at.
/// </summary>
public sealed class DesktopClientAuthenticator(
  DesktopClientPipeRules rules,
  IExecutableSigners signers,
  TimeProvider clock,
  ILogger<DesktopClientAuthenticator> logger)
{
  public const int MaxFailuresPerMinute = 5;

  private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);

  private readonly Dictionary<string, List<DateTimeOffset>> _failures = new(StringComparer.OrdinalIgnoreCase);
  private readonly Lock _lock = new();

  /// <summary>Null when <paramref name="client"/> may use the pipe; otherwise why not.</summary>
  public string? Refusal(PipeClient client)
  {
    var path = client.ExecutablePath;
    if (string.IsNullOrWhiteSpace(path))
    {
      return $"Process {client.ProcessId} has no executable path.";
    }

    if (IsLimited(path))
    {
      return $"{path} failed {MaxFailuresPerMinute} times in the last minute; refused without checking.";
    }

    var refusal = WrongExecutable(path) ?? (rules.IsDebugBuild ? null : signers.Mismatch(path));
    if (refusal is not null)
    {
      RecordFailure(path);
    }

    return refusal;
  }

  private string? WrongExecutable(string path)
  {
    if (SamePath(path, rules.Executable))
    {
      return null;
    }

    if (rules.IsDebugBuild)
    {
      var name = Path.GetFileName(path);
      if (name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) || name.Equals("dotnet", StringComparison.Ordinal))
      {
        logger.LogInformation("Debug build: taking {Path} as a desktop client run by the .NET host.", path);
        return null;
      }

      if (rules.DebugFolder is { } folder
        && name.Equals(Path.GetFileName(rules.Executable), StringComparison.OrdinalIgnoreCase)
        && IsUnder(path, folder))
      {
        return null;
      }

      return $"{path} is neither {rules.Executable}, dotnet.exe, nor the desktop client built under {rules.DebugFolder}.";
    }

    return $"{path} isn't the desktop client installed at {rules.Executable}.";
  }

  private bool IsLimited(string path)
  {
    lock (_lock)
    {
      if (!_failures.TryGetValue(path, out var failures))
      {
        return false;
      }

      var since = clock.GetUtcNow() - FailureWindow;
      failures.RemoveAll(x => x <= since);
      return failures.Count >= MaxFailuresPerMinute;
    }
  }

  private void RecordFailure(string path)
  {
    lock (_lock)
    {
      if (!_failures.TryGetValue(path, out var failures))
      {
        _failures[path] = failures = [];
      }

      failures.Add(clock.GetUtcNow());
    }
  }

  private static bool SamePath(string a, string b) =>
    string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

  private static bool IsUnder(string path, string folder) =>
    Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
