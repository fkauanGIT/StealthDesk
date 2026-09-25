using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;

namespace StealthDesk.Web.Server.Tests.Helpers;

internal sealed record PostgresTestConnectionInfo(
  string Host,
  int Port,
  string Username,
  string Password)
{
  public string GetAdminConnectionString()
  {
    var builder = new NpgsqlConnectionStringBuilder
    {
      Host = Host,
      Port = Port,
      Username = Username,
      Password = Password,
      Database = "postgres",
      Pooling = false
    };

    return builder.ConnectionString;
  }
}

internal static class PostgresTestContainer
{
  private const string DefaultPassword = "password";
  private const string DefaultUsername = "postgres";

  private static readonly SemaphoreSlim _sync = new(1, 1);
  private static PostgresTestConnectionInfo? _connectionInfo;
  private static PostgreSqlContainer? _container;

  public static async Task<string> CreateDatabase(string databaseName)
  {
    var normalizedDatabaseName = NormalizeDatabaseName(databaseName);
    var info = await GetConnectionInfo();

    await using var connection = new NpgsqlConnection(info.GetAdminConnectionString());
    await connection.OpenAsync();

    await using var command = connection.CreateCommand();
    command.CommandText = $"CREATE DATABASE \"{normalizedDatabaseName}\";";

    try
    {
      _ = await command.ExecuteNonQueryAsync();
    }
    catch (PostgresException ex) when (ex.SqlState == "42P04")
    {
      // duplicate_database
    }

    return normalizedDatabaseName;
  }

  public static async Task<PostgresTestConnectionInfo> GetConnectionInfo()
  {
    if (_connectionInfo is not null)
    {
      return _connectionInfo;
    }

    await _sync.WaitAsync();
    try
    {
      if (_connectionInfo is not null)
      {
        return _connectionInfo;
      }

      _container ??= new PostgreSqlBuilder("postgres:18-alpine")
        .WithUsername(DefaultUsername)
        .WithPassword(DefaultPassword)
        .WithDatabase("postgres")
        .WithCleanUp(true)
        .Build();

      await _container.StartAsync();

      _connectionInfo = new PostgresTestConnectionInfo(
        Host: _container.Hostname,
        Port: _container.GetMappedPublicPort(5432),
        Username: DefaultUsername,
        Password: DefaultPassword);

      return _connectionInfo;
    }
    finally
    {
      _sync.Release();
    }
  }

  private static string NormalizeDatabaseName(string input)
  {
    if (string.IsNullOrWhiteSpace(input))
    {
      return $"stealthdesk_tests_{Guid.NewGuid():N}";
    }

    var sb = new StringBuilder(input.Length);
    foreach (var c in input)
    {
      if (char.IsAsciiLetterOrDigit(c) || c == '_')
      {
        sb.Append(char.ToLowerInvariant(c));
        continue;
      }

      sb.Append('_');
    }

    var result = sb.ToString().Trim('_');
    if (result.Length == 0)
    {
      return $"stealthdesk_tests_{Guid.NewGuid():N}";
    }

    if (char.IsDigit(result[0]))
    {
      result = $"db_{result}";
    }

    return result;
  }
}
