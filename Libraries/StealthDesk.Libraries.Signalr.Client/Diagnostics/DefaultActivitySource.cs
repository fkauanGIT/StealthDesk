using System.Diagnostics;

namespace StealthDesk.Libraries.Signalr.Client.Diagnostics;
internal static class DefaultActivitySource
{
  public const string Name = "StealthDesk.Libraries.Signalr.Client";
  public static readonly ActivitySource Instance = new(Name);
  public static Activity? StartActivity(string name) => Instance.StartActivity(name);
}
