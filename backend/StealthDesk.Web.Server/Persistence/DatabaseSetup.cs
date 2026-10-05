using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;
using StealthDesk.Web.Server.Accounts;

namespace StealthDesk.Web.Server.Persistence;

public static class DatabaseSetup
{
  /// <summary>
  /// PostgreSQL built from the <c>POSTGRES_*</c> settings, or an in-memory database when
  /// <c>UseInMemoryDatabase</c> is true (optionally named by <c>InMemoryDatabaseName</c>).
  /// </summary>
  public static WebApplicationBuilder AddStealthDeskDatabase(this WebApplicationBuilder builder)
  {
    var configuration = builder.Configuration;

    if (configuration.GetValue<bool>("UseInMemoryDatabase"))
    {
      var name = configuration["InMemoryDatabaseName"];
      var databaseName = string.IsNullOrWhiteSpace(name) ? Guid.NewGuid().ToString("N") : name;
      builder.Services.AddDbContext<StealthDeskDb>(options => options.UseInMemoryDatabase(databaseName));
      return builder;
    }

    var connectionString = PostgresConnectionString(configuration);
    builder.Services.AddDbContext<StealthDeskDb>(options => options.UseNpgsql(connectionString));
    return builder;
  }

  /// <summary>
  /// Brings the schema up to date (migrations on PostgreSQL, a plain create in memory) and marks every device
  /// offline. Tenants are created by registration, starting with the first user's.
  /// </summary>
  public static async Task PrepareDatabaseAsync(this WebApplication app)
  {
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

  internal static string PostgresConnectionString(IConfiguration configuration)
  {
    string Required(string key) =>
      configuration[key] is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Missing configuration value '{key}'.");

    return new NpgsqlConnectionStringBuilder
    {
      Host = Required("POSTGRES_HOST"),
      Port = configuration.GetValue("POSTGRES_PORT", 5432),
      Database = configuration["POSTGRES_DB"] is { Length: > 0 } database ? database : "stealthdesk",
      Username = Required("POSTGRES_USER"),
      Password = Required("POSTGRES_PASSWORD"),
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
