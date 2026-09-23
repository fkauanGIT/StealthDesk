using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StealthDesk.Web.Server.Tests;

public class TestAppFactory : WebApplicationFactory<Program>
{
  private readonly string _databaseName = Guid.NewGuid().ToString("N");

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseEnvironment("Development");
    builder.UseSetting("UseInMemoryDatabase", "true");
    builder.UseSetting("InMemoryDatabaseName", _databaseName);
  }
}
