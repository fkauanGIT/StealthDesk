namespace StealthDesk.Contracts.Agents;

/// <summary>
/// An agent build the server hands out, and what proves a download of it is that build: a zip of the agent's
/// executable, named as the zip is (<c>StealthDesk.Agent.zip</c> holds <c>StealthDesk.Agent.exe</c>), and the desktop
/// client's folder next to it.
/// </summary>
/// <param name="Path">Where the server serves it, relative to the server's address.</param>
/// <param name="Sha256">The file's SHA-256, in hexadecimal.</param>
public sealed record AgentDownload(string Path, string FileName, long Size, string Sha256)
{
  /// <summary>The agent's executable in the zip.</summary>
  public string Executable => System.IO.Path.ChangeExtension(FileName, ".exe");
}

/// <summary>The PowerShell command that installs the Windows agent from the server it was copied from.</summary>
public static class AgentInstallCommand
{
  /// <summary>
  /// Downloads the agent into the temp folder and, only if its SHA-256 matches, unpacks it and runs <c>install</c>;
  /// otherwise deletes it and says why. Leaves nothing behind either way. It carries the installer key and nothing else
  /// secret. Run as an administrator.
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
      $"$bundle = Join-Path $env:TEMP {Quote(download.FileName)}",
      $"$folder = Join-Path $env:TEMP {Quote(System.IO.Path.GetFileNameWithoutExtension(download.FileName))}",
      $"Invoke-WebRequest -UseBasicParsing -Uri {Quote(origin + download.Path)} -OutFile $bundle",
      "if ((Get-FileHash -Algorithm SHA256 $bundle).Hash -eq " + Quote(download.Sha256) + ") { " +
        // A folder left by an earlier attempt would mix its files with these.
        "Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $folder; Expand-Archive -LiteralPath $bundle -DestinationPath $folder; Remove-Item $bundle; " +
        $"$agent = Join-Path $folder {Quote(download.Executable)}; try {{ {install} }} finally {{ Remove-Item -Recurse -Force $folder }} }} " +
      "else { Remove-Item $bundle; Write-Error 'The downloaded agent does not match its SHA-256 hash: it was deleted without running.' }");
  }

  // A single-quoted PowerShell string takes everything literally; a quote inside is written twice.
  private static string Quote(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
}
