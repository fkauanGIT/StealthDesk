using System.Security.Cryptography;

namespace StealthDesk.Core;

/// <summary>
/// Waiting time between retries: doubles on every attempt up to <see cref="Ceiling"/>, plus a random spread
/// so that many clients retrying at once (e.g. after a server restart) don't all hit at the same instant.
/// </summary>
public sealed class Backoff(TimeSpan first, TimeSpan ceiling, TimeSpan maxSpread)
{
  public TimeSpan First { get; } = first;
  public TimeSpan Ceiling { get; } = ceiling;
  public TimeSpan MaxSpread { get; } = maxSpread;

  /// <summary>Delay before the retry number <paramref name="attempt"/> (1 = first retry).</summary>
  public TimeSpan DelayFor(int attempt)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

    // 2^30 is far beyond any sensible ceiling; stop there so the shift can't overflow.
    var factor = 1L << Math.Min(attempt - 1, 30);
    var baseTicks = Math.Min(First.Ticks * factor, Ceiling.Ticks);
    if (baseTicks < 0)
    {
      baseTicks = Ceiling.Ticks;
    }

    var spreadMs = MaxSpread <= TimeSpan.Zero
      ? 0
      : RandomNumberGenerator.GetInt32(0, (int)MaxSpread.TotalMilliseconds + 1);

    return TimeSpan.FromTicks(baseTicks) + TimeSpan.FromMilliseconds(spreadMs);
  }
}
