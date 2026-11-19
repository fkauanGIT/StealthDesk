using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Contracts.Realtime;

/// <summary>Calls an agent makes on the server over the realtime connection.</summary>
public interface IAgentGateway
{
  /// <summary>Stores the signed report and marks the device online.</summary>
  Task<GatewayReply<ReportReceipt>> SubmitReport(SignedEnvelope<DeviceReport> envelope);

  /// <summary>
  /// Output of a terminal session, for the browser that opened it. The server delivers it only to that browser, and
  /// only when the session was opened on this agent's device; anything else is dropped.
  /// </summary>
  Task SendTerminalOutput(TerminalOutput output);

  /// <summary>
  /// A file's bytes, following an accepted <see cref="IAgentCallbacks.StartFileDownload"/>, matched to it by
  /// <paramref name="transferId"/>. Chunks for a transfer the server no longer knows about (abandoned, or never
  /// started) are read to completion and dropped.
  /// </summary>
  Task SendFileChunks(Guid transferId, IAsyncEnumerable<byte[]> chunks);
}

/// <summary>
/// Calls the server makes on a connected agent, each answered with a <see cref="GatewayReply{T}"/>. The cancellation
/// token isn't sent: it lets the server stop waiting for an answer.
/// </summary>
public interface IAgentCallbacks
{
  /// <summary>The version of the agent answering: a harmless call that proves the server can reach it.</summary>
  Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken);

  /// <summary>
  /// Starts a terminal session with this id, which sends its banner and first prompt through
  /// <see cref="IAgentGateway.SendTerminalOutput"/> before the answer. A session already running is left as it is.
  /// </summary>
  Task<GatewayReply<Guid>> OpenTerminal(Guid terminalId, CancellationToken cancellationToken);

  /// <summary>A line typed in the terminal: the answer to a question waiting, or the next command.</summary>
  Task<GatewayReply<TerminalInputReceipt>> SendTerminalInput(Guid terminalId, string line, CancellationToken cancellationToken);

  /// <summary>Completions for what is typed in the session; refused while a command runs.</summary>
  Task<GatewayReply<TerminalCompletions>> GetTerminalCompletions(Guid terminalId, TerminalCompletionRequest request, CancellationToken cancellationToken);

  /// <summary>Ends the session, stopping what it runs; its last output says so. Refused for one that isn't running.</summary>
  Task<GatewayReply<Guid>> CloseTerminal(Guid terminalId, CancellationToken cancellationToken);

  /// <summary>The Windows sessions with a desktop client connected to this agent, which a remote control session can show.</summary>
  Task<GatewayReply<IReadOnlyList<RemoteDesktopSession>>> GetDesktopSessions(CancellationToken cancellationToken);

  /// <summary>
  /// Has the desktop client of the request's Windows session join the relay session. Answered with the relay session's
  /// id once the client took the request, before it joins: the other end joins only after the answer.
  /// </summary>
  Task<GatewayReply<Guid>> StartRemoteControl(RemoteControlRequest request, CancellationToken cancellationToken);

  /// <summary>One still picture of the device's current desktop, without starting a remote control session.</summary>
  Task<GatewayReply<DesktopPreview>> GetDesktopPreview(CancellationToken cancellationToken);

  /// <summary>A directory's immediate contents, for the file manager. The device's own drive when <paramref name="path"/> is empty.</summary>
  Task<GatewayReply<DirectoryListing>> ListDirectory(string path, CancellationToken cancellationToken);

  /// <summary>
  /// Starts downloading whatever is at <paramref name="path"/>: a file as-is, or a folder zipped by the agent first.
  /// The answer carries its size and name (the folder's own name with ".zip" added); its bytes follow separately
  /// through <see cref="IAgentGateway.SendFileChunks"/>, matched to this call by <paramref name="transferId"/>,
  /// which the server generates before asking. A path that isn't a file or folder the agent can read, or a folder
  /// that couldn't be zipped, answers with <see cref="FileDownloadInfo.Exists"/> false, and no bytes follow.
  /// </summary>
  Task<GatewayReply<FileDownloadInfo>> StartFileDownload(Guid transferId, string path, CancellationToken cancellationToken);
}
