using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.Web.Client.RemoteControl;

/// <summary>The browser's end of a remote control session, as the page uses it. A fake stands in for it in tests.</summary>
public interface IRemoteDesktopConnection : IAsyncDisposable
{
    event Action<DisplayList>? DisplaysChanged;

    event Action<ScreenRegions>? RegionsReceived;

    event Action<string>? Ended;

    Task StartAsync(CancellationToken cancellationToken);

    Task ApplyAsync(CaptureSettings settings);

    Task ChangeDisplayAsync(string displayId);

    Task MovePointerAsync(double x, double y);

    Task MouseButtonAsync(int button, bool isPressed, double x, double y);

    Task MouseWheelAsync(double x, double y, int deltaY, int deltaX);

    Task KeyAsync(string key, string code, bool isPressed, KeyModifiers modifiers);

    Task ResetKeyboardAsync();

    Task CtrlAltDelAsync();
}

/// <summary>Makes the connection for a session's ticket; replaced in tests so no real relay is reached.</summary>
public interface IRemoteDesktopConnections
{
    IRemoteDesktopConnection Create(Uri server, RemoteControlTicket ticket);
}

internal sealed class RemoteDesktopConnections(TimeProvider clock, ILoggerFactory loggerFactory) : IRemoteDesktopConnections
{
    public IRemoteDesktopConnection Create(Uri server, RemoteControlTicket ticket) =>
        new RemoteDesktopConnection(server, ticket, clock, loggerFactory.CreateLogger<RemoteDesktopConnection>());
}
