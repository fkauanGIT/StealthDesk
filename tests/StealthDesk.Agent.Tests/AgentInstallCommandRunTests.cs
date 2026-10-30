using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using StealthDesk.Contracts.Agents;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// The install command run by Windows PowerShell, against a server that serves a stand-in for the agent's zip: it
/// unpacks and runs the download only when its hash matches, and never leaves it behind.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentInstallCommandRunTests : IAsyncDisposable
{
  private static readonly string Name = $"sd-test-agent-{Guid.NewGuid():N}";
  private static readonly byte[] StandIn = Bundle();
  private OneFileServer? _server;

  private OneFileServer Server => _server ??= new OneFileServer(StandIn);

  private static string Downloaded => Path.Combine(Path.GetTempPath(), $"{Name}.zip");

  private static string Unpacked => Path.Combine(Path.GetTempPath(), Name);

  public ValueTask DisposeAsync() => _server?.DisposeAsync() ?? ValueTask.CompletedTask;

  [Fact]
  public async Task AMatchingDownload_Runs_AndIsDeletedAfterwards()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows PowerShell.");

    var result = await RunAsync(Convert.ToHexString(SHA256.HashData(StandIn)));

    Assert.True(result.Outcome == "ran", result.ToString());
    Assert.False(File.Exists(Downloaded), result.ToString());
    Assert.False(Directory.Exists(Unpacked), result.ToString());
    Assert.Equal(1, Server.Requests);
  }

  [Fact]
  public async Task ATamperedDownload_IsRefusedBeforeItRuns_AndDeleted()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows PowerShell.");

    // The command names the real build's hash; what arrives is different by one byte.
    var real = StandIn;
    Server.Serve([.. real[..^1], (byte)(real[^1] ^ 0xFF)]);
    var result = await RunAsync(Convert.ToHexString(SHA256.HashData(real)));

    Assert.True(result.Outcome == "not run", result.ToString());
    Assert.True(result.Errors.Contains("does not match its SHA-256 hash", StringComparison.Ordinal), result.ToString());
    Assert.False(File.Exists(Downloaded), result.ToString());
    Assert.False(Directory.Exists(Unpacked), result.ToString());
  }

  private async Task<RunResult> RunAsync(string sha256)
  {
    var command = AgentInstallCommand.Windows(Server.Address, new AgentDownload("/agent", $"{Name}.zip", StandIn.Length, sha256), "id:secret");

    // $LASTEXITCODE is set only by running a program: here, only by the downloaded one.
    var script = $"$LASTEXITCODE = $null; {command}; if ($null -ne $LASTEXITCODE) {{ 'ran' }} else {{ 'not run' }}";
    var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
    {
      start.ArgumentList.Add(argument);
    }

    // Like a new PowerShell window: a module path inherited from PowerShell 7 (which runs these tests in CI) would
    // make Windows PowerShell load modules it can't use, and lose Invoke-WebRequest and Get-FileHash.
    start.Environment.Remove("PSModulePath");

    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(TestContext.Current.CancellationToken);
    return new RunResult((await output).Trim(), await errors);
  }

  // A harmless program in place of the agent, as the zip names it: run with the agent's arguments, it just fails,
  // leaving an exit code. A desktop client folder next to it, as in the real zip.
  private static byte[] Bundle()
  {
    using var zip = new MemoryStream();
    using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
    {
      archive.CreateEntryFromFile(Path.Combine(Environment.SystemDirectory, "where.exe"), $"{Name}.exe");
      using var client = new StreamWriter(archive.CreateEntry("DesktopClient/StealthDesk.DesktopClient.exe").Open());
      client.Write("stand-in");
    }

    return zip.ToArray();
  }

  /// <summary>What Windows PowerShell printed; shown whole when a check fails.</summary>
  private sealed record RunResult(string Output, string Errors)
  {
    public string Outcome => Output.Split('\n')[^1].Trim();

    public override string ToString() => $"Output:\n{Output}\nErrors:\n{Errors}";
  }

  /// <summary>Answers every request with one file, over plain HTTP on a free local port.</summary>
  private sealed class OneFileServer : IAsyncDisposable
  {
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _serving;
    private byte[] _file;
    private int _requests;

    public OneFileServer(byte[] file)
    {
      _file = file;
      _listener.Start();
      _serving = ServeAsync();
    }

    public Uri Address => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");

    public int Requests => Volatile.Read(ref _requests);

    public void Serve(byte[] file) => _file = file;

    public async ValueTask DisposeAsync()
    {
      await _stop.CancelAsync();
      _listener.Stop();
      try
      {
        await _serving;
      }
      catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
      {
      }

      _stop.Dispose();
    }

    private async Task ServeAsync()
    {
      while (!_stop.IsCancellationRequested)
      {
        using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token)))
        {
        }

        Interlocked.Increment(ref _requests);
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: {_file.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(header, _stop.Token);
        await stream.WriteAsync(_file, _stop.Token);
      }
    }
  }
}
