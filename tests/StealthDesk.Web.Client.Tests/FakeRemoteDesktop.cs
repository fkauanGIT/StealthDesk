using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Web.Client.RemoteControl;

namespace StealthDesk.Web.Client.Tests;

/// <summary>A remote desktop connection without a relay: it records calls and lets a test raise its events.</summary>
internal sealed class FakeRemoteDesktop : IRemoteDesktopConnection, IRemoteDesktopConnections
{
    public event Action<DisplayList>? DisplaysChanged;
    public event Action<ScreenRegions>? RegionsReceived;
    public event Action<CaptureMetrics>? MetricsReceived;
    public event Action<string>? ClipboardTextReceived;
    public event Action<SecureDesktop>? SecureDesktopChanged;
    public event Action<bool>? BlockInputChanged;
    public event Action<bool>? PrivacyScreenChanged;
    public event Action<string>? Ended;

    public List<string> Calls { get; } = [];

    public Uri? Server { get; private set; }

    public RemoteControlTicket? Ticket { get; private set; }

    public bool Started { get; private set; }

    public IRemoteDesktopConnection Create(Uri server, RemoteControlTicket ticket)
    {
        Server = server;
        Ticket = ticket;
        return this;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started = true;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(CaptureSettings settings)
    {
        Calls.Add($"apply quality={(settings.AutoQuality ? "auto" : settings.Quality.ToString())}");
        return Task.CompletedTask;
    }

    public Task ChangeDisplayAsync(string displayId)
    {
        Calls.Add($"display {displayId}");
        return Task.CompletedTask;
    }

    public Task MovePointerAsync(double x, double y) => Record($"move {x} {y}");

    public Task MouseButtonAsync(int button, bool isPressed, double x, double y) => Record($"button {button} {isPressed}");

    public Task MouseWheelAsync(double x, double y, int deltaY, int deltaX) => Record($"wheel {deltaY} {deltaX}");

    public Task KeyAsync(string key, string code, bool isPressed, KeyModifiers modifiers) => Record($"key {key} {isPressed}");

    public Task ResetKeyboardAsync() => Record("reset");

    public Task CtrlAltDelAsync() => Record("ctrlaltdel");

    public Task SetBlockInputAsync(bool active) => Record($"blockinput {active}");

    public Task SetPrivacyScreenAsync(bool active) => Record($"privacyscreen {active}");

    public Task SendClipboardTextAsync(string text) => Record($"clipboard-set {text}");

    public Task RequestClipboardAsync() => Record("clipboard-get");

    public void RaiseClipboardText(string text) => ClipboardTextReceived?.Invoke(text);

    public void RaiseSecureDesktop(SecureDesktop message) => SecureDesktopChanged?.Invoke(message);

    public void RaiseBlockInput(bool active) => BlockInputChanged?.Invoke(active);

    public void RaisePrivacyScreen(bool active) => PrivacyScreenChanged?.Invoke(active);

    public ValueTask DisposeAsync()
    {
        Calls.Add("dispose");
        return ValueTask.CompletedTask;
    }

    public void RaiseDisplays(DisplayList displays) => DisplaysChanged?.Invoke(displays);

    public void RaiseRegions(ScreenRegions regions) => RegionsReceived?.Invoke(regions);

    public void RaiseMetrics(CaptureMetrics metrics) => MetricsReceived?.Invoke(metrics);

    public void RaiseEnded(string reason) => Ended?.Invoke(reason);

    private Task Record(string call)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }
}
