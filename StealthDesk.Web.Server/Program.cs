using StealthDesk.Libraries.Shared.Constants;
using StealthDesk.Web.Server.Hubs;
using StealthDesk.Web.Server.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.AddStealthDeskServer();

var app = builder.Build();

if (app.Configuration.GetValue<bool>("UseInMemoryDatabase"))
{
  await app.EnsureDatabaseCreated();
}
else
{
  await app.ApplyMigrations();
}

await app.SeedDefaultTenant();

if (app.Environment.IsDevelopment())
{
  app.UseWebAssemblyDebugging();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseOutputCache();

app.MapDefaultEndpoints();
app.MapControllers();
app.MapHub<AgentHub>(AppConstants.AgentHubPath);
app.MapFallbackToFile("index.html");

app.Run();
