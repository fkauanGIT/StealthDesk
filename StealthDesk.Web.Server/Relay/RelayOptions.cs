namespace StealthDesk.Web.Server.Relay;

/// <summary>Limits of the relay remote control streams go through. Bound from the <c>Relay</c> section.</summary>
public sealed class RelayOptions
{
  public const string Section = "Relay";

  /// <summary>How long the tokens of a session can be used before either end connects.</summary>
  public TimeSpan TicketLifetime { get; init; } = TimeSpan.FromMinutes(1);

  /// <summary>How long the first end to connect waits for the other before the session is given up.</summary>
  public TimeSpan PartnerTimeout { get; init; } = TimeSpan.FromSeconds(20);

  /// <summary>The largest message an end may send; a larger one closes the session.</summary>
  public int MaxMessageBytes { get; init; } = 16 * 1024 * 1024;
}
