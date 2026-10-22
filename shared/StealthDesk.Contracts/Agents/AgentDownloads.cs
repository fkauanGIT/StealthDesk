namespace StealthDesk.Contracts.Agents;

/// <summary>An agent build the server hands out, and what proves a download of it is that build.</summary>
/// <param name="Path">Where the server serves it, relative to the server's address.</param>
/// <param name="Sha256">The file's SHA-256, in hexadecimal.</param>
public sealed record AgentDownload(string Path, string FileName, long Size, string Sha256);

/// <summary>The PowerShell command that installs the Windows agent from the server it was copied from.</summary>
public static class AgentInstallCommand
{
  /// <summary>
  /// Downloads the agent into the temp folder and runs <c>install</c> only if its SHA-256 matches; otherwise deletes it
  /// and says why. It carries the installer key and nothing else secret. Run as an administrator.
  /// </summary>
  public static string Windows(Uri server, AgentDownload download, string installerKey, Guid? deviceId = null, string? instance = null)
  {
    var origin = server.GetLeftPart(UriPartial.Authority);
    var install = $"& $agent install --server {Quote(origin)} --installer-key {Quote(installerKey)}";
    if (deviceId is { } id && id != Guid.Empty)
    {
      install += $" --device-id {id}";
    }

    if (!string.IsNullOrWhiteSpace(instance))
    {
      install += $" --instance {Quote(instance)}";
    }

    return string.Join("; ",
      "$ProgressPreference = 'SilentlyContinue'",
      // Windows PowerShell 5.1 may not offer TLS 1.2 on its own.
      "[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor 3072",
      $"$agent = Join-Path $env:TEMP {Quote(download.FileName)}",
      $"Invoke-WebRequest -UseBasicParsing -Uri {Quote(origin + download.Path)} -OutFile $agent",
      $"if ((Get-FileHash -Algorithm SHA256 $agent).Hash -eq {Quote(download.Sha256)}) {{ {install}; Remove-Item $agent }} " +
      "else { Remove-Item $agent; Write-Error 'The downloaded agent does not match its SHA-256 hash: it was deleted without running.' }");
  }

  // A single-quoted PowerShell string takes everything literally; a quote inside is written twice.
  private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
