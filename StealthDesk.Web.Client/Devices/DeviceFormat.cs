using System.Globalization;

namespace StealthDesk.Web.Client.Devices;

/// <summary>How device numbers read on screen.</summary>
public static class DeviceFormat
{
  public const string Missing = "—";

  public static string Gigabytes(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);

  public static string Usage(double usedGb, double totalGb) =>
    totalGb > 0 ? $"{Gigabytes(usedGb)} / {Gigabytes(totalGb)} GB" : Missing;

  public static double Share(double used, double total) => total > 0 ? used / total : 0;

  public static string Percent(double share) =>
    (Math.Clamp(double.IsNaN(share) ? 0 : share, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

  public static string Architecture(System.Runtime.InteropServices.Architecture value) => value.ToString().ToLowerInvariant();

  public static string OrMissing(string? value) => string.IsNullOrWhiteSpace(value) ? Missing : value;

  public static string LastSeen(DateTimeOffset lastSeen) => lastSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
