using StealthDesk.Agent.Core.Power;
using StealthDesk.Contracts.Devices;
using StealthDesk.Core;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>Records every power change asked of it, without touching the real machine.</summary>
internal sealed class FakePowerControl : IPowerControl
{
  public List<PowerAction> Calls { get; } = [];

  public Outcome<bool> NextResult { get; set; } = Outcome.Success(true);

  public Task<Outcome<bool>> ChangeState(PowerAction action)
  {
    Calls.Add(action);
    return Task.FromResult(NextResult);
  }
}
