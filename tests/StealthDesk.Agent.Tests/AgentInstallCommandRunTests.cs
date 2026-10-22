using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using StealthDesk.Contracts.Agents;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// The install command run by Windows PowerShell, against a server that serves a stand-in for the agent: it runs the
/// download only when its hash matches, and never leaves it behind.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AgentInstallCommandRunTests : IAsyncDisposable
{
  private readonly string _fileName = $"sd-test-agent-{Guid.NewGuid():N}.exe";
  private OneFileServer? _server;

  // A harmless program in place of the agent: run with the agent's arguments, it just fails, leaving an exit code.
  private static byte[] StandIn => File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "where.exe"));

  private OneFileServer Server => _server ??= new OneFileServer(StandIn);

  public ValueTask DisposeAsync() => _server?.DisposeAsync() ?? ValueTask.CompletedTask;

  [Fact]
  public async Task AMatchingDownload_Runs_AndIsDeletedAfterwards()
  {
    Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs Windows PowerShell.");

    var result = await RunAsync(Convert.ToHexString(SHA256.HashData(StandIn)));

    Assert.Equal("ran", result.Outcome);
    Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), _fileName)));
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

    Assert.Equal("not run", result.Outcome);
    Assert.Contains("does not match its SHA-256 hash", result.Errors, StringComparison.Ordinal);
    Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), _fileName)));
  }

  private async Task<(string Outcome, string Errors)> RunAsync(string sha256)
  {
    var command = AgentInstallCommand.Windows(Server.Address, new AgentDownload("/agent", _fileName, StandIn.Length, sha256), "id:secret");

    // $LASTEXITCODE is set only by running a program: here, only by the downloaded one.
    var script = $"$LASTEXITCODE = $null; {command}; if ($null -ne $LASTEXITCODE) {{ 'ran' }} else {{ 'not run' }}";
    var start = new ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
    {
      start.ArgumentList.Add(argument);
    }

    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync();
    var errors = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(TestContext.Current.CancellationToken);
    return ((await output).Trim().Split('\n')[^1].Trim(), await errors);
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
