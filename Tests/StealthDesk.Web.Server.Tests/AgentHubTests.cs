using System.Net;
using StealthDesk.Libraries.Shared.Constants;

namespace StealthDesk.Web.Server.Tests;

public class AgentHubTests
{
  [Fact]
  public async Task Negotiate_ReturnsOk()
  {
    using var factory = new TestAppFactory();
    using var client = factory.CreateClient();

    var response = await client.PostAsync(
      $"{AppConstants.AgentHubPath}/negotiate?negotiateVersion=1",
      content: null,
      TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }
}
