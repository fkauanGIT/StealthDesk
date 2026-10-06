using StealthDesk.Web.Server;
using StealthDesk.Web.Server.Accounts;

var builder = WebApplication.CreateBuilder(args);
builder.AddStealthDeskServer();

var app = builder.Build();
await app.PrepareDatabaseAsync();

if (app.Environment.IsDevelopment())
{
  app.UseWebAssemblyDebugging();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<MustChangePasswordMiddleware>();
app.MapStealthDesk();

await app.RunAsync();
