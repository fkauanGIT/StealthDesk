using StealthDesk.Contracts.Devices;
using StealthDesk.Core;

namespace StealthDesk.Agent.Core.Power;

/// <summary>Restarts or shuts down the machine the agent runs on. Not registered by <see cref="AgentHostSetup"/>: the
/// platform supplies its own, the same way as <see cref="Inventory.IDeviceInventory"/>.</summary>
public interface IPowerControl
{
  Task<Outcome<bool>> ChangeState(PowerAction action);
}
