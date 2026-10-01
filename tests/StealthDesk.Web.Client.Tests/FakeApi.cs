using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace StealthDesk.Web.Client.Tests;

/// <summary>Stands in for the server: every request waits for the response the test chooses.</summary>
internal sealed class FakeApi : HttpMessageHandler
{
  private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public void Respond(HttpStatusCode status) => _response.SetResult(new HttpResponseMessage(status));

  public void RespondWith<T>(T body) =>
    _response.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });

  public void RespondWithHtml() =>
    _response.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("<!DOCTYPE html><html></html>", Encoding.UTF8, "text/html"),
    });

  public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("http://localhost/") };

  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
    _response.Task;
}
