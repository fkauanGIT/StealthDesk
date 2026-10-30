using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using StealthDesk.Branding;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Agents;

namespace StealthDesk.Web.Server.AgentDownloads;

public sealed class AgentDownloadOptions
{
  public const string Section = "AgentDownloads";

  /// <summary>
  /// Where the agent builds the server hands out are, relative to the server's folder unless absolute. The Windows
  /// agent is <c>windows/StealthDesk.Agent.zip</c> in it: the agent and its desktop client. The container image ships
  /// one there.
  /// </summary>
  public string Folder { get; set; } = "downloads";
}

/// <summary>
/// The agent builds this server hands out. A file's SHA-256 is worked out once and again only when the file changes,
/// so replacing a build is enough for install commands to name the new one.
/// </summary>
public sealed class AgentDownloadCatalog(IOptions<AgentDownloadOptions> options, IHostEnvironment environment)
{
  public static readonly string WindowsAgentFileName = $"{Brand.AgentName}.zip";

  private readonly Lock _gate = new();
  private (string Path, long Size, DateTime Written, string Sha256)? _known;

  public string WindowsAgentPath => Path.Combine(Path.GetFullPath(options.Value.Folder, environment.ContentRootPath), "windows", WindowsAgentFileName);

  /// <summary>The Windows agent, or null when the server has no build of it.</summary>
  public AgentDownload? WindowsAgent()
  {
    var file = new FileInfo(WindowsAgentPath);
    if (!file.Exists)
    {
      return null;
    }

    lock (_gate)
    {
      if (_known is not { } known || known.Path != file.FullName || known.Size != file.Length || known.Written != file.LastWriteTimeUtc)
      {
        using var stream = file.OpenRead();
        known = (file.FullName, file.Length, file.LastWriteTimeUtc, Convert.ToHexString(SHA256.HashData(stream)));
        _known = known;
      }

      return new AgentDownload(Routes.WindowsAgentDownload, WindowsAgentFileName, known.Size, known.Sha256);
    }
  }
}

public static class AgentDownloadEndpoints
{
  public static WebApplicationBuilder AddStealthDeskAgentDownloads(this WebApplicationBuilder builder)
  {
    builder.Services.AddOptions<AgentDownloadOptions>().Bind(builder.Configuration.GetSection(AgentDownloadOptions.Section));
    builder.Services.AddSingleton<AgentDownloadCatalog>();
    return builder;
  }

  public static IEndpointRouteBuilder MapAgentDownloadEndpoints(this IEndpointRouteBuilder endpoints)
  {
    // The machine being installed has no account here. The build isn't secret: install commands carry its hash.
    endpoints.MapGet(Routes.WindowsAgentDownload, (AgentDownloadCatalog catalog) =>
        File.Exists(catalog.WindowsAgentPath)
          ? Results.File(catalog.WindowsAgentPath, "application/zip", AgentDownloadCatalog.WindowsAgentFileName)
          : Results.NotFound())
      .AllowAnonymous();

    return endpoints;
  }
}
