using System.Net;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

public class RoutingTests
{
  [Theory]
  [InlineData("/api/v1/does-not-exist")]
  [InlineData("/api/does-not-exist")]
  [InlineData("/hubs/does-not-exist")]
  public async Task UnknownApiOrHubPath_IsNotFound(string path)
  {
    using var server = ServerHost.InMemory();
    using var client = server.CreateClient();

    var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
  }

  [Fact]
  public async Task UnknownPagePath_ServesTheWebApp()
  {
    using var server = ServerHost.InMemory();
    using var client = server.CreateClient();

    // A page of the web app opened directly; its route only exists in the browser.
    var response = await client.GetAsync("/devices/some-device", TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
  }
}
