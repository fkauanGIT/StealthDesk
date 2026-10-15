using StealthDesk.Web.Server.ServiceAccounts;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Core.Security;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.AuthorizationLogs;
using StealthDesk.Web.Server.Email;
using StealthDesk.Observability;
using StealthDesk.Web.Server.Dashboard;
using StealthDesk.Web.Server.Devices;
using StealthDesk.Web.Server.Gateway;
using StealthDesk.Contracts.Agents;
using StealthDesk.Web.Server.InstallerKeys;
using StealthDesk.Web.Server.TenantSettings;
using StealthDesk.Web.Server.Invites;
using StealthDesk.Web.Server.UserGroups;
using StealthDesk.Web.Server.Permissions;
using StealthDesk.Web.Server.PersonalAccessTokens;
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
    builder.Services.AddMemoryCache();
    builder.Services.AddSingleton<IPasswordHasher<PersonalAccessTokenRecord>, PasswordHasher<PersonalAccessTokenRecord>>();
    builder.Services.AddScoped<PersonalAccessTokenService>();
    builder.Services.AddSingleton<IPasswordHasher<ServiceAccountCredentialRecord>, PasswordHasher<ServiceAccountCredentialRecord>>();
    builder.Services.AddScoped<ServiceAccountService>();
    builder.Services.Configure<ServiceAccountOptions>(builder.Configuration.GetSection(ServiceAccountOptions.Section));
    builder.Services.Configure<ServiceAccountBootstrapOptions>(builder.Configuration.GetSection(ServiceAccountBootstrapOptions.Section));
    builder.Services.AddSingleton<ServiceAccountCredentialCleanup>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ServiceAccountCredentialCleanup>());
    builder.Services.AddSingleton<IPasswordHasher<InstallerKeyRecord>, PasswordHasher<InstallerKeyRecord>>();
    builder.Services.AddSingleton<InstallerKeyLocks>();
    builder.Services.AddScoped<InstallerKeyService>();
    builder.Services.Configure<InstallerKeyOptions>(builder.Configuration.GetSection(InstallerKeyOptions.Section));
    builder.Services.AddSingleton<InstallerKeyUsageCleanup>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<InstallerKeyUsageCleanup>());
    builder.Services.AddScoped<TenantSettingService>();
    builder.Services.AddOptions<TenantDefaultsOptions>()
      .Bind(builder.Configuration.GetSection(TenantDefaultsOptions.Section))
      .Validate(x => InstanceNames.Validate(x.InstanceName) is null, $"{TenantDefaultsOptions.Section}:{nameof(TenantDefaultsOptions.InstanceName)} isn't a valid agent instance name.")
      .ValidateOnStart();
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
    app.MapUserGroupEndpoints();
    app.MapPermissionAssignmentEndpoints();
    app.MapPersonalAccessTokenEndpoints();
    app.MapServiceAccountEndpoints();
    app.MapInstallerKeyEndpoints();
    app.MapTenantSettingEndpoints();
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
