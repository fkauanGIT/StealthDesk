using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StealthDesk.Branding;
using StealthDesk.Contracts.Agents;
using StealthDesk.Contracts.DesktopClients;
using StealthDesk.DesktopClient;
using StealthDesk.Hosting;
using StealthDesk.Ipc;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
{
  return 1;
}

var start = DesktopClientCommandLine.Parse(args);
var sessionId = Process.GetCurrentProcess().SessionId;
using (var identity = WindowsIdentity.GetCurrent())
{
  if (DesktopClientRole.Refusal(identity.IsSystem, sessionId, start, DesktopClientBuild.IsDebug) is { } refusal)
  {
    Tell(refusal, sessionId);
    return 1;
  }
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
// One file per session: each session has its own client.
builder.AddDailyFileLog(Path.Combine(start!.DataFolder, "logs", $"desktop-client-session{sessionId}.log"), keepDays: 7);
builder.Services.AddHostedService<LifetimeLogging>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IDesktopClientEndpoint, DesktopClientEndpoint>();
builder.Services.AddSingleton(provider => ActivatorUtilities.CreateInstance<AgentConnection>(
  provider,
  Pipes.NameFor(start.Instance, DesktopClientBuild.IsDebug),
  new DesktopClientHello(sessionId, typeof(AgentConnection).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")));
builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentConnection>());
using var host = builder.Build();
host.Services.GetRequiredService<ILogger<Program>>()
  .LogInformation("Desktop client of agent instance {Instance} in session {SessionId}.", start.Instance, sessionId);
// Taken before running: RunAsync disposes the host's services when it returns.
var agent = host.Services.GetRequiredService<AgentConnection>();
await host.RunAsync();
return agent.GaveUp ? 1 : 0;

// Whoever opened it by hand sees why it closed; with its output read by another program, that program gets it instead.
// Session 0 has no desktop to show a message on.
static void Tell(string message, int sessionId)
{
  if (Console.IsErrorRedirected || sessionId == 0)
  {
    Console.Error.WriteLine(message);
    return;
  }

  if (OperatingSystem.IsWindowsVersionAtLeast(5))
  {
    PInvoke.MessageBox(HWND.Null, message, Brand.Name, MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION);
  }
}
