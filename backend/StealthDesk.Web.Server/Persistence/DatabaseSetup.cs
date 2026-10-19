using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.Bootstrap;

namespace StealthDesk.Web.Server.Persistence;

public static class DatabaseSetup
{
  public const string UseInMemoryDatabase = "UseInMemoryDatabase";
  public const string InMemoryDatabaseName = "InMemoryDatabaseName";

  // What appsettings.Development.json uses, so a server outside development can't run with it by mistake.
  private const string DevelopmentPassword = "password";

  /// <summary>
  /// PostgreSQL built from the <c>POSTGRES_*</c> settings, or an in-memory database when
  /// <c>UseInMemoryDatabase</c> is true (optionally named by <c>InMemoryDatabaseName</c>). Outside development
  /// the server refuses the in-memory database and the development password.
  /// </summary>
  public static WebApplicationBuilder AddStealthDeskDatabase(this WebApplicationBuilder builder)
  {
    var configuration = builder.Configuration;
    var development = builder.Environment.IsDevelopment();

    if (configuration.GetValue<bool>(UseInMemoryDatabase))
    {
      if (!development)
      {
        throw new InvalidOperationException(
          $"{UseInMemoryDatabase} is only for development and tests: its data is lost when the server stops. " +
          $"Turn it off and set the POSTGRES_* settings (the environment is {builder.Environment.EnvironmentName}).");
      }

      var name = configuration[InMemoryDatabaseName];
      var databaseName = string.IsNullOrWhiteSpace(name) ? Guid.NewGuid().ToString("N") : name;
      builder.Services.AddDbContext<StealthDeskDb>(options => options.UseInMemoryDatabase(databaseName).AddInterceptors(ServiceAccountInvariant.Instance));
      return builder;
    }

    var connectionString = PostgresConnectionString(configuration, development);
    builder.Services.AddDbContext<StealthDeskDb>(options => options.UseNpgsql(connectionString).AddInterceptors(ServiceAccountInvariant.Instance));
    return builder;
  }

  /// <summary>
  /// Checks every setting, then brings the schema up to date (migrations on PostgreSQL, a plain create in memory),
  /// marks every device offline and creates what the <c>Bootstrap</c> settings describe. Without a bootstrap
  /// administrator, tenants are created by registration, starting with the first user's.
  /// </summary>
  public static async Task PrepareDatabaseAsync(this WebApplication app)
  {
    // Settings are checked before the database is touched, not only when the server starts listening.
    app.Services.GetService<IStartupValidator>()?.Validate();

    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<StealthDeskDb>();

    if (db.Database.IsRelational())
    {
      await db.Database.MigrateAsync();
    }
    else
    {
      await db.Database.EnsureCreatedAsync();
    }

    var cleared = await MarkAllOfflineAsync(db);
    if (cleared > 0)
    {
      app.Logger.LogInformation("Marked {Count} devices offline until their agents reconnect.", cleared);
    }

    await scope.ServiceProvider.GetRequiredService<ServerBootstrap>().RunAsync(CancellationToken.None);
  }

  // Connections don't survive a restart, and a server that stopped abruptly never saw them close.
  // Agents still running reconnect and report themselves online again.
  private static async Task<int> MarkAllOfflineAsync(StealthDeskDb db)
  {
    var online = db.Devices.Where(x => x.IsOnline || x.ConnectionId != string.Empty);

    if (db.Database.IsRelational())
    {
      return await online.ExecuteUpdateAsync(set => set
        .SetProperty(x => x.IsOnline, false)
        .SetProperty(x => x.ConnectionId, string.Empty));
    }

    // The in-memory provider can't run a bulk update.
    var devices = await online.ToListAsync();
    foreach (var device in devices)
    {
      device.IsOnline = false;
      device.ConnectionId = string.Empty;
    }

    await db.SaveChangesAsync();
    return devices.Count;
  }

  internal static string PostgresConnectionString(IConfiguration configuration, bool development)
  {
    string Required(string key) =>
      configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing configuration value '{key}'.");

    var port = configuration.GetValue("POSTGRES_PORT", 5432);
    if (port is < 1 or > 65535)
    {
      throw new InvalidOperationException($"POSTGRES_PORT must be a port number from 1 to 65535, not {port}.");
    }

    var password = Required("POSTGRES_PASSWORD");
    if (!development && password == DevelopmentPassword)
    {
      throw new InvalidOperationException("POSTGRES_PASSWORD is the development password: set the database's real one.");
    }

    return new NpgsqlConnectionStringBuilder
    {
      Host = Required("POSTGRES_HOST"),
      Port = port,
      Database = configuration["POSTGRES_DB"] is { Length: > 0 } database ? database : "stealthdesk",
      Username = Required("POSTGRES_USER"),
      Password = password,
    }.ConnectionString;
  }

  /// <summary>Used by <c>dotnet ef</c> to create migrations without starting the server.</summary>
  internal sealed class DesignTimeFactory : IDesignTimeDbContextFactory<StealthDeskDb>
  {
    public StealthDeskDb CreateDbContext(string[] args)
    {
      var appServices = new ServiceCollection()
        .Configure<IdentityOptions>(AccountSetup.ConfigureStores)
        .BuildServiceProvider();

      var options = new DbContextOptionsBuilder<StealthDeskDb>()
        .UseNpgsql("Host=localhost;Database=stealthdesk_design")
        .UseApplicationServiceProvider(appServices)
        .Options;
      return new StealthDeskDb(options, UnscopedTenant.Instance);
    }
  }
}
