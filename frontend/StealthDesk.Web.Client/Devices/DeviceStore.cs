using System.Net.Http.Json;
using System.Text.Json;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Devices;

namespace StealthDesk.Web.Client.Devices;

/// <summary>
/// The devices the dashboard shows. Loaded over HTTP and kept current by live updates; pages render from it.
/// </summary>
public sealed class DeviceStore(HttpClient http)
{
  private Dictionary<Guid, DeviceSummary> _devices = [];
  private HashSet<Guid>? _changedWhileLoading;
  private bool _loaded;

  /// <summary>Raised whenever <see cref="Devices"/> or <see cref="Error"/> changes.</summary>
  public event Action? Changed;

  /// <summary>Ordered by name; null until the first load finishes.</summary>
  public IReadOnlyList<DeviceSummary>? Devices { get; private set; }

  public string? Error { get; private set; }

  /// <summary>
  /// Replaces the list with the server's: devices it no longer has are dropped. Changes pushed while the request
  /// was in flight are newer than the answer, so they are kept.
  /// </summary>
  public async Task LoadAsync()
  {
    _changedWhileLoading = [];
    try
    {
      var answer = await http.GetFromJsonAsync<List<DeviceSummary>>(Routes.Devices) ?? [];
      var devices = answer.ToDictionary(x => x.Id);
      foreach (var id in _changedWhileLoading)
      {
        var pushed = _devices[id];
        if (!devices.TryGetValue(id, out var loaded) || pushed.LastSeen > loaded.LastSeen)
        {
          devices[id] = pushed;
        }
      }

      _devices = devices;
      _loaded = true;
      Error = null;
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      Error = "Could not load the devices. Check that the server is running.";
    }
    finally
    {
      _changedWhileLoading = null;
    }

    Publish();
  }

  /// <summary>A change pushed by the server: replaces the device, or adds it if it's new.</summary>
  public void Apply(DeviceSummary device)
  {
    // A message delayed behind a newer one must not bring back an older state.
    if (_devices.TryGetValue(device.Id, out var current) && device.LastSeen < current.LastSeen)
    {
      return;
    }

    _devices[device.Id] = device;
    _changedWhileLoading?.Add(device.Id);
    Publish();
  }

  private void Publish()
  {
    if (_loaded)
    {
      Devices = [.. _devices.Values.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)];
    }

    Changed?.Invoke();
  }
}
