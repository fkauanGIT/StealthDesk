using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>The real server, hosted in memory, in the Development environment.</summary>
public sealed class ServerHost : WebApplicationFactory<Program>
{
  private readonly Dictionary<string, string?> _settings;
  private readonly List<Action<IServiceCollection>> _services = [];
  private bool _started;
  private string? _pool;
  private bool _released;

  private ServerHost(Dictionary<string, string?> settings)
  {
    _settings = settings;

    // Development reads the machine's user secrets: providers configured there must not leak into tests.
    foreach (var key in new[] { "MicrosoftClientId", "MicrosoftClientSecret", "GitHubClientId", "GitHubClientSecret" })
    {
      _settings.TryAdd($"Accounts:{key}", string.Empty);
    }
  }

  /// <param name="databaseName">Two servers given the same name share the database, like a restart.</param>
  public static ServerHost InMemory(string? databaseName = null) => new(new Dictionary<string, string?>
  {
    ["UseInMemoryDatabase"] = "true",
    ["InMemoryDatabaseName"] = databaseName ?? Guid.NewGuid().ToString("N"),
  });

  /// <summary>A server on its own, freshly migrated PostgreSQL database. Needs Docker.</summary>
  public static async Task<ServerHost> OnPostgresAsync() => new(await PostgresDatabases.CreateAsync());

  /// <summary>
  /// Overrides a setting, e.g. <c>With("Accounts:EnablePublicRegistration", "true")</c>. Only before the server starts,
  /// which happens on first use.
  /// </summary>
  public ServerHost With(string key, string? value)
  {
    _settings[key] = value;
    return this;
  }

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

  /// <summary>Replaces services after the server's own registrations. Only before the server starts.</summary>
  public ServerHost WithServices(Action<IServiceCollection> configure)
  {
    _services.Add(configure);
    return this;
  }

  /// <summary>Captures account emails instead of sending them, with sending turned on.</summary>
  public ServerHost WithCapturedEmails(CapturedEmails emails) =>
    With("Email:DisableSending", "false")
      .WithServices(services => services.AddSingleton<StealthDesk.Web.Server.Email.IEmailTransport>(emails));

  /// <summary>
  /// Closes this server's pooled connections as it stops. Pools outlive the server and keep idle connections for
  /// minutes; across a whole run they would exhaust the test database's connections.
  /// </summary>
  public override async ValueTask DisposeAsync()
  {
    RememberPool();
    await base.DisposeAsync();
    ClearPool();
  }

  protected override void Dispose(bool disposing)
  {
    if (disposing)
    {
      RememberPool();
    }

    base.Dispose(disposing);
    ClearPool();
  }

  // Read while the server still runs: disposing one way also runs the other, after the services are gone.
  private void RememberPool()
  {
    if (!_released && _pool is null && _started && _settings.GetValueOrDefault("UseInMemoryDatabase") != "true")
    {
      using var scope = Services.CreateScope();
      _pool = scope.ServiceProvider.GetRequiredService<StealthDeskDb>().Database.GetConnectionString();
    }
  }

  private void ClearPool()
  {
    _released = true;
    if (Interlocked.Exchange(ref _pool, null) is { } connectionString)
    {
      Npgsql.NpgsqlConnection.ClearPool(new Npgsql.NpgsqlConnection(connectionString));
    }
  }

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
    _started = true;
    builder.UseEnvironment("Development");
    foreach (var (key, value) in _settings)
    {
      builder.UseSetting(key, value);
    }

    builder.ConfigureTestServices(services =>
    {
      foreach (var configure in _services)
      {
        configure(services);
      }
    });
  }
}
