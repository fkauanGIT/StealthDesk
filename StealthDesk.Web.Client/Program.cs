using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using StealthDesk.Contracts;
using StealthDesk.Web.Client;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.AuthorizationLogs;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Terminal;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddStealthDeskAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<AccountApi>();
builder.Services.AddScoped<ServerAuthenticationState>();
builder.Services.AddScoped<AuthenticationStateProvider>(sp => sp.GetRequiredService<ServerAuthenticationState>());
builder.Services.AddScoped<SessionGuard>();
builder.Services.AddScoped<IPasskeyBridge, PasskeyBridge>();
builder.Services.AddScoped<IThemeBridge, ThemeBridge>();
builder.Services.AddScoped<ThemeState>();
builder.Services.AddScoped<AuthorizationLogApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.Users.UserApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.Invites.InviteApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.UserGroups.UserGroupApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.DeviceGroups.DeviceGroupApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.Permissions.PermissionApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.PersonalAccessTokens.PersonalAccessTokenApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.ServiceAccounts.ServiceAccountApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.InstallerKeys.InstallerKeyApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.TenantSettings.TenantSettingApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.ServerAdministration.ServerAdministrationApi>();
builder.Services.AddScoped<StealthDesk.Web.Client.ServerAdministration.IServerAlerts, StealthDesk.Web.Client.ServerAdministration.ServerAlerts>();
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddScoped<DeviceStore>();
builder.Services.AddScoped(sp => new LiveUpdates(
  sp.GetRequiredService<DeviceStore>(),
  new Uri(new Uri(builder.HostEnvironment.BaseAddress), Routes.Dashboard),
  sp.GetRequiredService<ILogger<LiveUpdates>>()));
builder.Services.AddScoped<ILiveUpdates>(sp => sp.GetRequiredService<LiveUpdates>());
builder.Services.AddScoped<IDeviceHub>(sp => sp.GetRequiredService<LiveUpdates>());
builder.Services.AddScoped<ITerminalKeys, TerminalKeys>();
builder.Services.AddScoped<DeviceTerminal>();

await builder.Build().RunAsync();
