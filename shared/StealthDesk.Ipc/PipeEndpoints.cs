using PolyType;
using StealthDesk.Contracts.DesktopClients;
using StreamJsonRpc;

namespace StealthDesk.Ipc;

/// <summary>What the agent answers on the pipe: the desktop client calls these.</summary>
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IAgentEndpoint
{
  /// <summary>The client is connected and says who it is.</summary>
  Task ClientReady(DesktopClientHello hello, CancellationToken cancellationToken);
}

/// <summary>What a desktop client answers on the pipe: the agent calls these.</summary>
[JsonRpcContract]
[GenerateShape(IncludeMethods = MethodShapeFlags.PublicInstance)]
public partial interface IDesktopClientEndpoint
{
  Task<DesktopSessionInfo> GetSessionInfo(CancellationToken cancellationToken);

  /// <summary>The client closes; <paramref name="reason"/> goes to its log.</summary>
  Task Shutdown(string reason, CancellationToken cancellationToken);

  /// <summary>
  /// The client joins the relay session as the device's end, in the background: it returns once the request is taken,
  /// and throws with why when it isn't.
  /// </summary>
  Task StartRemoteControl(DesktopRemoteControlStart request, CancellationToken cancellationToken);
}
