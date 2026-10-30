using System.Net.WebSockets;
using MessagePack;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.RemoteControl;

namespace StealthDesk.RemoteControl;

/// <summary>
/// One end of a remote control session on the relay. Each message goes in a <see cref="StreamEnvelope"/>, and the
/// other end answers every one with a <see cref="StreamAck"/>: a sender with more than
/// <see cref="MaxUnacknowledgedBytes"/> in flight waits before sending more, so it never runs ahead of what the
/// connection carries, and each ack tells how long the trip took.
/// </summary>
public sealed class RelayStream(WebSocket socket, TimeProvider clock, ILogger logger)
{
  /// <summary>How much may be sent and not yet acknowledged; a single larger message still goes when nothing is.</summary>
  public const int MaxUnacknowledgedBytes = 128 * 1024;

  /// <summary>The largest message taken from the other end, as large as the relay lets through.</summary>
  public const int MaxMessageBytes = 16 * 1024 * 1024;

  private const int BufferBytes = 64 * 1024;

  private readonly SemaphoreSlim _sending = new(1, 1);
  private readonly Lock _gate = new();
  private long _unacknowledged;
  private long _latencyTicks;
  private TaskCompletionSource _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public long UnacknowledgedBytes
  {
    get
    {
      lock (_gate)
      {
        return _unacknowledged;
      }
    }
  }

  /// <summary>How long the last acknowledged message took to reach the other end and its ack to come back.</summary>
  public TimeSpan Latency => TimeSpan.FromTicks(Interlocked.Read(ref _latencyTicks));

  /// <summary>Sends <paramref name="message"/>, once the other end acknowledged enough of what was sent before.</summary>
  public async Task SendAsync<T>(StreamMessageType type, T message, CancellationToken cancellationToken)
  {
    ArgumentOutOfRangeException.ThrowIfEqual(type, StreamMessageType.Ack);
    await WaitForRoomAsync(cancellationToken);
    var bytes = MessagePackSerializer.Serialize(
      new StreamEnvelope(type, MessagePackSerializer.Serialize(message, cancellationToken: cancellationToken), clock.GetTimestamp()),
      cancellationToken: cancellationToken);

    // Counted first: the ack may come back before the send returns.
    lock (_gate)
    {
      _unacknowledged += bytes.Length;
    }

    await SendRawAsync(bytes, cancellationToken);
  }

  /// <summary>
  /// Receives until the other end closes or <paramref name="stopping"/> is cancelled, acknowledging each message and
  /// handing it to <paramref name="handle"/>, in order; then closes this end. Answers why the stream ended.
  /// </summary>
  public async Task<string> RunAsync(Func<StreamEnvelope, CancellationToken, Task> handle, CancellationToken stopping)
  {
    var buffer = new byte[BufferBytes];
    using var message = new MemoryStream();
    // Cancelling a receive aborts the WebSocket: stopping sends the close instead, and reads on until the other end
    // answers it, or gives up after a while.
    using var reading = new CancellationTokenSource();
    using var stoppingRegistration = stopping.Register(() => _ = CloseOnStopAsync(reading));
    try
    {
      while (true)
      {
        message.SetLength(0);
        ValueWebSocketReceiveResult received;
        do
        {
          received = await socket.ReceiveAsync(buffer.AsMemory(), reading.Token);
          if (received.MessageType == WebSocketMessageType.Close)
          {
            if (stopping.IsCancellationRequested)
            {
              return "Stopped.";
            }

            // The relay hangs up right after its close message: the stream ended normally either way.
            await TryCloseAsync(WebSocketCloseStatus.NormalClosure, "The session ended.");
            return socket.CloseStatusDescription ?? "The other end left.";
          }

          if (message.Length + received.Count > MaxMessageBytes)
          {
            await TryCloseAsync(WebSocketCloseStatus.MessageTooBig, $"A message was larger than {MaxMessageBytes} bytes.");
            return "The other end sent a message too large to take.";
          }

          message.Write(buffer, 0, received.Count);
        }
        while (!received.EndOfMessage);

        StreamEnvelope envelope;
        try
        {
          envelope = MessagePackSerializer.Deserialize<StreamEnvelope>(message.GetBuffer().AsMemory(0, (int)message.Length), cancellationToken: stopping);
        }
        catch (MessagePackSerializationException ex)
        {
          logger.LogWarning(ex, "A message from the other end couldn't be read; ending the stream.");
          await TryCloseAsync(WebSocketCloseStatus.InvalidPayloadData, "A message couldn't be read.");
          return "The other end sent a message that couldn't be read.";
        }

        if (envelope.Type == StreamMessageType.Ack)
        {
          Acknowledged(MessagePackSerializer.Deserialize<StreamAck>(envelope.Payload, cancellationToken: stopping));
          continue;
        }

        var ack = new StreamEnvelope(
          StreamMessageType.Ack,
          MessagePackSerializer.Serialize(new StreamAck((int)message.Length, envelope.SentAt), cancellationToken: stopping),
          clock.GetTimestamp());
        await SendRawAsync(MessagePackSerializer.Serialize(ack, cancellationToken: stopping), stopping);
        await handle(envelope, stopping);
      }
    }
    catch (Exception ex) when (stopping.IsCancellationRequested && ex is OperationCanceledException or WebSocketException or ObjectDisposedException)
    {
      return "Stopped.";
    }
    catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
    {
      return $"The connection was lost: {ex.Message}";
    }
    finally
    {
      // Nothing more will be acknowledged: a sender waiting for room finds the socket closed instead.
      Release();
    }
  }

  private void Acknowledged(StreamAck ack)
  {
    Interlocked.Exchange(ref _latencyTicks, clock.GetElapsedTime(ack.SentAt).Ticks);
    lock (_gate)
    {
      _unacknowledged = Math.Max(0, _unacknowledged - ack.ReceivedBytes);
    }

    Release();
  }

  private void Release()
  {
    TaskCompletionSource released;
    lock (_gate)
    {
      released = _acknowledged;
      _acknowledged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    released.TrySetResult();
  }

  private async Task WaitForRoomAsync(CancellationToken cancellationToken)
  {
    while (true)
    {
      Task acknowledged;
      lock (_gate)
      {
        if (_unacknowledged < MaxUnacknowledgedBytes || socket.State != WebSocketState.Open)
        {
          return;
        }

        acknowledged = _acknowledged.Task;
      }

      await acknowledged.WaitAsync(cancellationToken);
    }
  }

  // One at a time, as a WebSocket requires: acks go out between the messages being sent.
  private async Task SendRawAsync(byte[] bytes, CancellationToken cancellationToken)
  {
    await _sending.WaitAsync(cancellationToken);
    try
    {
      await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken);
    }
    finally
    {
      _sending.Release();
    }
  }

  private async Task CloseOnStopAsync(CancellationTokenSource reading)
  {
    await TryCloseAsync(WebSocketCloseStatus.EndpointUnavailable, "The other end is closing.");
    try
    {
      reading.CancelAfter(TimeSpan.FromSeconds(2));
    }
    catch (ObjectDisposedException)
    {
      // The stream already ended.
    }
  }

  private async Task TryCloseAsync(WebSocketCloseStatus status, string reason)
  {
    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
    try
    {
      await socket.CloseOutputAsync(status, reason, limit.Token);
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
    {
      // Gone already.
    }
  }
}
