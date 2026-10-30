using StealthDesk.Agent.Core.DesktopClients;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>Windows sessions as a test sets them.</summary>
internal sealed class FakeDesktopSessions : IDesktopSessions
{
  public List<DesktopSession> Sessions { get; } = [];

  public IReadOnlyList<DesktopSession> Current() => Sessions.ToList();
}

/// <summary>Desktop clients that are only records: which were started where, which exited and which were stopped.</summary>
internal sealed class FakeDesktopClientLauncher : IDesktopClientLauncher
{
  private readonly List<FakeDesktopClient> _all = [];
  private int _nextId = 1000;

  public string? MissingReason { get; set; }

  /// <summary>How many of the next starts fail.</summary>
  public int FailingStarts { get; set; }

  /// <summary>Sessions in the order clients were started in them.</summary>
  public List<int> Starts { get; } = [];

  public IReadOnlyList<FakeDesktopClient> Clients => _all;

  public IEnumerable<FakeDesktopClient> Alive => _all.Where(x => !x.HasExited);

  public string? Missing() => MissingReason;

  public IDesktopClientProcess? Start(int sessionId)
  {
    if (FailingStarts > 0)
    {
      FailingStarts--;
      return null;
    }

    Starts.Add(sessionId);
    return Add(sessionId);
  }

  /// <summary>A client the agent didn't start, as if left by an earlier run.</summary>
  public FakeDesktopClient Add(int sessionId)
  {
    var client = new FakeDesktopClient(_nextId++, sessionId);
    _all.Add(client);
    return client;
  }

  public IReadOnlyList<IDesktopClientProcess> Running() => Alive.ToList();
}

internal sealed class FakeDesktopClient(int id, int sessionId) : IDesktopClientProcess
{
  public int Id { get; } = id;

  public int SessionId { get; } = sessionId;

  public bool HasExited { get; private set; }

  public bool Stopped { get; private set; }

  public void Exit() => HasExited = true;

  public void Stop()
  {
    Stopped = true;
    HasExited = true;
  }

  public void Dispose()
  {
  }
}
