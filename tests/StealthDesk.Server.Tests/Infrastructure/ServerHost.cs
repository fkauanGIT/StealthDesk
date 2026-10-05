using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Mvc.Testing;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>The real server, hosted in memory, in the Development environment (self-registration on).</summary>
public sealed class ServerHost : WebApplicationFactory<Program>
{
  private readonly Dictionary<string, string?> _settings;
  private readonly List<Action<IServiceCollection>> _services = [];

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

  /// <summary>
  /// Overrides a setting, e.g. <c>With("Gateway:AllowSelfRegistration", "false")</c>. Only before the server starts,
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

  protected override void ConfigureWebHost(IWebHostBuilder builder)
  {
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
