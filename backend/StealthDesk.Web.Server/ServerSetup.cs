using StealthDesk.Core.Security;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.Email;
using StealthDesk.Observability;
using StealthDesk.Web.Server.Dashboard;
using StealthDesk.Web.Server.Devices;
using StealthDesk.Web.Server.Gateway;

namespace StealthDesk.Web.Server;

public static class ServerSetup
{
  public static WebApplicationBuilder AddStealthDeskServer(this WebApplicationBuilder builder)
  {
    builder.AddObservability(ServiceNames.Server);
    builder.AddStealthDeskDatabase();
    builder.AddStealthDeskAccounts();
    builder.AddStealthDeskEmail();

    builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection(GatewayOptions.Section));
    builder.Services.AddSignalR().AddMessagePackProtocol();

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IMessageSigner, MessageSigner>();
    builder.Services.AddScoped<IDeviceRegistry, DeviceRegistry>();
    builder.Services.AddScoped<ReportProcessor>();
    builder.Services.AddSingleton<IDeviceNotifier, DeviceNotifier>();

    return builder;
  }

  public static WebApplication MapStealthDesk(this WebApplication app)
  {
    // The web client's files, under their own names and the fingerprinted names its import map points to.
    app.MapStaticAssets();
    app.MapHealthEndpoints();
    app.MapAccountEndpoints();
    app.MapManageEndpoints();
    app.MapTwoFactorEndpoints();
    app.MapDeviceEndpoints();
    app.MapGet(Routes.ServerVersion, () => typeof(ServerSetup).Assembly.GetName().Version?.ToString() ?? "unknown");
    app.MapHub<AgentGatewayHub>(Routes.AgentGateway);
    app.MapHub<DashboardHub>(Routes.Dashboard);

    // Any other path is a page of the web app, but an unknown API or hub path is an error, not a page.
    app.MapFallback("/api/{**path}", () => Results.NotFound());
    app.MapFallback("/hubs/{**path}", () => Results.NotFound());
    app.MapFallbackToFile("index.html");
    return app;
  }
}
