using System.Text.Json;
using System.Text.Json.Nodes;
using StealthDesk.Agent.Shared.Options;
using StealthDesk.Libraries.Shared.Services.FileSystem;
using Microsoft.Extensions.Options;

namespace StealthDesk.Agent.Shared.Services;

public interface IOptionsAccessor
{
  Guid DeviceId { get; }
  string InstanceId { get; }
  string? PrivateKey { get; }
  Uri ServerUri { get; }
  Guid TenantId { get; }
  string GetAppSettingsPath();
  Guid GetRequiredTenantId();
  Task UpdateAppOptions(AgentAppOptions options);
  Task UpdateId(Guid uid);
  Task UpdatePrivateKey(string privateKeyBase64);
}

internal class OptionsAccessor(
  IFileSystem fileSystem,
  IFileSystemPathProvider fileSystemPathProvider,
  IOptionsMonitor<AgentAppOptions> appOptions,
  IOptions<InstanceOptions> instanceOptions) : IOptionsAccessor
{
  private readonly IOptionsMonitor<AgentAppOptions> _appOptions = appOptions;
  private readonly IFileSystem _fileSystem = fileSystem;
  private readonly IFileSystemPathProvider _fileSystemPathProvider = fileSystemPathProvider;
  private readonly IOptions<InstanceOptions> _instanceOptions = instanceOptions;
  private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
  private readonly SemaphoreSlim _updateLock = new(1, 1);

  public Guid DeviceId => _appOptions.CurrentValue.DeviceId;
  public string InstanceId => _instanceOptions.Value.InstanceId ?? string.Empty;
  public string? PrivateKey => _appOptions.CurrentValue.PrivateKey;
  public Uri ServerUri =>
    _appOptions.CurrentValue.ServerUri ??
    AppConstants.ServerUri ??
    throw new InvalidOperationException("Server URI is not configured correctly.");
  public Guid TenantId => _appOptions.CurrentValue.TenantId;

  public string GetAppSettingsPath()
  {
    return _fileSystemPathProvider.GetAgentAppSettingsPath();
  }

  public Guid GetRequiredTenantId()
  {
    return _appOptions.CurrentValue.TenantId == default
    ? throw new InvalidOperationException("Tenant ID is not configured correctly.")
    : _appOptions.CurrentValue.TenantId;
  }

  public async Task UpdateAppOptions(AgentAppOptions options)
  {
    using var guard = await _updateLock.AcquireLockAsync(CancellationToken.None);

    var path = GetAppSettingsPath();

    if (!_fileSystem.FileExists(path))
    {
      await _fileSystem.WriteAllTextAsync(path, "{}");
    }

    var contents = await _fileSystem.ReadAllTextAsync(path);
    var json = JsonNode.Parse(contents) ??
               throw new InvalidOperationException("Failed to parse app settings JSON.");

    json[AgentAppOptions.SectionKey] = JsonSerializer.SerializeToNode(options);
    contents = JsonSerializer.Serialize(json, _jsonOptions);
    await _fileSystem.WriteAllTextAsync(path, contents);
  }

  public async Task UpdateId(Guid uid)
  {
    _appOptions.CurrentValue.DeviceId = uid;
    await UpdateAppOptions(_appOptions.CurrentValue);
  }

  public async Task UpdatePrivateKey(string privateKeyBase64)
  {
    _appOptions.CurrentValue.PrivateKey = privateKeyBase64;
    await UpdateAppOptions(_appOptions.CurrentValue);
  }
}
