using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace StealthDesk.Web.UITests.Infrastructure;

/// <summary>
/// The real server, started as its own process the way it runs for real, on a fresh PostgreSQL database and a free
/// port. One PostgreSQL container serves the whole run; each server gets an empty database, so every test starts
/// from a new installation.
/// <para>
/// With STEALTHDESK_UI_IMAGE set to a container image, each server is that image instead, run with <c>docker run</c>
/// in Production on the same kind of fresh database. With STEALTHDESK_UI_SERVER set to an address, tests that need
/// an installation of their own are skipped: only the deployment tests run, against that server.
/// </para>
/// </summary>
public sealed class UiServer : IAsyncDisposable
{
  public static string? Image { get; } = Environment.GetEnvironmentVariable("STEALTHDESK_UI_IMAGE") is { Length: > 0 } image ? image : null;

  public static Uri? Address { get; } = Environment.GetEnvironmentVariable("STEALTHDESK_UI_SERVER") is { Length: > 0 } address
    ? new Uri(address.TrimEnd('/') + "/")
    : null;

  private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
  {
    var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    await container.StartAsync();
    return container;
  });

  private readonly Process? _process;
  private readonly StreamWriter? _log;
  private readonly string? _container;
  private readonly string? _database;

  private UiServer(Process? process, StreamWriter? log, Uri baseAddress, string? database, string? container = null)
  {
    _process = process;
    _log = log;
    BaseAddress = baseAddress;
    _database = database;
    _container = container;
  }

  /// <summary>Where the browser opens the web client, ending with a slash.</summary>
  public Uri BaseAddress { get; }

  /// <summary>The connection string of this server's database, for tests that arrange data the pages can't yet.</summary>
  public string Database => _database ?? throw new InvalidOperationException("A server the tests didn't start has no database they can reach.");

  /// <summary>The server at STEALTHDESK_UI_SERVER, which the tests use but don't start or stop.</summary>
  public static UiServer Existing() =>
    new(null, null, Address ?? throw new InvalidOperationException("STEALTHDESK_UI_SERVER names no server."), null);

  /// <param name="settings">Extra settings, e.g. <c>("Accounts:EnablePublicRegistration", "true")</c>.</param>
  public static async Task<UiServer> StartAsync(string testName, params (string Key, string Value)[] settings)
  {
    Assert.SkipWhen(Address is not null, "Needs a new installation of its own; STEALTHDESK_UI_SERVER points at a server shared by the deployment tests.");

    var (database, connectionString) = await CreateDatabaseAsync();
    var port = FreePort();
    if (Image is not null)
    {
      return await StartImageAsync(testName, port, database, connectionString, settings);
    }

    var baseAddress = new Uri($"http://localhost:{port}/");

    // The built server, without `dotnet run`: evaluating the project again would cost seconds on every test.
    var output = Path.Combine(UiPaths.Repository, "StealthDesk.Web.Server", "bin", BuildConfiguration, "net10.0");
    var arguments = new List<string>
    {
      Path.Combine(output, "StealthDesk.Web.Server.dll"),
      "--urls", baseAddress.ToString().TrimEnd('/'),
    };
    foreach (var (key, value) in database.Concat(settings))
    {
      arguments.Add($"--{key}={value}");
    }

    var start = new ProcessStartInfo("dotnet")
    {
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      UseShellExecute = false,
      WorkingDirectory = output,
    };

    // As the "http" launch profile runs it: Development serves the web client's files from the build.
    start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
    arguments.ForEach(start.ArgumentList.Add);

    // The server's output goes to a file next to the screenshots, to read when a test fails.
    var log = new StreamWriter(Path.Combine(UiPaths.Results, $"{testName}.server.log")) { AutoFlush = true };
    var process = Process.Start(start) ?? throw new InvalidOperationException("The server didn't start.");
    process.OutputDataReceived += (_, e) => Write(log, e.Data);
    process.ErrorDataReceived += (_, e) => Write(log, e.Data);
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    var server = new UiServer(process, log, baseAddress, connectionString);
    await server.ReadyOrGoneAsync();
    return server;
  }

  public async ValueTask DisposeAsync()
  {
    // Stopping the docker client leaves its container running: the container goes first.
    if (_container is not null)
    {
      using var remove = Process.Start(new ProcessStartInfo("docker", ["rm", "--force", _container]) { RedirectStandardOutput = true, RedirectStandardError = true });
      await remove!.WaitForExitAsync();
    }

    if (_process is not null)
    {
      if (!_process.HasExited)
      {
        _process.Kill(entireProcessTree: true);
        await _process.WaitForExitAsync();
      }

      _process.Dispose();
    }

    if (_log is not null)
    {
      await _log.DisposeAsync();
    }
  }

  // The image as users run it: Production, settings as StealthDesk_ variables, PostgreSQL reached on the host.
  private static async Task<UiServer> StartImageAsync(
    string testName, int port, (string Key, string Value)[] database, string connectionString, (string Key, string Value)[] settings)
  {
    var container = $"stealthdesk-ui-{Guid.NewGuid():N}"[..32];
    var arguments = new List<string>
    {
      "run", "--rm", "--name", container,
      "--add-host", "host.docker.internal:host-gateway",
      "--publish", $"127.0.0.1:{port}:8080",
    };

    // Nothing in the tests reads email: a server without SMTP settings sends none rather than refusing to start.
    var all = database.Select(x => x.Key == "POSTGRES_HOST" ? (x.Key, "host.docker.internal") : x)
      .Append(("Email:DisableSending", "true"))
      .Concat(settings);
    foreach (var (key, value) in all)
    {
      arguments.Add("--env");
      arguments.Add($"StealthDesk_{key.Replace(":", "__", StringComparison.Ordinal)}={value}");
    }

    arguments.Add(Image!);

    var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    arguments.ForEach(start.ArgumentList.Add);

    var log = new StreamWriter(Path.Combine(UiPaths.Results, $"{testName}.server.log")) { AutoFlush = true };
    var process = Process.Start(start) ?? throw new InvalidOperationException("docker didn't start.");
    process.OutputDataReceived += (_, e) => Write(log, e.Data);
    process.ErrorDataReceived += (_, e) => Write(log, e.Data);
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();

    var server = new UiServer(process, log, new Uri($"http://localhost:{port}/"), connectionString, container);
    await server.ReadyOrGoneAsync();
    return server;
  }

  private static string BuildConfiguration =>
    typeof(UiServer).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

  // A server that never became ready is stopped here: thrown before the test got it, it would be left running.
  private async Task ReadyOrGoneAsync()
  {
    try
    {
      await WaitUntilAliveAsync();
    }
    catch
    {
      await DisposeAsync();
      throw;
    }
  }

  private async Task WaitUntilAliveAsync()
  {
    using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline)
    {
      if (_process is { HasExited: true })
      {
        throw new InvalidOperationException($"The server stopped with code {_process.ExitCode}; see its log in {UiPaths.Results}.");
      }

      try
      {
        if ((await http.GetAsync("alive")).StatusCode == HttpStatusCode.OK)
        {
          return;
        }
      }
      catch (HttpRequestException)
      {
        // Not listening yet.
      }
      catch (TaskCanceledException)
      {
        // Still starting.
      }

      await Task.Delay(250);
    }

    throw new TimeoutException($"The server didn't answer within a minute; see its log in {UiPaths.Results}.");
  }

  private static async Task<((string Key, string Value)[] Settings, string ConnectionString)> CreateDatabaseAsync()
  {
    var container = await Container.Value;
    var name = $"ui_{Guid.NewGuid():N}";
    await using (var connection = new NpgsqlConnection(container.GetConnectionString()))
    {
      await connection.OpenAsync();
      await using var command = connection.CreateCommand();
      command.CommandText = $"CREATE DATABASE \"{name}\"";
      await command.ExecuteNonQueryAsync();
    }

    var server = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
    var connectionString = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = name }.ConnectionString;
    return (
    [
      ("UseInMemoryDatabase", "false"),
      ("POSTGRES_HOST", server.Host!),
      ("POSTGRES_PORT", server.Port.ToString()),
      ("POSTGRES_USER", server.Username!),
      ("POSTGRES_PASSWORD", server.Password!),
      ("POSTGRES_DB", name),
      // Development reads the machine's user secrets: providers configured there must not change the pages.
      ("Accounts:MicrosoftClientId", string.Empty),
      ("Accounts:GitHubClientId", string.Empty),
    ], connectionString);
  }

  private static int FreePort()
  {
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    return ((IPEndPoint)listener.LocalEndpoint).Port;
  }

  private static void Write(StreamWriter log, string? line)
  {
    if (line is null)
    {
      return;
    }

    lock (log)
    {
      log.WriteLine(line);
    }
  }
}

/// <summary>Where the repository is, and where screenshots and server logs go.</summary>
public static class UiPaths
{
  public static string Repository { get; } = FindRepository();

  public static string Results { get; } =
    Directory.CreateDirectory(Path.Combine(Repository, "TestResults", "StealthDesk.Web.UITests")).FullName;

  private static string FindRepository()
  {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
      if (File.Exists(Path.Combine(directory.FullName, "StealthDesk.slnx")))
      {
        return directory.FullName;
      }
    }

    throw new InvalidOperationException("The tests must run inside the repository: StealthDesk.slnx wasn't found.");
  }
}
