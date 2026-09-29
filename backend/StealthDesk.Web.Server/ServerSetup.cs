using StealthDesk.Core.Security;
using StealthDesk.Observability;
using StealthDesk.Web.Server.Devices;
using StealthDesk.Web.Server.Gateway;

namespace StealthDesk.Web.Server;

public static class ServerSetup
{
  public static WebApplicationBuilder AddStealthDeskServer(this WebApplicationBuilder builder)
  {
    builder.AddObservability(ServiceNames.Server);
    builder.AddStealthDeskDatabase();

    builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.Section));
    builder.Services.AddSignalR().AddMessagePackProtocol();

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IMessageSigner, MessageSigner>();
    builder.Services.AddScoped<IDeviceRegistry, DeviceRegistry>();
    builder.Services.AddScoped<ReportProcessor>();

    return builder;
  }

  public static WebApplication MapStealthDesk(this WebApplication app)
  {
    app.MapHealthEndpoints();
    app.MapDeviceEndpoints();
    app.MapGet(Routes.ServerVersion, () => typeof(ServerSetup).Assembly.GetName().Version?.ToString() ?? "unknown");
    app.MapHub<AgentGatewayHub>(Routes.AgentGateway);
    app.MapFallbackToFile("index.html");
    return app;
  }
}
