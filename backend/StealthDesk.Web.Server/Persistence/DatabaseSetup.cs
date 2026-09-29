using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace StealthDesk.Web.Server.Persistence;

public static class DatabaseSetup
{
  public const string DefaultTenantName = "Default";

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
  /// Brings the schema up to date (migrations on PostgreSQL, a plain create in memory)
  /// and makes sure there is a tenant for agents to join.
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

    if (!await db.Tenants.AnyAsync())
    {
      db.Tenants.Add(new TenantRecord { Name = DefaultTenantName });
      await db.SaveChangesAsync();
      app.Logger.LogInformation("Created the '{Tenant}' tenant.", DefaultTenantName);
    }
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
      var options = new DbContextOptionsBuilder<StealthDeskDb>()
        .UseNpgsql("Host=localhost;Database=stealthdesk_design")
        .Options;
      return new StealthDeskDb(options);
    }
  }
}
