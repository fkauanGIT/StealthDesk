namespace StealthDesk.RemoteControl;

/// <summary>How much was sent lately: the bytes of the last <see cref="Window"/>, as megabits per second.</summary>
public sealed class TransferMeter(TimeProvider clock, TimeSpan window)
{
  private readonly Queue<(DateTimeOffset At, long Bytes)> _sent = new();
  private readonly Lock _gate = new();
  private long _total;

  public TimeSpan Window { get; } = window;

  public void Record(long bytes)
  {
    lock (_gate)
    {
      _sent.Enqueue((clock.GetUtcNow(), bytes));
      _total += bytes;
      Forget();
    }
  }

  public double Mbps
  {
    get
    {
      lock (_gate)
      {
        Forget();
        return _total * 8 / Window.TotalSeconds / 1_000_000;
      }
    }
  }

  private void Forget()
  {
    var since = clock.GetUtcNow() - Window;
    while (_sent.TryPeek(out var oldest) && oldest.At < since)
    {
      _total -= _sent.Dequeue().Bytes;
    }
  }
}
