using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>The real server, hosted in memory, in the Development environment (self-registration on).</summary>
public sealed class ServerHost : WebApplicationFactory<Program>
{
  private readonly Dictionary<string, string?> _settings;

  private ServerHost(Dictionary<string, string?> settings)
  {
    _settings = settings;
  }

  /// <param name="databaseName">Two servers given the same name share the database, like a restart.</param>
  public static ServerHost InMemory(string? databaseName = null) => new(new Dictionary<string, string?>
  {
    ["UseInMemoryDatabase"] = "true",
    ["InMemoryDatabaseName"] = databaseName ?? Guid.NewGuid().ToString("N"),
  });

  /// <summary>A server on its own, freshly migrated PostgreSQL database. Needs Docker.</summary>
  public static async Task<ServerHost> OnPostgresAsync() => new(await PostgresDatabases.CreateAsync());

  /// <summary>Another server on this one's database, as if it had restarted.</summary>
  public ServerHost Restarted() => new(_settings);

  public T WithDb<T>(Func<StealthDeskDb, T> action)
  {
    using var scope = Services.CreateScope();
    return action(scope.ServiceProvider.GetRequiredService<StealthDeskDb>());
  }

  public async Task<T> WithDbAsync<T>(Func<StealthDeskDb, Task<T>> action)
  {
    await using var scope = Services.CreateAsyncScope();
    return await action(scope.ServiceProvider.GetRequiredService<StealthDeskDb>());
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
