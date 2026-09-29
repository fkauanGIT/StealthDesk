using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace StealthDesk.Agent.Core.Settings;

/// <summary>Current agent settings, and the few values the agent writes back to its own settings file.</summary>
public interface ISettingsStore
{
  AgentSettings Current { get; }

  Task SavePrivateKeyAsync(string privateKey, CancellationToken cancellationToken = default);

  Task SaveIdentityAsync(Guid deviceId, Guid tenantId, CancellationToken cancellationToken = default);
}

public sealed class SettingsStore(IOptions<AgentSettings> startup, IAgentPaths paths) : ISettingsStore, IDisposable
{
  private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

  private readonly SemaphoreSlim _writeGate = new(1, 1);
  private AgentSettings _current = startup.Value;

  public AgentSettings Current => Volatile.Read(ref _current);

  public Task SavePrivateKeyAsync(string privateKey, CancellationToken cancellationToken = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(privateKey);
    return SaveAsync(settings => settings with { PrivateKey = privateKey }, cancellationToken);
  }

  public Task SaveIdentityAsync(Guid deviceId, Guid tenantId, CancellationToken cancellationToken = default)
  {
    return SaveAsync(settings => settings with { DeviceId = deviceId, TenantId = tenantId }, cancellationToken);
  }

  public void Dispose() => _writeGate.Dispose();

  // Rewrites only the "Agent" section, keeping anything else in the file, and swaps the file in one move
  // so a crash mid-write can't leave a half-written identity behind.
  private async Task SaveAsync(Func<AgentSettings, AgentSettings> change, CancellationToken cancellationToken)
  {
    await _writeGate.WaitAsync(cancellationToken);
    try
    {
      var updated = change(Current);
      var file = paths.SettingsFile;
      Directory.CreateDirectory(Path.GetDirectoryName(file)!);

      var document = File.Exists(file)
        ? JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken)) as JsonObject ?? []
        : [];

      document[AgentSettings.Section] = JsonSerializer.SerializeToNode(updated, _json);

      var temporary = file + ".tmp";
      await File.WriteAllTextAsync(temporary, document.ToJsonString(_json), cancellationToken);
      File.Move(temporary, file, overwrite: true);

      Volatile.Write(ref _current, updated);
    }
    finally
    {
      _writeGate.Release();
    }
  }
}
