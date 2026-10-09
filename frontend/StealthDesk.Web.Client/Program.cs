using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using StealthDesk.Contracts;
using StealthDesk.Web.Client;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.AuthorizationLogs;
using StealthDesk.Web.Client.Devices;

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
builder.Services.AddScoped<AuthorizationLogApi>();

builder.Services.AddScoped<DeviceStore>();
builder.Services.AddScoped<ILiveUpdates>(sp => new LiveUpdates(
  sp.GetRequiredService<DeviceStore>(),
  new Uri(new Uri(builder.HostEnvironment.BaseAddress), Routes.Dashboard),
  sp.GetRequiredService<ILogger<LiveUpdates>>()));

await builder.Build().RunAsync();
