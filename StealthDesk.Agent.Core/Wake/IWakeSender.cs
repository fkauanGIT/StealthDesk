using StealthDesk.Core;

namespace StealthDesk.Agent.Core.Wake;

/// <summary>Broadcasts a Wake-on-LAN magic packet on this agent's own network, for some other device's MAC address.</summary>
public interface IWakeSender
{
  Task<Outcome<bool>> SendMagicPacket(string macAddress);
}
