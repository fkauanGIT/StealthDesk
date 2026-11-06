namespace StealthDesk.Web.Server.RemoteControl;

/// <summary>Rules the server applies to remote control, for the whole deployment. Bound from the <c>RemoteControl</c> section.</summary>
public sealed class RemoteControlOptions
{
  public const string Section = "RemoteControl";

  /// <summary>Whether a desktop preview may be requested, regardless of anyone's permission.</summary>
  public bool EnableDesktopPreview { get; init; } = true;
}
