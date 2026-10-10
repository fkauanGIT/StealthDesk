using StealthDesk.Core.Security;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.AuthorizationLogs;
using StealthDesk.Web.Server.Email;
using StealthDesk.Observability;
using StealthDesk.Web.Server.Dashboard;
using StealthDesk.Web.Server.Devices;
using StealthDesk.Web.Server.Gateway;
using StealthDesk.Web.Server.Invites;
using StealthDesk.Web.Server.Users;

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
    builder.Services.AddScoped<IDeviceAccess, DeviceAccess>();
    builder.Services.AddScoped<ReportProcessor>();
    builder.Services.AddSingleton<IDeviceNotifier, DeviceNotifier>();

    builder.Services.Configure<AuthorizationLogOptions>(builder.Configuration.GetSection(AuthorizationLogOptions.Section));
    builder.Services.AddSingleton<IAuthorizationChangeFactory, AuthorizationChangeFactory>();
    builder.Services.AddSingleton<AuthorizationLogCleanup>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AuthorizationLogCleanup>());

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
    app.MapPasskeyEndpoints();
    app.MapExternalLoginEndpoints();
    app.MapDeviceEndpoints();
    app.MapAuthorizationLogEndpoints();
    app.MapUserEndpoints();
    app.MapInviteEndpoints();
    app.MapGet(Routes.ServerVersion, () => typeof(ServerSetup).Assembly.GetName().Version?.ToString() ?? "unknown").AllowAnonymous();
    app.MapHub<AgentGatewayHub>(Routes.AgentGateway);
    app.MapHub<DashboardHub>(Routes.Dashboard);

    // Any other path is a page of the web app, but an unknown API or hub path is an error, not a page.
    app.MapFallback("/api/{**path}", () => Results.NotFound()).AllowAnonymous();
    app.MapFallback("/hubs/{**path}", () => Results.NotFound()).AllowAnonymous();
    app.MapFallbackToFile("index.html");
    return app;
  }
}
