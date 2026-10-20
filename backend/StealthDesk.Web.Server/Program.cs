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

app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<MustChangePasswordMiddleware>();
app.MapStealthDesk();

await app.RunAsync();
