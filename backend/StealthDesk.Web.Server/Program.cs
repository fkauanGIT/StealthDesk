using StealthDesk.Web.Server;

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
app.MapStealthDesk();

await app.RunAsync();
