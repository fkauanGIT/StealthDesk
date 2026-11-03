using StealthDesk.RemoteControl.Input;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>A fake input desktop: a test sets its name and sees whether it was followed.</summary>
internal sealed class FakeInputDesktop : IInputDesktop
{
    private volatile string? _name = "Default";

    public int Followed { get; private set; }

    public void Set(string? name) => _name = name;

    public string? Name() => _name;

    public bool SwitchToInput()
    {
        Followed++;
        return true;
    }
}
