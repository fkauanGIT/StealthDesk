using MessagePack;

namespace StealthDesk.Contracts.Devices;

/// <summary>A fixed disk of a device. Sizes are in gigabytes.</summary>
[MessagePackObject(keyAsPropertyName: true)]
public sealed record DiskInfo
{
  public string Name { get; init; } = string.Empty;
  public string Label { get; init; } = string.Empty;
  public string Format { get; init; } = string.Empty;
  public double SizeGb { get; init; }
  public double FreeGb { get; init; }
}
