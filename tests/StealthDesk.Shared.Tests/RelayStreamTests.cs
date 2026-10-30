using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.Shared.Tests;

/// <summary>
/// The two ends of a remote control session: messages in order, each acknowledged, and a sender that waits while too
/// much is unacknowledged. The two ends are WebSockets over a local socket.
/// </summary>
public sealed class RelayStreamTests : IDisposable
{
  private readonly List<IDisposable> _disposables = [];
  private readonly CancellationTokenSource _stop = new();

  private static CancellationToken Token => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Messages_ArriveInOrder_AndEachIsAcknowledged()
  {
    var (sender, receiver) = await PairAsync();
    var received = new ConcurrentQueue<string>();
    var receiving = receiver.Stream.RunAsync(Collect(received), _stop.Token);
    var acks = sender.Stream.RunAsync(Ignore, _stop.Token);

    foreach (var text in new[] { "one", "two", "three" })
    {
      await sender.Stream.SendAsync(TestType, text, Token);
    }

    Assert.True(await Until(() => received.Count == 3 && sender.Stream.UnacknowledgedBytes == 0));
    Assert.Equal(["one", "two", "three"], received);
  }

  [Fact]
  public async Task TheSender_Waits_WhileMoreThanTheWindowIsUnacknowledged()
  {
    var (sender, receiver) = await PairAsync();
    _ = sender.Stream.RunAsync(Ignore, _stop.Token);
    var chunk = new string('x', 50 * 1024);

    // 50 KB in flight, then 100 KB: both under the window.
    await sender.Stream.SendAsync(TestType, chunk, Token);
    await sender.Stream.SendAsync(TestType, chunk, Token);
    await sender.Stream.SendAsync(TestType, chunk, Token);
    var fourth = sender.Stream.SendAsync(TestType, chunk, Token);
    await Task.Delay(300, Token);

    Assert.False(fourth.IsCompleted, "The fourth message went out with 150 KB unacknowledged.");
    Assert.True(sender.Stream.UnacknowledgedBytes >= RelayStream.MaxUnacknowledgedBytes);

    var received = new ConcurrentQueue<string>();
    _ = receiver.Stream.RunAsync(Collect(received), _stop.Token);
    await fourth.WaitAsync(TimeSpan.FromSeconds(5), Token);
    Assert.True(await Until(() => received.Count == 4 && sender.Stream.UnacknowledgedBytes == 0));
  }

  [Fact]
  public async Task AMessageLargerThanTheWindow_StillGoes_WhenNothingIsInFlight()
  {
    var (sender, receiver) = await PairAsync();
    var received = new ConcurrentQueue<string>();
    _ = receiver.Stream.RunAsync(Collect(received), _stop.Token);
    _ = sender.Stream.RunAsync(Ignore, _stop.Token);
    var large = new string('y', RelayStream.MaxUnacknowledgedBytes * 3);

    await sender.Stream.SendAsync(TestType, large, Token).WaitAsync(TimeSpan.FromSeconds(5), Token);

    Assert.True(await Until(() => received.Count == 1));
    Assert.Equal(large, Assert.Single(received));
  }

  [Fact]
  public async Task TheLatency_IsHowLongTheAckTookToComeBack()
  {
    var clock = new FakeTimeProvider();
    var (sender, receiver) = await PairAsync(clock);
    var received = new ConcurrentQueue<string>();
    _ = receiver.Stream.RunAsync(Collect(received), _stop.Token);

    await sender.Stream.SendAsync(TestType, "ping", Token);
    Assert.True(await Until(() => received.Count == 1));
    clock.Advance(TimeSpan.FromMilliseconds(40));
    // Its ack is waiting on the socket: reading it now, 40 ms after sending.
    _ = sender.Stream.RunAsync(Ignore, _stop.Token);

    Assert.True(await Until(() => sender.Stream.Latency == TimeSpan.FromMilliseconds(40)), $"Latency was {sender.Stream.Latency}.");
  }

  [Fact]
  public async Task WhenTheOtherEndCloses_TheStreamEnds_WithWhy()
  {
    var (sender, receiver) = await PairAsync();
    var receiving = receiver.Stream.RunAsync(Ignore, _stop.Token);

    await sender.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "The other end left.", Token);

    Assert.Equal("The other end left.", await receiving.WaitAsync(TimeSpan.FromSeconds(5), Token));
  }

  [Fact]
  public async Task Stopping_ClosesThisEnd_AndTheOtherEndSeesIt()
  {
    var (sender, receiver) = await PairAsync();
    using var stopping = new CancellationTokenSource();
    var receiving = receiver.Stream.RunAsync(Ignore, stopping.Token);
    var sending = sender.Stream.RunAsync(Ignore, _stop.Token);

    await stopping.CancelAsync();

    Assert.Equal("Stopped.", await receiving.WaitAsync(TimeSpan.FromSeconds(5), Token));
    Assert.Equal("The other end is closing.", await sending.WaitAsync(TimeSpan.FromSeconds(5), Token));
  }

  [Fact]
  public async Task AMessageThatCantBeRead_EndsTheStream()
  {
    var (sender, receiver) = await PairAsync();
    var handled = 0;
    var receiving = receiver.Stream.RunAsync((_, _) => { handled++; return Task.CompletedTask; }, _stop.Token);

    await sender.Socket.SendAsync(new byte[] { 0xc1, 0x00, 0x13 }, WebSocketMessageType.Binary, endOfMessage: true, Token);

    Assert.Equal("The other end sent a message that couldn't be read.", await receiving.WaitAsync(TimeSpan.FromSeconds(5), Token));
    Assert.Equal(0, handled);
  }

  [Fact]
  public async Task AnAck_CantBeSentAsAMessage()
  {
    var (sender, _) = await PairAsync();

    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => sender.Stream.SendAsync(StreamMessageType.Ack, new StreamAck(1, 0), Token));
  }

  public void Dispose()
  {
    _stop.Cancel();
    _disposables.ForEach(x => x.Dispose());
  }

  // Any type but an ack is carried the same way.
  private static StreamMessageType TestType => (StreamMessageType)99;

  private static Func<StreamEnvelope, CancellationToken, Task> Collect(ConcurrentQueue<string> into) => (message, _) =>
  {
    into.Enqueue(MessagePackSerializer.Deserialize<string>(message.Payload));
    return Task.CompletedTask;
  };

  private static Task Ignore(StreamEnvelope message, CancellationToken cancellationToken) => Task.CompletedTask;

  private static async Task<bool> Until(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
      if (condition())
      {
        return true;
      }

      await Task.Delay(20);
    }

    return false;
  }

  private async Task<(End Sender, End Receiver)> PairAsync(TimeProvider? clock = null)
  {
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var client = new TcpClient();
    _disposables.Add(client);
    var accepting = listener.AcceptTcpClientAsync(Token);
    await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, Token);
    var server = await accepting;
    _disposables.Add(server);
    var a = WebSocket.CreateFromStream(client.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
    var b = WebSocket.CreateFromStream(server.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
    _disposables.Add(a);
    _disposables.Add(b);
    return (
      new End(a, new RelayStream(a, clock ?? TimeProvider.System, NullLogger.Instance)),
      new End(b, new RelayStream(b, clock ?? TimeProvider.System, NullLogger.Instance)));
  }

  private sealed record End(WebSocket Socket, RelayStream Stream);
}
