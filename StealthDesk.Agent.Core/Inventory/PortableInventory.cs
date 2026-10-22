using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using StealthDesk.Contracts.Devices;

namespace StealthDesk.Agent.Core.Inventory;

/// <summary>The parts of the inventory .NET can read the same way on every operating system.</summary>
public static class PortableInventory
{
  private const double BytesPerGb = 1024d * 1024 * 1024;

  public static string AgentVersion =>
    Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0";

  public static double ToGb(double bytes) => Math.Round(bytes / BytesPerGb, 2);

  /// <summary>Fixed disks that are mounted and have a size.</summary>
  public static IReadOnlyList<DiskInfo> FixedDisks()
  {
    var disks = new List<DiskInfo>();
    foreach (var drive in DriveInfo.GetDrives())
    {
      try
      {
        if (drive is not { IsReady: true, DriveType: DriveType.Fixed } || drive.TotalSize <= 0)
        {
          continue;
        }

        disks.Add(new DiskInfo
        {
          Name = drive.Name,
          Label = drive.VolumeLabel,
          Format = drive.DriveFormat,
          SizeGb = ToGb(drive.TotalSize),
          FreeGb = ToGb(drive.AvailableFreeSpace),
        });
      }
      catch (IOException)
      {
        // A drive can disappear between listing and reading it; skip it.
      }
      catch (UnauthorizedAccessException)
      {
      }
    }

    return disks;
  }

  /// <summary>Size and used space of the disk the operating system runs from.</summary>
  public static (double TotalGb, double UsedGb) SystemDiskUsage()
  {
    var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? Path.GetPathRoot(Environment.CurrentDirectory);
    try
    {
      var drive = new DriveInfo(systemRoot!);
      return drive.IsReady
        ? (ToGb(drive.TotalSize), ToGb(drive.TotalSize - drive.AvailableFreeSpace))
        : (0, 0);
    }
    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
    {
      return (0, 0);
    }
  }

  /// <summary>Addresses of the network interfaces that are up (loopback and tunnels excluded).</summary>
  public static (string IpV4, string IpV6, IReadOnlyList<string> MacAddresses) Network()
  {
    var active = NetworkInterface.GetAllNetworkInterfaces()
      .Where(x => x.OperationalStatus == OperationalStatus.Up)
      .Where(x => x.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
      .ToList();

    var addresses = active
      .SelectMany(x => x.GetIPProperties().UnicastAddresses)
      .Select(x => x.Address)
      .ToList();

    var ipV4 = addresses.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork);
    var ipV6 = addresses.FirstOrDefault(x =>
      x.AddressFamily == AddressFamily.InterNetworkV6 && !x.IsIPv6LinkLocal && !x.IsIPv6SiteLocal && !x.IsIPv6Teredo);

    var macs = active
      .Select(x => x.GetPhysicalAddress().ToString())
      .Where(x => x.Length > 0)
      .Distinct()
      .ToList();

    return (ipV4?.ToString() ?? string.Empty, ipV6?.ToString() ?? string.Empty, macs);
  }
}
