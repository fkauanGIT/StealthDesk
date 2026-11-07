using System.Threading.Channels;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>A session watcher a test can raise changes on, instead of a real Windows session.</summary>
internal sealed class FakeSessionNotifications : ISessionNotifications
{
  private readonly Channel<SessionChangeKind> _changes = Channel.CreateUnbounded<SessionChangeKind>();

  public IAsyncEnumerable<SessionChangeKind> Changes => _changes.Reader.ReadAllAsync();

  public void Raise(SessionChangeKind kind) => _changes.Writer.TryWrite(kind);

  public void Dispose() => _changes.Writer.TryComplete();
}
