using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Contracts.Realtime;
using StealthDesk.Realtime;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>A realtime channel with a scripted server: it records every report and answers as told.</summary>
internal sealed class FakeChannel : IRealtimeChannel<IAgentGateway>, IAgentGateway
{
  private readonly Queue<bool> _openResults = new();

  public HubConnectionState State { get; set; } = HubConnectionState.Connected;
  public IAgentGateway Server => this;
  public List<SignedEnvelope<DeviceReport>> Received { get; } = [];
  public List<Uri> OpenedEndpoints { get; } = [];
  public int OpenAttempts { get; private set; }

  /// <summary>What the server answers; by default it accepts and echoes the device's identity.</summary>
  public Func<DeviceReport, GatewayReply<ReportReceipt>> Answer { get; set; } =
    report => GatewayReply.Accept(new ReportReceipt(report.DeviceId, report.TenantId, DateTimeOffset.UtcNow));

  public event Func<Task>? Restored;
  public event Func<Exception?, Task>? Lost;

  /// <summary>The next <see cref="OpenAsync"/> calls fail this many times before succeeding.</summary>
  public void FailOpens(int times)
  {
    for (var i = 0; i < times; i++)
    {
      _openResults.Enqueue(false);
    }
  }

  public Task<bool> OpenAsync(Uri endpoint, Action<HttpConnectionOptions>? configure = null, CancellationToken cancellationToken = default)
  {
    OpenAttempts++;
    OpenedEndpoints.Add(endpoint);
    var opened = !_openResults.TryDequeue(out var result) || result;
    State = opened ? HubConnectionState.Connected : HubConnectionState.Disconnected;
    return Task.FromResult(opened);
  }

  public Task<GatewayReply<ReportReceipt>> SubmitReport(SignedEnvelope<DeviceReport> envelope)
  {
    lock (Received)
    {
      Received.Add(envelope);
    }

    return Task.FromResult(Answer(envelope.Payload));
  }

  public Task SimulateRestoredAsync() => Restored?.Invoke() ?? Task.CompletedTask;

  public Task SimulateLostAsync() => Lost?.Invoke(null) ?? Task.CompletedTask;

  public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
