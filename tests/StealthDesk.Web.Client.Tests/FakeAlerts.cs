using StealthDesk.Contracts.ServerSettings;
using StealthDesk.Web.Client.ServerAdministration;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The server's alert, as a test sets it.</summary>
internal sealed class FakeAlerts : IServerAlerts
{
  public ServerAlert? Current { get; set; }

  public Task<ServerAlert?> CurrentAsync() => Task.FromResult(Current);
}
