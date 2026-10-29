using System.Net.WebSockets;
using System.Text;
using StealthDesk.Contracts.Relay;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Relay;

namespace StealthDesk.Server.Tests;

/// <summary>
/// The relay remote control streams go through: two ends of a session the server issued, each with its own
/// one-time token, the viewer's end its signed-in user, and whatever one sends reaching the other.
/// </summary>
public class RelayTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  // A receive that never comes fails the test instead of holding it forever.
  private static CancellationToken Soon => CancellationTokenSource.CreateLinkedTokenSource(Cancel, new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).Token;

  [Fact]
  public async Task TwoEnds_ExchangeMessagesBothWays_InOrder_EvenLargerThanABuffer()
  {
    using var server = ServerHost.InMemory();
    var (tickets, cookie) = await IssueAsync(server);

    var (viewer, device) = await JoinBothAsync(server, tickets, cookie);
    var frame = Enumerable.Range(0, 300_000).Select(x => (byte)x).ToArray();
    await device.SendAsync(frame, WebSocketMessageType.Binary, endOfMessage: true, Cancel);
    foreach (var key in new[] { "a", "b", "c" })
    {
      await viewer.SendAsync(Encoding.UTF8.GetBytes(key), WebSocketMessageType.Text, endOfMessage: true, Cancel);
    }

    var (receivedFrame, frameType) = await ReceiveAsync(viewer);
    Assert.Equal(WebSocketMessageType.Binary, frameType);
    Assert.Equal(frame, receivedFrame);
    foreach (var key in new[] { "a", "b", "c" })
    {
      var (text, type) = await ReceiveAsync(device);
      Assert.Equal((key, WebSocketMessageType.Text), (Encoding.UTF8.GetString(text), type));
    }
  }

  [Fact]
  public async Task AWrongToken_OrAnUnknownSession_IsRefused()
  {
    using var server = ServerHost.InMemory();
    var (tickets, _) = await IssueAsync(server);

    var wrong = await RefusedAsync(JoinAsync(server, tickets, RelayRole.Device, token: tickets.ViewerToken));
    var unknown = await RefusedAsync(JoinAsync(server, tickets with { SessionId = Guid.NewGuid() }, RelayRole.Device));

    Assert.Equal(401, wrong);
    Assert.Equal(404, unknown);
  }

  [Fact]
  public async Task ATokenIsGoodOnce_WhileTheSessionRuns_AndAfter()
  {
    using var server = ServerHost.InMemory();
    var (tickets, cookie) = await IssueAsync(server);
    var (viewer, device) = await JoinBothAsync(server, tickets, cookie);

    var again = await RefusedAsync(JoinAsync(server, tickets, RelayRole.Device));
    await device.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", Cancel);
    await ReceiveCloseAsync(viewer);
    var after = await RefusedAsync(JoinAsync(server, tickets, RelayRole.Device));

    Assert.Equal(409, again);
    Assert.Equal(404, after);
  }

  [Fact]
  public async Task TheViewersEnd_MustBeTheUserTheSessionWasIssuedTo()
  {
    using var server = ServerHost.InMemory();
    var (tickets, _) = await IssueAsync(server);
    await TestAccounts.CreateUserAsync(server, "someone-else@example.com", confirmed: true);
    var otherCookie = await TestAccounts.SignInForCookieAsync(server, "someone-else@example.com");

    var anonymous = await RefusedAsync(JoinAsync(server, tickets, RelayRole.Viewer));
    var someoneElse = await RefusedAsync(JoinAsync(server, tickets, RelayRole.Viewer, otherCookie));

    Assert.Equal(401, anonymous);
    Assert.Equal(403, someoneElse);
  }

  [Fact]
  public async Task AnEndLeftAlone_GivesUpAfterThePartnerTimeout_AndTheSessionIsGone()
  {
    using var server = ServerHost.InMemory().With("Relay:PartnerTimeout", "00:00:01");
    var (tickets, cookie) = await IssueAsync(server);

    var watch = System.Diagnostics.Stopwatch.StartNew();
    var alone = await RefusedAsync(JoinAsync(server, tickets, RelayRole.Device));
    var late = await RefusedAsync(JoinAsync(server, tickets, RelayRole.Viewer, cookie));

    Assert.Equal(408, alone);
    Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(15));
    Assert.Equal(404, late);
  }

  [Fact]
  public async Task TicketsNobodyUses_Expire()
  {
    using var server = ServerHost.InMemory().With("Relay:TicketLifetime", "00:00:01");
    var (tickets, _) = await IssueAsync(server);

    await Task.Delay(TimeSpan.FromSeconds(1.5), Cancel);

    Assert.Equal(404, await RefusedAsync(JoinAsync(server, tickets, RelayRole.Device)));
  }

  [Fact]
  public async Task OneEndLeaving_ClosesTheOther_WithWhy()
  {
    using var server = ServerHost.InMemory();
    var (tickets, cookie) = await IssueAsync(server);
    var (viewer, device) = await JoinBothAsync(server, tickets, cookie);

    await device.CloseAsync(WebSocketCloseStatus.NormalClosure, "Session over", Cancel);
    var closed = await ReceiveCloseAsync(viewer);

    Assert.Equal("The other end left.", closed.CloseStatusDescription);
    Assert.Equal(WebSocketCloseStatus.NormalClosure, closed.CloseStatus);
  }

  [Fact]
  public async Task AMessageAboveTheLimit_EndsTheSession_ForBothEnds()
  {
    using var server = ServerHost.InMemory().With("Relay:MaxMessageBytes", "100000");
    var (tickets, cookie) = await IssueAsync(server);
    var (viewer, device) = await JoinBothAsync(server, tickets, cookie);

    await device.SendAsync(new byte[150_000], WebSocketMessageType.Binary, endOfMessage: true, Cancel);
    var toViewer = await ReceiveCloseAsync(viewer);
    var toDevice = await ReceiveCloseAsync(device);

    Assert.Equal(WebSocketCloseStatus.MessageTooBig, toViewer.CloseStatus);
    Assert.Equal(WebSocketCloseStatus.MessageTooBig, toDevice.CloseStatus);
    Assert.Equal("A message was larger than 100000 bytes.", toDevice.CloseStatusDescription);
  }

  [Fact]
  public async Task APlainRequest_IsRefused()
  {
    using var server = ServerHost.InMemory();
    using var client = server.CreateClient();

    using var response = await client.GetAsync($"{StealthDesk.Contracts.Routes.Relay}?sessionId={Guid.NewGuid()}&role=Device&token=x", Cancel);

    Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
  }

  private static async Task<(RelayTickets Tickets, string Cookie)> IssueAsync(ServerHost server)
  {
    var viewer = await TestAccounts.CreateUserAsync(server, "viewer@example.com", confirmed: true);
    var cookie = await TestAccounts.SignInForCookieAsync(server, "viewer@example.com");
    return (server.Services.GetRequiredService<IRelaySessions>().Issue(viewer.Id), cookie);
  }

  // Together: neither end is let in before the other arrives.
  private static async Task<(WebSocket Viewer, WebSocket Device)> JoinBothAsync(ServerHost server, RelayTickets tickets, string cookie)
  {
    var viewer = JoinAsync(server, tickets, RelayRole.Viewer, cookie);
    var device = JoinAsync(server, tickets, RelayRole.Device);
    return (await viewer, await device);
  }

  private static Task<WebSocket> JoinAsync(ServerHost server, RelayTickets tickets, RelayRole role, string? cookie = null, string? token = null)
  {
    var client = server.Server.CreateWebSocketClient();
    if (cookie is not null)
    {
      client.ConfigureRequest = request => request.Headers["Cookie"] = cookie;
    }

    var uri = RelayConnection.For(server.Server.BaseAddress, tickets.SessionId, role, token ?? (role == RelayRole.Viewer ? tickets.ViewerToken : tickets.DeviceToken));
    return client.ConnectAsync(uri, Cancel);
  }

  // The test server reports a refused handshake as "Incomplete handshake, status code: N".
  private static async Task<int> RefusedAsync(Task<WebSocket> joining)
  {
    var refused = await Assert.ThrowsAnyAsync<Exception>(() => joining);
    var code = refused.Message[(refused.Message.LastIndexOf(' ') + 1)..].TrimEnd('.');
    return int.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
  }

  private static async Task<(byte[] Data, WebSocketMessageType Type)> ReceiveAsync(WebSocket socket)
  {
    using var data = new MemoryStream();
    var buffer = new byte[32 * 1024];
    WebSocketReceiveResult received;
    do
    {
      received = await socket.ReceiveAsync(buffer, Soon);
      data.Write(buffer, 0, received.Count);
    }
    while (!received.EndOfMessage);

    return (data.ToArray(), received.MessageType);
  }

  private static async Task<WebSocketReceiveResult> ReceiveCloseAsync(WebSocket socket)
  {
    var buffer = new byte[32 * 1024];
    while (true)
    {
      var received = await socket.ReceiveAsync(buffer, Soon);
      if (received.MessageType == WebSocketMessageType.Close)
      {
        return received;
      }
    }
  }
}
