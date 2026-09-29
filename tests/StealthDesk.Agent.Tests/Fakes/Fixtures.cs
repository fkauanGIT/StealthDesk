using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StealthDesk.Agent.Core.Inventory;
using StealthDesk.Agent.Core.Settings;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>An agent instance folder under the temp directory, removed at the end of the test.</summary>
internal sealed class TempAgentFolder : IDisposable
{
  public TempAgentFolder()
  {
    Root = Path.Combine(Path.GetTempPath(), $"stealthdesk-tests-{Guid.NewGuid():N}");
    Paths = new AgentPaths(instanceName: null, isDebugBuild: false, dataRoot: Root);
  }

  public string Root { get; }
  public AgentPaths Paths { get; }

  /// <summary>A store that starts from what is saved on disk, like the agent does after a restart.</summary>
  public SettingsStore OpenStore(AgentSettings? defaults = null)
  {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["Agent:ServerUrl"] = (defaults?.ServerUrl ?? new Uri("http://server.test")).ToString(),
      })
      .AddJsonFile(Paths.SettingsFile, optional: true)
      .Build();

    var settings = configuration.GetSection(AgentSettings.Section).Get<AgentSettings>() ?? new AgentSettings();
    return new SettingsStore(Options.Create(settings), Paths);
  }

  public void Dispose()
  {
    if (Directory.Exists(Root))
    {
      Directory.Delete(Root, recursive: true);
    }
  }
}

internal sealed class FakeInventory : IDeviceInventory
{
  public Task<DeviceReport> CaptureAsync(CancellationToken cancellationToken = default) =>
    Task.FromResult(new DeviceReport { MachineName = "TEST-PC", Platform = DevicePlatform.Windows, CpuCores = 4 });
}

/// <summary>A fake clock that says when a timer has been created, so a test only moves time once someone is waiting.</summary>
internal sealed class WatchedClock() : FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero))
{
  private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public Task TimerCreated => _timerCreated.Task;

  public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
  {
    var timer = base.CreateTimer(callback, state, dueTime, period);
    _timerCreated.TrySetResult();
    return timer;
  }
}

internal static class Wait
{
  public static async Task<bool> UntilAsync(Func<bool> condition)
  {
    var deadline = DateTime.UtcNow.AddSeconds(5);
    while (DateTime.UtcNow < deadline)
    {
      if (condition())
      {
        return true;
      }

      await Task.Delay(20);
    }

    return false;
  }
}
