namespace StealthDesk.Agent.Core.DesktopClients;

/// <summary>A Windows session with a desktop a desktop client can run in.</summary>
/// <param name="Id">The session's id.</param>
/// <param name="UserName">Who is logged on, or null at the sign-in screen.</param>
/// <param name="IsConsole">The machine's own screen, as opposed to a remote desktop session.</param>
public sealed record DesktopSession(int Id, string? UserName, bool IsConsole);

/// <summary>The sessions that should each have a desktop client.</summary>
public interface IDesktopSessions
{
  /// <summary>The console session, whether or not anyone is logged on, and every active remote session.</summary>
  IReadOnlyList<DesktopSession> Current();
}

/// <summary>A desktop client process.</summary>
public interface IDesktopClientProcess : IDisposable
{
  int Id { get; }

  int SessionId { get; }

  bool HasExited { get; }

  void Stop();
}

/// <summary>Starts desktop clients and finds the ones running.</summary>
public interface IDesktopClientLauncher
{
  /// <summary>Null when the desktop client is installed where it is started from; otherwise why it can't be started.</summary>
  string? Missing();

  /// <summary>A desktop client started in <paramref name="sessionId"/>, or null when it couldn't be.</summary>
  IDesktopClientProcess? Start(int sessionId);

  /// <summary>Every desktop client of this agent running now, whoever started it.</summary>
  IReadOnlyList<IDesktopClientProcess> Running();
}
