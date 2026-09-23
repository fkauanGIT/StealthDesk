using Npgsql;
using StealthDesk.Web.Server.Data;

namespace StealthDesk.Web.Server.Startup;

public static class DatabaseRegistrationExtensions
{
  public static void AddStealthDeskInMemoryDb(this IHostApplicationBuilder hostBuilder)
  {
    var dbName = hostBuilder.Configuration.GetValue<string>("InMemoryDatabaseName");
    if (string.IsNullOrWhiteSpace(dbName))
    {
      dbName = Guid.NewGuid().ToString("N");
    }

    hostBuilder.Services.AddDbContext<AppDb>(options =>
    {
      options.UseInMemoryDatabase(dbName);
    });
  }

  public static void AddStealthDeskPostgresDb(this IHostApplicationBuilder hostBuilder)
  {
    var pgUser = hostBuilder.Configuration.GetValue<string>("POSTGRES_USER");
    var pgPass = hostBuilder.Configuration.GetValue<string>("POSTGRES_PASSWORD");
    var pgHost = hostBuilder.Configuration.GetValue<string>("POSTGRES_HOST");
    var pgDb = hostBuilder.Configuration.GetValue<string>("POSTGRES_DB");
    var pgPortRaw = hostBuilder.Configuration.GetValue<string>("POSTGRES_PORT");
    var pgPort = 5432;

    ArgumentException.ThrowIfNullOrWhiteSpace(pgUser);
    ArgumentException.ThrowIfNullOrWhiteSpace(pgHost);
    ArgumentException.ThrowIfNullOrWhiteSpace(pgPass);

    if (string.IsNullOrWhiteSpace(pgDb))
    {
      pgDb = "stealthdesk";
    }

    if (int.TryParse(pgPortRaw, out var parsedPort))
    {
      pgPort = parsedPort;
    }

    if (Uri.TryCreate(pgHost, UriKind.Absolute, out var pgHostUri))
    {
      pgHost = pgHostUri.Host;
      if (pgHostUri.Port > 0)
      {
        pgPort = pgHostUri.Port;
      }
    }

    var pgBuilder = new NpgsqlConnectionStringBuilder
    {
      Database = pgDb,
      Username = pgUser,
      Password = pgPass,
      Host = pgHost,
      Port = pgPort
    };

    hostBuilder.Services.AddDbContext<AppDb>(options =>
    {
      options.UseNpgsql(pgBuilder.ConnectionString);
    });
  }
}
