using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Agent.Tests.Fakes;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.DesktopClient;
using StealthDesk.RemoteControl;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// The desktop client's side of a remote control session: it joins the relay in the background, tells the user when
/// the session starts and ends unless the tenant turned that off, and leaves the relay when it closes. The relay is a
/// WebSocket over a local socket, so these run on every platform.
/// </summary>
public sealed class RemoteControlSessionsTests : IAsyncDisposable
{
  private static readonly Uri Address = new("wss://desk.example.com/relay?sessionId=1&role=Device&token=secret");

  private readonly Notices _notices = new();
  private readonly StoppingLifetime _lifetime = new();
  private readonly ConcurrentQueue<Uri> _joined = new();
  private readonly List<IDisposable> _disposables = [];

  private static CancellationToken Token => TestContext.Current.CancellationToken;

  [Fact]
  public async Task WhileASessionRuns_TheViewerGetsTheDisplays_AndTheScreen_AndCanSwitchDisplays()
  {
    var (relay, sessions) = await ConnectedAsync();
    sessions.Start(Request(notify: false));
    var viewer = new RelayStream(relay, TimeProvider.System, NullLogger.Instance);
    var received = new ConcurrentQueue<StreamEnvelope>();
    _ = viewer.RunAsync((message, _) => { received.Enqueue(message); return Task.CompletedTask; }, _lifetime.ApplicationStopping);

    Assert.True(await Wait.UntilAsync(() => received.Any(x => x.Type == StreamMessageType.ScreenRegions)));
    var displays = Read<DisplayList>(received.First());
    Assert.Equal([FakeScreen.Primary, FakeScreen.Second], displays.Displays);
    Assert.Equal(FakeScreen.Primary.Id, displays.ShownId);
    var first = Read<ScreenRegions>(received.First(x => x.Type == StreamMessageType.ScreenRegions));
    Assert.Equal((0, 0, 64, 48), Single(first));

    await viewer.SendAsync(StreamMessageType.ChangeDisplay, new ChangeDisplay(FakeScreen.Second.Id), Token);

    Assert.True(await Wait.UntilAsync(() => received.Any(x => x.Type == StreamMessageType.ScreenRegions && Single(Read<ScreenRegions>(x)) == (0, 0, 32, 24))));
    Assert.Contains(received, x => x.Type == StreamMessageType.DisplayList && Read<DisplayList>(x).ShownId == FakeScreen.Second.Id);
  }

  [Fact]
  public async Task TheViewersSettings_AndItsAskingForTheWholeScreen_AreTaken()
  {
    var (relay, sessions) = await ConnectedAsync();
    sessions.Start(Request(notify: false));
    var viewer = new RelayStream(relay, TimeProvider.System, NullLogger.Instance);
    var received = new ConcurrentQueue<StreamEnvelope>();
    _ = viewer.RunAsync((message, _) => { received.Enqueue(message); return Task.CompletedTask; }, _lifetime.ApplicationStopping);
    Assert.True(await Wait.UntilAsync(() => received.Any(x => x.Type == StreamMessageType.ScreenRegions)));

    await viewer.SendAsync(StreamMessageType.CaptureSettings, new CaptureSettings(Encoding: ImageEncoding.Png), Token);
    await viewer.SendAsync(StreamMessageType.RequestFullFrame, new RequestFullFrame(), Token);

    // The fake screen never changes: only a whole frame asked for comes, encoded as now set.
    Assert.True(await Wait.UntilAsync(() => received.Any(x =>
      x.Type == StreamMessageType.ScreenRegions && Read<ScreenRegions>(x).Regions.Single().Encoding == ImageEncoding.Png)));
  }

  [Fact]
  public async Task ASession_JoinsTheRelay_AndTellsTheUser_WhenItStartsAndEnds()
  {
    var (relay, sessions) = await ConnectedAsync();

    Assert.Null(sessions.Start(Request(notify: true)));
    var session = Assert.Single(sessions.Running);
    Assert.True(await Wait.UntilAsync(() => _notices.Shown.Count == 1));
    Assert.Equal(Address, Assert.Single(_joined));
    Assert.Equal(new ViewerPermissions(Interact: true, false, false, ReadClipboard: true, false), session.Permissions);

    await relay.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "The other end left.", Token);
    await session.Ended.WaitAsync(TimeSpan.FromSeconds(5), Token);

    Assert.Equal(
      ["tech@example.com started remote control of this computer.", "tech@example.com's remote control of this computer ended."],
      _notices.Shown);
    Assert.Empty(sessions.Running);
  }

  [Fact]
  public async Task WithTheNoticeOff_TheUserIsntTold()
  {
    var (relay, sessions) = await ConnectedAsync();

    sessions.Start(Request(notify: false));
    var session = Assert.Single(sessions.Running);
    await relay.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "The other end left.", Token);
    await session.Ended.WaitAsync(TimeSpan.FromSeconds(5), Token);

    Assert.Single(_joined);
    Assert.Empty(_notices.Shown);
  }

  [Fact]
  public async Task ARelayThatCantBeJoined_EndsTheSession_WithoutTellingTheUser()
  {
    var sessions = Create((_, _) => Task.FromException<WebSocket>(new WebSocketException("The server returned status code '408'.")));

    Assert.Null(sessions.Start(Request(notify: true)));

    // It may be over before it could be looked at.
    Assert.True(await Wait.UntilAsync(() => sessions.Running.Count == 0));
    Assert.Empty(_notices.Shown);
  }

  [Fact]
  public async Task WhenTheClientCloses_ItLeavesTheRelay_AndTellsTheUserTheSessionEnded()
  {
    var (relay, sessions) = await ConnectedAsync();
    sessions.Start(Request(notify: true));
    var session = Assert.Single(sessions.Running);
    Assert.True(await Wait.UntilAsync(() => _notices.Shown.Count == 1));

    _lifetime.StopApplication();

    await session.Ended.WaitAsync(TimeSpan.FromSeconds(5), Token);
    Assert.True(await LeftAsync(relay), "The relay never saw the device leave.");
    Assert.Equal(2, _notices.Shown.Count);
  }

  [Fact]
  public void AnAddressThatIsntAWebSocket_ASessionAlreadyRunning_OrAClosingClient_IsRefused()
  {
    var sessions = Create((_, _) => new TaskCompletionSource<WebSocket>().Task);

    var notWebSocket = sessions.Start(Request(notify: true) with { RelayAddress = new Uri("https://desk.example.com/relay") });
    var first = sessions.Start(Request(notify: true));
    var again = sessions.Start(Request(notify: true));
    _lifetime.StopApplication();
    var closing = sessions.Start(Request(notify: true) with { RelaySessionId = Guid.NewGuid() });

    Assert.Equal("The relay address isn't a WebSocket address.", notWebSocket);
    Assert.Null(first);
    Assert.Equal("That session is already running.", again);
    Assert.Equal("The desktop client is closing.", closing);
  }

  public ValueTask DisposeAsync()
  {
    _lifetime.StopApplication();
    _disposables.ForEach(x => x.Dispose());
    return ValueTask.CompletedTask;
  }

  private static DesktopRemoteControlStart Request(bool notify) => new(
    Guid.Parse("6f1c2d6e-8a51-4c3b-9a9e-1d2f3a4b5c6d"),
    Address,
    "tech@example.com",
    new ViewerPermissions(Interact: true, UseSecureScreens: false, SendCtrlAltDel: false, ReadClipboard: true, WriteClipboard: false),
    notify);

  private RemoteControlSessions Create(RelayConnector connect) =>
    new(connect, () => new FakeScreen(), FakeScreen.Encode, _notices, TimeProvider.System, _lifetime, NullLogger<RemoteControlSessions>.Instance);

  /// <summary>Sessions whose relay is the returned WebSocket: the server's end of the one the client joins with.</summary>
  private async Task<(WebSocket Relay, RemoteControlSessions Sessions)> ConnectedAsync()
  {
    var (relay, device) = await SocketPairAsync();
    return (relay, Create((address, _) =>
    {
      _joined.Enqueue(address);
      return Task.FromResult(device);
    }));
  }

  private Task<(WebSocket Server, WebSocket Client)> SocketPairAsync() => LocalWebSockets.ConnectAsync(_disposables, Token);

  // A close message, or the connection gone: either way the device's end left.
  private static async Task<bool> LeftAsync(WebSocket relay)
  {
    var buffer = new byte[1024];
    try
    {
      while (true)
      {
        ValueWebSocketReceiveResult received = await relay.ReceiveAsync(buffer.AsMemory(), Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        if (received.MessageType == WebSocketMessageType.Close)
        {
          return true;
        }
      }
    }
    catch (WebSocketException)
    {
      return true;
    }
    catch (TimeoutException)
    {
      return false;
    }
  }

  private static T Read<T>(StreamEnvelope message) => MessagePack.MessagePackSerializer.Deserialize<T>(message.Payload);

  // The only region of a frame, where it is: the fake screen never changes, so every frame sent is a whole screen.
  private static (int X, int Y, int Width, int Height) Single(ScreenRegions frame) =>
    Assert.Single(frame.Regions) is var region ? (region.X, region.Y, region.Width, region.Height) : default;

  private sealed class Notices : IUserNotice
  {
    private readonly ConcurrentQueue<string> _shown = new();

    public IReadOnlyList<string> Shown => [.. _shown];

    public void Show(string message) => _shown.Enqueue(message);
  }

  private sealed class StoppingLifetime : IHostApplicationLifetime
  {
    private readonly CancellationTokenSource _stopping = new();

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => _stopping.Token;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() => _stopping.Cancel();
  }
}
