using StealthDesk.Contracts.Devices;

namespace StealthDesk.Agent.Core.Inventory;

/// <summary>Reads the machine's current state. Identity fields (device and tenant ids) are left empty.</summary>
public interface IDeviceInventory
{
  Task<DeviceReport> CaptureAsync(CancellationToken cancellationToken = default);
}

/// <summary>Latest measured processor load, from 0 (idle) to 1 (fully busy).</summary>
public interface ICpuLoad
{
  double Current { get; }
}
