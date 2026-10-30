using System.IO.Pipes;
using System.Runtime.Versioning;
using StealthDesk.Branding;

namespace StealthDesk.Ipc;

/// <summary>Creating and opening the pipe between an agent and its desktop clients. The agent serves it.</summary>
public static class Pipes
{
  /// <summary>
  /// The pipe an agent instance serves. A debug build has its own, so a debug agent and client never meet an
  /// installed one.
  /// </summary>
  public static string NameFor(string instance, bool isDebugBuild) =>
    isDebugBuild ? $"{Brand.Key}-debug-{instance}" : $"{Brand.Key}-{instance}";

  /// <summary>A new server end of <paramref name="name"/>, with the default access of the process creating it.</summary>
  public static NamedPipeServerStream CreateServer(string name) =>
    new(name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

  /// <summary>A new server end of <paramref name="name"/> that only <paramref name="security"/> lets in.</summary>
  [SupportedOSPlatform("windows")]
  public static NamedPipeServerStream CreateServer(string name, PipeSecurity security) =>
    NamedPipeServerStreamAcl.Create(
      name,
      PipeDirection.InOut,
      NamedPipeServerStream.MaxAllowedServerInstances,
      PipeTransmissionMode.Byte,
      PipeOptions.Asynchronous,
      inBufferSize: 0,
      outBufferSize: 0,
      security);

  /// <summary>
  /// The client end of <paramref name="name"/> on this machine, once a server takes it; throws
  /// <see cref="TimeoutException"/> when none does within <paramref name="timeLimit"/>.
  /// </summary>
  public static async Task<NamedPipeClientStream> ConnectAsync(string name, TimeSpan timeLimit, CancellationToken cancellationToken)
  {
    var stream = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
    try
    {
      await stream.ConnectAsync((int)timeLimit.TotalMilliseconds, cancellationToken);
      return stream;
    }
    catch
    {
      await stream.DisposeAsync();
      throw;
    }
  }

  /// <summary>Starts exchanging messages on a connected <paramref name="stream"/>, which the connection then owns.</summary>
  public static PipeConnection<TRemote> Open<TRemote>(PipeStream stream, object localEndpoint)
    where TRemote : class => new(stream, localEndpoint);
}
