using StealthDesk.Contracts.Devices;

namespace StealthDesk.Web.Client.Devices;

/// <summary>The answer to looking up one device: found, unknown to the server, or the server couldn't say.</summary>
public sealed record DeviceLookup(DeviceSummary? Device, bool IsNotFound, bool IsFailed)
{
  public static readonly DeviceLookup NotFound = new(null, IsNotFound: true, IsFailed: false);
  public static readonly DeviceLookup Failed = new(null, IsNotFound: false, IsFailed: true);

  public static DeviceLookup Found(DeviceSummary device) => new(device, IsNotFound: false, IsFailed: false);
}
