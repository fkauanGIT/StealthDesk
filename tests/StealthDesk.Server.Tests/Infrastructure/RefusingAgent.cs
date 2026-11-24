using StealthDesk.Contracts.DesktopClients;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Contracts.Terminal;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>An agent that refuses every call the server makes; tests override the calls they answer.</summary>
public class RefusingAgent : IAgentCallbacks
{
  private const string NoAnswers = "This test agent has no answers.";

  public virtual Task<GatewayReply<string>> GetAgentVersion(CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<string>(NoAnswers));

  public virtual Task<GatewayReply<Guid>> OpenTerminal(Guid terminalId, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<Guid>(NoAnswers));

  public virtual Task<GatewayReply<TerminalInputReceipt>> SendTerminalInput(Guid terminalId, string line, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<TerminalInputReceipt>(NoAnswers));

  public virtual Task<GatewayReply<TerminalCompletions>> GetTerminalCompletions(Guid terminalId, TerminalCompletionRequest request, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<TerminalCompletions>(NoAnswers));

  public virtual Task<GatewayReply<Guid>> CloseTerminal(Guid terminalId, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<Guid>(NoAnswers));

  public virtual Task<GatewayReply<IReadOnlyList<RemoteDesktopSession>>> GetDesktopSessions(CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<IReadOnlyList<RemoteDesktopSession>>(NoAnswers));

  public virtual Task<GatewayReply<Guid>> StartRemoteControl(RemoteControlRequest request, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<Guid>(NoAnswers));

  public virtual Task<GatewayReply<DesktopPreview>> GetDesktopPreview(CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<DesktopPreview>(NoAnswers));

  public virtual Task<GatewayReply<DirectoryListing>> ListDirectory(string path, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<DirectoryListing>(NoAnswers));

  public virtual Task<GatewayReply<FileDownloadInfo>> StartFileDownload(Guid transferId, string path, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<FileDownloadInfo>(NoAnswers));

  public virtual Task<GatewayReply<Guid>> StartFileUpload(Guid transferId, string directoryPath, string fileName, bool overwrite, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<Guid>(NoAnswers));

  public virtual Task<GatewayReply<string>> CreateDirectory(string directoryPath, string name, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<string>(NoAnswers));

  public virtual Task<GatewayReply<bool>> DeleteEntry(string path, CancellationToken cancellationToken) =>
    Task.FromResult(GatewayReply.Refuse<bool>(NoAnswers));
}
