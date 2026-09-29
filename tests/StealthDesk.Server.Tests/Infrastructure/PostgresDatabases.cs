using Npgsql;
using Testcontainers.PostgreSql;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>
/// One PostgreSQL container for the whole test run (starting it takes seconds) and one fresh database per test
/// (creating it takes milliseconds), so tests never see each other's data.
/// </summary>
internal static class PostgresDatabases
{
  private static readonly Lazy<Task<PostgreSqlContainer>> _container = new(StartContainerAsync);

  /// <summary>Creates an empty database and returns the settings the server needs to use it.</summary>
  public static async Task<Dictionary<string, string?>> CreateAsync()
  {
    var container = await _container.Value;
    var databaseName = $"test_{Guid.NewGuid():N}";

    await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
    {
      await connection.OpenAsync();
      await using var command = connection.CreateCommand();
      command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
      await command.ExecuteNonQueryAsync();
    }

    var server = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
    return new Dictionary<string, string?>
    {
      ["UseInMemoryDatabase"] = "false",
      ["POSTGRES_HOST"] = server.Host,
      ["POSTGRES_PORT"] = server.Port.ToString(),
      ["POSTGRES_USER"] = server.Username,
      ["POSTGRES_PASSWORD"] = server.Password,
      ["POSTGRES_DB"] = databaseName,
    };
  }

  private static async Task<PostgreSqlContainer> StartContainerAsync()
  {
    var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    await container.StartAsync();
    return container;
  }
}
