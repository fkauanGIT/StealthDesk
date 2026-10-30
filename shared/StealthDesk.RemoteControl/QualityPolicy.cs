using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl;

/// <summary>The JPEG quality to encode with, from the settings and the bandwidth being used.</summary>
public static class QualityPolicy
{
  /// <summary>
  /// The quality set by hand, or, automatically, the maximum up to the lower bound, the minimum from the upper bound,
  /// and in proportion in between: the more is sent, the less each frame weighs.
  /// </summary>
  public static int For(CaptureSettings settings, double mbpsOut)
  {
    settings = settings.Normalized();
    if (!settings.AutoQuality)
    {
      return settings.Quality;
    }

    if (mbpsOut <= settings.AutoQualityLowerMbps)
    {
      return settings.AutoQualityMaximum;
    }

    if (mbpsOut >= settings.AutoQualityUpperMbps)
    {
      return settings.AutoQualityMinimum;
    }

    var share = (mbpsOut - settings.AutoQualityLowerMbps) / (settings.AutoQualityUpperMbps - settings.AutoQualityLowerMbps);
    return (int)Math.Round(settings.AutoQualityMaximum - ((settings.AutoQualityMaximum - settings.AutoQualityMinimum) * share));
  }

  /// <summary>
  /// Whether what was sent at a lower quality may now be sent again at the best: back at the maximum, with the
  /// bandwidth well under the lower bound, so the recovery doesn't push it back up.
  /// </summary>
  public static bool MayRecover(CaptureSettings settings, double mbpsOut)
  {
    settings = settings.Normalized();
    return settings.AutoQuality && mbpsOut <= settings.AutoQualityLowerMbps * 0.75;
  }
}
