using StealthDesk.Contracts.Devices;

namespace StealthDesk.Web.Client.Tests;

internal static class SampleDevices
{
  public static DeviceSummary Sample(string name, bool isOnline = true, DateTimeOffset? lastSeen = null) => new()
  {
    Id = Guid.NewGuid(),
    Name = name,
    OsDescription = "Microsoft Windows 11 Pro",
    CpuLoad = 0.25,
    MemoryTotalGb = 16,
    MemoryUsedGb = 8,
    StorageTotalGb = 512,
    StorageUsedGb = 256,
    LoggedOnUsers = ["alice", "bob"],
    IsOnline = isOnline,
    LastSeen = lastSeen ?? new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
  };
}
