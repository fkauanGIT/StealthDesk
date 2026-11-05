using StealthDesk.RemoteControl;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>A privacy screen that records calls and reports back whatever was asked.</summary>
internal sealed class FakePrivacyScreen : IPrivacyScreen
{
  public List<bool> Calls { get; } = [];

  public bool SetActive(bool active)
  {
    Calls.Add(active);
    return active;
  }
}
