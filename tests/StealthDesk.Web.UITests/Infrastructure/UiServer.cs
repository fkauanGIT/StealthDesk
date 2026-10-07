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
/// </summary>
public sealed class UiServer : IAsyncDisposable
{
  private static readonly Lazy<Task<PostgreSqlContainer>> Container = new(async () =>
  {
    var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    await container.StartAsync();
    return container;
  });

  private readonly Process _process;
  private readonly StreamWriter _log;

  private UiServer(Process process, StreamWriter log, Uri baseAddress)
  {
    _process = process;
    _log = log;
    BaseAddress = baseAddress;
  }

  /// <summary>Where the browser opens the web client, ending with a slash.</summary>
  public Uri BaseAddress { get; }

  /// <param name="settings">Extra settings, e.g. <c>("Accounts:EnablePublicRegistration", "true")</c>.</param>
  public static async Task<UiServer> StartAsync(string testName, params (string Key, string Value)[] settings)
  {
    var database = await CreateDatabaseAsync();
    var port = FreePort();
    var baseAddress = new Uri($"http://localhost:{port}/");

    // The built server, without `dotnet run`: evaluating the project again would cost seconds on every test.
    var output = Path.Combine(UiPaths.Repository, "backend", "StealthDesk.Web.Server", "bin", BuildConfiguration, "net10.0");
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

    var server = new UiServer(process, log, baseAddress);
    await server.WaitUntilAliveAsync();
    return server;
  }

  public async ValueTask DisposeAsync()
  {
    if (!_process.HasExited)
    {
      _process.Kill(entireProcessTree: true);
      await _process.WaitForExitAsync();
    }

    _process.Dispose();
    await _log.DisposeAsync();
  }

  private static string BuildConfiguration =>
    typeof(UiServer).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

  private async Task WaitUntilAliveAsync()
  {
    using var http = new HttpClient { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
    var deadline = DateTime.UtcNow.AddSeconds(60);
    while (DateTime.UtcNow < deadline)
    {
      if (_process.HasExited)
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

  private static async Task<(string Key, string Value)[]> CreateDatabaseAsync()
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
    return
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
    ];
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
