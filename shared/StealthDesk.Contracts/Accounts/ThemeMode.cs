namespace StealthDesk.Contracts.Accounts;

/// <summary>How the web client looks.</summary>
public enum ThemeMode
{
  /// <summary>Light or dark, as the device is set.</summary>
  Auto = 0,
  Light = 1,
  Dark = 2,
}

/// <summary>The signed-in user's chosen theme, kept with their account so it follows them to any browser.</summary>
public sealed record ThemeUpdate(ThemeMode Theme);
