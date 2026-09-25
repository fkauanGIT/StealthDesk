using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using StealthDesk.Web.Server.Tests.Helpers;

namespace StealthDesk.Web.Server.Tests;

public class TestAppFactory : WebApplicationFactory<Program>
{
  private readonly Dictionary<string, string?> _settings;

  /// <param name="databaseName">
  /// In-memory database name. Pass the same name to two factories to simulate a server restart.
  /// </param>
  public TestAppFactory(string? databaseName = null)
    : this(new Dictionary<string, string?>
    {
      ["UseInMemoryDatabase"] = "true",
      ["InMemoryDatabaseName"] = databaseName ?? Guid.NewGuid().ToString("N"),
    })
  {
  }

  private TestAppFactory(Dictionary<string, string?> settings)
  {
    _settings = settings;
  }

  /// <summary>
  /// Creates a server backed by a new database in the shared PostgreSQL test container.
  /// The server applies the migrations on startup.
  /// </summary>
  public static async Task<TestAppFactory> CreateWithPostgres([CallerMemberName] string testDatabaseName = "")
  {
    var connectionInfo = await PostgresTestContainer.GetConnectionInfo();
    var databaseName = await PostgresTestContainer.CreateDatabase($"{testDatabaseName}-{Guid.NewGuid()}");

    return new TestAppFactory(new Dictionary<string, string?>
    {
      ["UseInMemoryDatabase"] = "false",
      ["POSTGRES_USER"] = connectionInfo.Username,
      ["POSTGRES_PASSWORD"] = connectionInfo.Password,
      ["POSTGRES_HOST"] = connectionInfo.Host,
      ["POSTGRES_PORT"] = $"{connectionInfo.Port}",
      ["POSTGRES_DB"] = databaseName,
    });
  }

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    builder.UseEnvironment("Development");

    foreach (var (key, value) in _settings)
    {
      builder.UseSetting(key, value);
    }
  }
}
