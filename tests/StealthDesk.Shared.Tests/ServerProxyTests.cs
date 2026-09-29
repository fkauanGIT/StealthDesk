using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Realtime;

namespace StealthDesk.Shared.Tests;

public class ServerProxyTests
{
  public interface IBadGateway
  {
    int Synchronous();
  }

  [Fact]
  public void Calls_FailClearlyWhenTheChannelIsNotOpen()
  {
    var proxy = ServerProxy<IBadGateway>.Create(() => throw new InvalidOperationException("The channel is not open."));

    var error = Assert.Throws<InvalidOperationException>(() => proxy.Synchronous());
    Assert.Contains("not open", error.Message);
  }

  [Fact]
  public void Calls_RejectMethodsThatDontReturnATask()
  {
    var connection = new HubConnectionBuilder().WithUrl("http://localhost/unused").Build();
    var proxy = ServerProxy<IBadGateway>.Create(() => connection);

    Assert.Throws<NotSupportedException>(() => proxy.Synchronous());
  }
}
