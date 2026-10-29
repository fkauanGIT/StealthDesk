using StealthDesk.Web.Server;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.ReverseProxy;

var builder = WebApplication.CreateBuilder(args);
builder.AddStealthDeskServer();

var app = builder.Build();
await app.PrepareDatabaseAsync();

// First, so everything after it sees the client instead of the proxy.
await app.UseStealthDeskForwardedHeadersAsync();

if (app.Environment.IsDevelopment())
{
  app.UseWebAssemblyDebugging();
}

// The relay's WebSockets; SignalR's hubs bring their own. Pinged as often as the hubs, so a proxy doesn't cut a
// session whose screen is still.
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<MustChangePasswordMiddleware>();
app.MapStealthDesk();

await app.RunAsync();
