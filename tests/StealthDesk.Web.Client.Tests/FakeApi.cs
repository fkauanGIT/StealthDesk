using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace StealthDesk.Web.Client.Tests;

/// <summary>
/// Stands in for the server: each request gets the next response the test chose, waiting for it if there's none yet.
/// </summary>
internal sealed class FakeApi : HttpMessageHandler
{
  private readonly Lock _gate = new();
  private readonly Queue<HttpResponseMessage> _ready = new();
  private TaskCompletionSource<HttpResponseMessage>? _waiting;

  public void Respond(HttpStatusCode status) => Send(new HttpResponseMessage(status));

  public void Respond<T>(HttpStatusCode status, T body) =>
    Send(new HttpResponseMessage(status) { Content = JsonContent.Create(body) });

  /// <summary>Requests the fake received, in order, as "METHOD path?query".</summary>
  public IReadOnlyList<string> Requests => [.. _requests];

  private readonly System.Collections.Concurrent.ConcurrentQueue<string> _requests = new();

  /// <summary>The bodies the fake received, in order; empty for requests without one.</summary>
  public IReadOnlyList<string> Bodies => [.. _bodies];

  private readonly System.Collections.Concurrent.ConcurrentQueue<string> _bodies = new();

  public void RespondWith<T>(T body) =>
    Send(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });

  public void RespondWithHtml() =>
    Send(new HttpResponseMessage(HttpStatusCode.OK)
    {
      Content = new StringContent("<!DOCTYPE html><html></html>", Encoding.UTF8, "text/html"),
    });

  public HttpClient CreateClient() => new(this) { BaseAddress = new Uri("http://localhost/") };

  protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
  {
    _requests.Enqueue($"{request.Method} {request.RequestUri!.PathAndQuery}");
    _bodies.Enqueue(request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult() ?? string.Empty);
    lock (_gate)
    {
      if (_ready.TryDequeue(out var response))
      {
        return Task.FromResult(response);
      }

      _waiting = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
      return _waiting.Task;
    }
  }

  private void Send(HttpResponseMessage response)
  {
    TaskCompletionSource<HttpResponseMessage>? waiting;
    lock (_gate)
    {
      waiting = _waiting;
      _waiting = null;
      if (waiting is null)
      {
        _ready.Enqueue(response);
      }
    }

    waiting?.SetResult(response);
  }
}
