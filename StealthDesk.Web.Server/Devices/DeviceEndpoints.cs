using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.Web.Server.Gateway;
using StealthDesk.Web.Server.Permissions;
using StealthDesk.Web.Server.RemoteControl;

namespace StealthDesk.Web.Server.Devices;

public static class DeviceEndpoints
{
  // The list holds only the devices the caller may read. A device they may not read answers 404, like one that
  // doesn't exist, so asking can't reveal that it does.
  public static IEndpointRouteBuilder MapDeviceEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var devices = endpoints.MapGroup(string.Empty).RequireAuthorization().ChecksPermission(PermissionNames.DeviceRead);

    devices.MapGet(Routes.Devices, async (ClaimsPrincipal user, IDeviceAccess access, StealthDeskDb db, CancellationToken cancellationToken) =>
    {
      var scope = await access.ForAsync(user, cancellationToken);
      var devices = await scope.Apply(db.Devices.AsNoTracking().Include(x => x.GroupMemberships))
        .OrderBy(x => x.Name)
        .ToListAsync(cancellationToken);

      return devices.Select(ToSummary);
    });

    devices.MapGet($"{Routes.Devices}/{{id:guid}}", async (
      Guid id,
      ClaimsPrincipal user,
      IAuthorizationService authorization,
      StealthDeskDb db,
      CancellationToken cancellationToken) =>
    {
      var device = await db.Devices.AsNoTracking().Include(x => x.GroupMemberships).FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
      if (device is null || !(await authorization.AuthorizeAsync(user, device, PermissionPolicies.For(PermissionNames.DeviceRead))).Succeeded)
      {
        return Results.NotFound();
      }

      return Results.Ok(ToSummary(device));
    });

    endpoints.MapGet($"{Routes.Devices}/{{id:guid}}/desktop-preview", async (
      Guid id,
      ClaimsPrincipal user,
      IAuthorizationService authorization,
      IOptions<RemoteControlOptions> options,
      IAgentCalls agents,
      StealthDeskDb db,
      CancellationToken cancellationToken) =>
    {
      // Never why it's missing: not found, no permission and the feature turned off all answer the same.
      if (!options.Value.EnableDesktopPreview)
      {
        return Results.NotFound();
      }

      var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
      if (device is null || !(await authorization.AuthorizeAsync(user, device, PermissionPolicies.For(PermissionNames.DeviceDesktopPreviewRead))).Succeeded)
      {
        return Results.NotFound();
      }

      var preview = await agents.CallAsync(device.Id, (agent, token) => agent.GetDesktopPreview(token), cancellationToken);
      if (!preview.Accepted)
      {
        return Results.NotFound();
      }

      return Results.Bytes(preview.Value!.Image, preview.Value.Encoding == ImageEncoding.Png ? "image/png" : "image/jpeg");
    }).RequireAuthorization().ChecksPermission(PermissionNames.DeviceDesktopPreviewRead);

    endpoints.MapGet($"{Routes.Devices}/{{id:guid}}/files", async (
      Guid id,
      string? path,
      ClaimsPrincipal user,
      IAuthorizationService authorization,
      IAgentCalls agents,
      StealthDeskDb db,
      CancellationToken cancellationToken) =>
    {
      var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
      if (device is null || !(await authorization.AuthorizeAsync(user, device, PermissionPolicies.For(PermissionNames.DeviceFileManagerRead))).Succeeded)
      {
        return Results.NotFound();
      }

      var listing = await agents.CallAsync(device.Id, (agent, token) => agent.ListDirectory(path ?? string.Empty, token), cancellationToken);
      if (!listing.Accepted)
      {
        return Results.NotFound();
      }

      return Results.Ok(listing.Value);
    }).RequireAuthorization().ChecksPermission(PermissionNames.DeviceFileManagerRead);

    endpoints.MapGet($"{Routes.Devices}/{{id:guid}}/files/download", async (
      Guid id,
      string path,
      ClaimsPrincipal user,
      IAuthorizationService authorization,
      IAgentCalls agents,
      IFileTransfers transfers,
      IOptions<FileManagerOptions> options,
      StealthDeskDb db,
      HttpContext httpContext,
      ILoggerFactory loggerFactory,
      CancellationToken cancellationToken) =>
    {
      var logger = loggerFactory.CreateLogger("StealthDesk.Web.Server.Devices.FileDownload");
      logger.LogInformation("Download requested: device {DeviceId}, path {Path}.", id, path);

      var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
      if (device is null || !(await authorization.AuthorizeAsync(user, device, PermissionPolicies.For(PermissionNames.DeviceFileManagerRead))).Succeeded)
      {
        logger.LogInformation("Download refused: device {DeviceId} not found or not permitted.", id);
        return Results.NotFound();
      }

      // Begun before asking the agent: its bytes can start arriving on another connection before this call returns.
      var transferId = Guid.NewGuid();
      using var transfer = transfers.Begin(transferId);
      var info = await agents.CallAsync(device.Id, (agent, token) => agent.StartFileDownload(transferId, path, token), cancellationToken);
      logger.LogInformation(
        "Download agent reply for {TransferId}: accepted={Accepted}, exists={Exists}, size={Size}, error={Error}.",
        transferId, info.Accepted, info.Value?.Exists, info.Value?.SizeBytes, info.Error);
      if (!info.Accepted || !info.Value!.Exists)
      {
        return Results.NotFound();
      }

      if (info.Value.SizeBytes > options.Value.MaxDownloadSizeBytes)
      {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
      }

      var response = httpContext.Response;
      response.ContentType = "application/octet-stream";
      response.Headers.ContentDisposition =
        new ContentDispositionHeaderValue("attachment") { FileNameStar = info.Value.FileName }.ToString();

      try
      {
        await foreach (var chunk in transfer.ReadChunks(cancellationToken))
        {
          await response.Body.WriteAsync(chunk, cancellationToken);
        }
      }
      catch (TimeoutException) when (!response.HasStarted)
      {
        logger.LogWarning("Download {TransferId} timed out waiting for the device.", transferId);
        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
      }
      catch (Exception ex) when (!response.HasStarted)
      {
        // The agent accepted the download but failed while actually reading the file (e.g. a locked system file).
        logger.LogWarning(ex, "Download {TransferId} failed while reading the file.", transferId);
        return Results.StatusCode(StatusCodes.Status502BadGateway);
      }
      catch (Exception ex)
      {
        // Headers (and maybe some bytes) already went out: nothing left to answer with but to end the connection.
        logger.LogWarning(ex, "Download {TransferId} failed after the response had already started.", transferId);
        throw;
      }

      return Results.Empty;
    }).RequireAuthorization().ChecksPermission(PermissionNames.DeviceFileManagerRead);

    endpoints.MapPost($"{Routes.Devices}/{{id:guid}}/files/upload", async (
      Guid id,
      string path,
      string fileName,
      bool overwrite,
      ClaimsPrincipal user,
      IAuthorizationService authorization,
      IAgentCalls agents,
      IFileTransfers transfers,
      IOptions<FileManagerOptions> fileManagerOptions,
      IOptions<GatewayOptions> gatewayOptions,
      StealthDeskDb db,
      HttpContext httpContext,
      CancellationToken cancellationToken) =>
    {
      var device = await db.Devices.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
      if (device is null || !(await authorization.AuthorizeAsync(user, device, PermissionPolicies.For(PermissionNames.DeviceFileManagerWrite))).Succeeded)
      {
        return Results.NotFound();
      }

      var maxSize = fileManagerOptions.Value.MaxUploadSizeBytes;
      if (maxSize > 0 && httpContext.Request.ContentLength is { } declaredLength && declaredLength > maxSize)
      {
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
      }

      // Begun, and fed, before asking the agent: its own bytes can start arriving on another connection before
      // this call returns, same as a download.
      var transferId = Guid.NewGuid();
      using var transfer = transfers.Begin(transferId);
      var pump = PumpUploadAsync(httpContext.Request.Body, transfer, maxSize, cancellationToken);

      var accepted = await agents.CallAsync(device.Id, (agent, token) => agent.StartFileUpload(transferId, path, fileName, overwrite, token), cancellationToken);
      if (!accepted.Accepted)
      {
        return Results.Problem(accepted.Error, statusCode: StatusCodes.Status409Conflict);
      }

      // Every byte is through (or the body failed) by the time this returns; what's left is for the agent to
      // finish writing them to disk and say so.
      await pump;

      using var reportWait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      reportWait.CancelAfter(gatewayOptions.Value.AgentCallTimeout);
      try
      {
        var error = await transfer.Outcome.WaitAsync(reportWait.Token);
        return error is null ? Results.Ok() : Results.Problem(error, statusCode: StatusCodes.Status502BadGateway);
      }
      catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
      {
        return Results.StatusCode(StatusCodes.Status504GatewayTimeout);
      }
    }).RequireAuthorization().ChecksPermission(PermissionNames.DeviceFileManagerWrite);

    return endpoints;
  }

  /// <summary>
  /// Feeds the request body into <paramref name="transfer"/> a chunk at a time, for the agent to pull. Ends the
  /// transfer the moment the body does (success, a size violation, or a read failure), and never blocks forever
  /// on a refused or abandoned transfer: writing is cancelled the moment <paramref name="transfer"/> is disposed.
  /// </summary>
  private static async Task PumpUploadAsync(Stream body, FileTransferSink transfer, long maxSize, CancellationToken cancellationToken)
  {
    using var writeLimit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transfer.Done);
    var buffer = new byte[64 * 1024];
    long total = 0;
    try
    {
      int read;
      while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
      {
        total += read;
        if (maxSize > 0 && total > maxSize)
        {
          transfer.Writer.TryComplete(new InvalidOperationException("The upload is larger than the server's transfer limit."));
          return;
        }

        await transfer.Writer.WriteAsync(buffer[..read], writeLimit.Token);
      }

      transfer.Writer.TryComplete();
    }
    catch (Exception ex)
    {
      transfer.Writer.TryComplete(ex);
    }
  }

  public static DeviceSummary ToSummary(DeviceRecord device) => new()
  {
    Id = device.Id,
    TenantId = device.TenantId,
    Name = device.Name,
    Alias = device.Alias,
    DnsName = device.DnsName,
    AgentVersion = device.AgentVersion,
    Platform = device.Platform,
    OsDescription = device.OsDescription,
    OsArchitecture = device.OsArchitecture,
    Is64BitOs = device.Is64BitOs,
    CpuCores = device.CpuCores,
    CpuLoad = device.CpuLoad,
    MemoryTotalGb = device.MemoryTotalGb,
    MemoryUsedGb = device.MemoryUsedGb,
    StorageTotalGb = device.StorageTotalGb,
    StorageUsedGb = device.StorageUsedGb,
    LoggedOnUsers = device.LoggedOnUsers,
    MacAddresses = device.MacAddresses,
    LocalIpV4 = device.LocalIpV4,
    LocalIpV6 = device.LocalIpV6,
    PublicIpV4 = device.PublicIpV4,
    PublicIpV6 = device.PublicIpV6,
    Disks = device.Disks,
    IsOnline = device.IsOnline,
    LastSeen = device.LastSeen,
    GroupIds = [.. device.GroupMemberships.Select(x => x.DeviceGroupId).Order()],
  };
}
