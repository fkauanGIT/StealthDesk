using StealthDesk.Agent.Core.Wake;
using StealthDesk.Core;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>Records every MAC address it was asked to wake, without touching the real network.</summary>
internal sealed class FakeWakeSender : IWakeSender
{
  public List<string> Calls { get; } = [];

  public Outcome<bool> NextResult { get; set; } = Outcome.Success(true);

  public Task<Outcome<bool>> SendMagicPacket(string macAddress)
  {
    Calls.Add(macAddress);
    return Task.FromResult(NextResult);
  }
}
