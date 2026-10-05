using System.Net;
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

  /// <summary>Raised when the server refuses the session, so the user can sign in again instead of seeing errors.</summary>
  public event Action? SessionExpired;

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
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
    {
      ReportSessionExpired();
      return;
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

  /// <summary>One device, for a page opened directly. Unknown devices are never added to the list.</summary>
  public async Task<DeviceLookup> FindAsync(Guid id)
  {
    if (_devices.TryGetValue(id, out var known))
    {
      return DeviceLookup.Found(known);
    }

    try
    {
      using var response = await http.GetAsync(Routes.Device(id));
      if (response.StatusCode == HttpStatusCode.Unauthorized)
      {
        ReportSessionExpired();
        return DeviceLookup.Failed;
      }

      if (response.StatusCode == HttpStatusCode.NotFound)
      {
        return DeviceLookup.NotFound;
      }

      response.EnsureSuccessStatusCode();
      var device = await response.Content.ReadFromJsonAsync<DeviceSummary>();
      if (device is null)
      {
        return DeviceLookup.NotFound;
      }

      // A push that arrived while asking is newer than the answer.
      if (_devices.TryGetValue(id, out var pushed))
      {
        return DeviceLookup.Found(pushed);
      }

      _devices[id] = device;
      return DeviceLookup.Found(device);
    }
    catch (Exception ex) when (ex is HttpRequestException or JsonException)
    {
      return DeviceLookup.Failed;
    }
  }

  public DeviceSummary? Get(Guid id) => _devices.GetValueOrDefault(id);

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

  /// <summary>The session is gone; anything shown so far belongs to it.</summary>
  public void ReportSessionExpired()
  {
    _devices = [];
    _loaded = false;
    Devices = null;
    SessionExpired?.Invoke();
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
