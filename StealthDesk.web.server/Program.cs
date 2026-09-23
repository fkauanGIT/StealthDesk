using StealthDesk.Web.Server.Startup;

var builder = WebApplication.CreateBuilder(args);

builder.AddStealthDeskServer();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
  app.UseWebAssemblyDebugging();
}

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.UseOutputCache();

app.MapDefaultEndpoints();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
