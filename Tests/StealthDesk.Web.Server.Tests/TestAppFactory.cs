using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StealthDesk.Web.Server.Tests;

/// <param name="databaseName">
/// In-memory database name. Pass the same name to two factories to simulate a server restart.
/// </param>
public class TestAppFactory(string? databaseName = null) : WebApplicationFactory<Program>
{
  private readonly string _databaseName = databaseName ?? Guid.NewGuid().ToString("N");

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseEnvironment("Development");
    builder.UseSetting("UseInMemoryDatabase", "true");
    builder.UseSetting("InMemoryDatabaseName", _databaseName);
  }
}
