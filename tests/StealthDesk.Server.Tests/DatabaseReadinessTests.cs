using System.Net;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

public sealed class DatabaseReadinessTests
{
  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  // Probes come from the container runtime or an orchestrator, which never sign in.
  [Theory]
  [InlineData("/health/ready")]
  [InlineData("/health/live")]
  [InlineData("/health")]
  [InlineData("/alive")]
  public async Task TheHealthEndpoints_AnswerWithoutSigningIn(string path)
  {
    using var server = ServerHost.InMemory();
    using var client = TestAccounts.Client(server);

    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path, Cancel)).StatusCode);
  }

  // A process waiting for its database isn't stuck: it's not ready, but restarting it would help nothing.
  [Fact]
  public async Task WithoutItsDatabase_TheServerIsNotReady_ButStillAlive()
  {
    using var server = await ServerHost.OnPostgresAsync();
    using var client = TestAccounts.Client(server);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Cancel)).StatusCode);

    await PostgresDatabases.DropAsync(server.Setting("POSTGRES_DB")!);

    Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health/ready", Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/health", Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/alive", Cancel)).StatusCode);
  }

  [Fact]
  public async Task WithMigrationsOff_ADatabaseMissingThem_StopsTheServer()
  {
    using var server = (await ServerHost.OnPostgresAsync()).With("ApplyMigrationsOnStart", "false");

    var refusal = server.RefusalToStart();

    Assert.Contains("ApplyMigrationsOnStart is off, but the database lacks", refusal, StringComparison.Ordinal);
    Assert.False(await TablesExistAsync(server), "The server created tables with migrations off.");
  }

  [Fact]
  public async Task WithMigrationsOff_AnUpToDateDatabase_IsUsedAsItIs()
  {
    using var migrated = await ServerHost.OnPostgresAsync();
    await TestAccounts.CreateUserAsync(migrated, "kept@example.com");

    using var restarted = migrated.Restarted().With("ApplyMigrationsOnStart", "false");
    using var client = TestAccounts.Client(restarted);

    Assert.Equal(HttpStatusCode.OK, (await TestAccounts.SignInAsync(client, "kept@example.com")).StatusCode);
    Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready", Cancel)).StatusCode);
  }

  // Asked of the database directly: the server that refused to start has no services to ask with.
  private static async Task<bool> TablesExistAsync(ServerHost server)
  {
    var connection = new Npgsql.NpgsqlConnectionStringBuilder
    {
      Host = server.Setting("POSTGRES_HOST"),
      Port = int.Parse(server.Setting("POSTGRES_PORT")!),
      Database = server.Setting("POSTGRES_DB"),
      Username = server.Setting("POSTGRES_USER"),
      Password = server.Setting("POSTGRES_PASSWORD"),
      Pooling = false,
    };
    await using var open = new Npgsql.NpgsqlConnection(connection.ConnectionString);
    await open.OpenAsync(Cancel);
    await using var command = open.CreateCommand();
    command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'";
    return (long)(await command.ExecuteScalarAsync(Cancel))! > 0;
  }
}
