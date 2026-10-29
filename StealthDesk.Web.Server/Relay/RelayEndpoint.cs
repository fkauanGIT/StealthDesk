using System.Net.WebSockets;
using System.Security.Claims;
using Microsoft.Extensions.Options;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Relay;

namespace StealthDesk.Web.Server.Relay;

/// <summary>
/// The relay itself: two ends of a session the server issued meet here, each with its own token, and whatever one
/// sends the other receives, as it comes. Neither end learns anything about the other but its messages.
/// </summary>
public static class RelayEndpoint
{
  // Read in pieces: a message is forwarded as it arrives, never held whole.
  private const int BufferBytes = 64 * 1024;

  public static void MapRelay(this IEndpointRouteBuilder endpoints) =>
    // Anonymous: the device's end has no user; the session's tokens decide, and the viewer's end must be its user.
    endpoints.Map(Routes.Relay, RelayAsync).AllowAnonymous();

  private static async Task RelayAsync(
    HttpContext context,
    RelaySessions sessions,
    IOptions<RelayOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<RelaySessions> logger)
  {
    if (!context.WebSockets.IsWebSocketRequest)
    {
      await RefuseAsync(context, StatusCodes.Status400BadRequest, "Relay connections are WebSockets.");
      return;
    }

    var query = context.Request.Query;
    if (!Guid.TryParse(query[RelayConnection.SessionIdQuery], out var sessionId)
      || !Enum.TryParse<RelayRole>(query[RelayConnection.RoleQuery], ignoreCase: true, out var role)
      || !Enum.IsDefined(role)
      || query[RelayConnection.TokenQuery].ToString() is not { Length: > 0 } token)
    {
      await RefuseAsync(context, StatusCodes.Status400BadRequest, "A relay connection names its session, role and token.");
      return;
    }

    if (sessions.Find(sessionId) is not { } session)
    {
      await RefuseAsync(context, StatusCodes.Status404NotFound, "No such session, or it expired.");
      return;
    }

    if (!session.Accepts(role, token))
    {
      logger.LogWarning("A wrong token was given for the {Role} end of relay session {SessionId}.", role, sessionId);
      await RefuseAsync(context, StatusCodes.Status401Unauthorized, "Wrong token.");
      return;
    }

    if (role == RelayRole.Viewer && context.User.FindFirstValue(ClaimTypes.NameIdentifier) != session.ViewerUserId.ToString())
    {
      logger.LogWarning("Relay session {SessionId} was joined as its viewer by someone it wasn't issued to.", sessionId);
      await RefuseAsync(
        context,
        context.User.Identity?.IsAuthenticated == true ? StatusCodes.Status403Forbidden : StatusCodes.Status401Unauthorized,
        "The session was issued to someone else.");
      return;
    }

    if (!session.Join(role))
    {
      await RefuseAsync(context, StatusCodes.Status409Conflict, $"The {role} end of the session already joined.");
      return;
    }

    var other = role == RelayRole.Viewer ? RelayRole.Device : RelayRole.Viewer;
    var partnerTimeout = options.Value.PartnerTimeout;
    try
    {
      // Before accepting: an end left alone gets a plain answer instead of an open socket nobody writes to.
      await session.Arrived(other).WaitAsync(partnerTimeout, lifetime.ApplicationStopping);
    }
    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
    {
      sessions.Forget(session);
      if (session.TryEnd())
      {
        await session.Ended.CancelAsync();
      }

      await RefuseAsync(context, StatusCodes.Status408RequestTimeout, "The other end never joined.");
      return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var own = new RelayEnd(socket);
    session.Connected(role, own);
    RelayEnd partner;
    try
    {
      partner = await session.End(other).WaitAsync(partnerTimeout, session.Ended.Token);
    }
    catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
    {
      await EndAsync(session, sessions, own, null, WebSocketCloseStatus.EndpointUnavailable, "The other end never joined.");
      return;
    }

    logger.LogInformation("The {Role} end joined relay session {SessionId}.", role, sessionId);
    var (status, reason) = await ForwardAsync(own, partner, options.Value.MaxMessageBytes, session.Ended.Token);
    await EndAsync(session, sessions, own, partner, status, reason);
  }

  // What one end sends, to the other, until it leaves; answers how the session should end.
  private static async Task<(WebSocketCloseStatus Status, string Reason)> ForwardAsync(
    RelayEnd own,
    RelayEnd partner,
    int maxMessageBytes,
    CancellationToken ended)
  {
    var buffer = new byte[BufferBytes];
    try
    {
      while (true)
      {
        var size = 0;
        ValueWebSocketReceiveResult received;
        do
        {
          received = await own.Socket.ReceiveAsync(buffer.AsMemory(), ended);
          if (received.MessageType == WebSocketMessageType.Close)
          {
            return (WebSocketCloseStatus.NormalClosure, "The other end left.");
          }

          size += received.Count;
          if (size > maxMessageBytes)
          {
            return (WebSocketCloseStatus.MessageTooBig, $"A message was larger than {maxMessageBytes} bytes.");
          }

          await partner.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, ended);
        }
        while (!received.EndOfMessage);
      }
    }
    catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
    {
      return (WebSocketCloseStatus.EndpointUnavailable, "The other end left.");
    }
  }

  // Whichever end goes first tells the other why and ends the session; each end closes its own socket either way.
  private static async Task EndAsync(
    RelaySession session,
    RelaySessions sessions,
    RelayEnd own,
    RelayEnd? partner,
    WebSocketCloseStatus status,
    string reason)
  {
    sessions.Forget(session);
    var first = session.TryEnd();
    await Task.WhenAll(
      own.CloseAsync(status, status == WebSocketCloseStatus.MessageTooBig ? reason : "The session ended."),
      first && partner is not null ? partner.CloseAsync(status, reason) : Task.CompletedTask);
    if (first)
    {
      await session.Ended.CancelAsync();
    }
  }

  private static async Task RefuseAsync(HttpContext context, int status, string reason)
  {
    context.Response.StatusCode = status;
    await context.Response.WriteAsync(reason);
  }
}
