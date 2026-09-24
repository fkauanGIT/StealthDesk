using Microsoft.AspNetCore.SignalR;
using StealthDesk.Libraries.Api.Contracts.Dtos.HubDtos;
using StealthDesk.Libraries.Api.Contracts.Hubs;
using StealthDesk.Libraries.Api.Contracts.Hubs.Clients;
using StealthDesk.Libraries.Shared.Services.Encryption;
using StealthDesk.Web.Server.Extensions.Dtos.Internal;
using StealthDesk.Web.Server.Services.DeviceManagement;

namespace StealthDesk.Web.Server.Hubs;

public class AgentHub(
  AppDb appDb,
  TimeProvider timeProvider,
  IDeviceManager deviceManager,
  IOptions<AppOptions> appOptions,
  IEd25519KeyProvider keyProvider,
  ILogger<AgentHub> logger) : HubWithItems<IAgentHubClient>, IAgentHub
{
  private readonly AppDb _appDb = appDb;
  private readonly IOptions<AppOptions> _appOptions = appOptions;
  private readonly IDeviceManager _deviceManager = deviceManager;
  private readonly IEd25519KeyProvider _keyProvider = keyProvider;
  private readonly ILogger<AgentHub> _logger = logger;
  private readonly TimeProvider _timeProvider = timeProvider;

  // A new hub instance is created for every call, so per-connection state lives in Context.Items.
  private InternalDtos.DeviceResponseDto? Device
  {
    get => GetItem<InternalDtos.DeviceResponseDto?>(null);
    set => SetItem(value);
  }

  public override async Task OnDisconnectedAsync(Exception? exception)
  {
    try
    {
      if (Device is { } cachedDevice)
      {
        var currentConnectionId = await _appDb.Devices
          .Where(x => x.Id == cachedDevice.Id)
          .Select(x => x.ConnectionId)
          .FirstOrDefaultAsync();

        // The agent may have reconnected already. Only the current connection can mark the device offline.
        if (currentConnectionId == Context.ConnectionId)
        {
          await _deviceManager.MarkDeviceOffline(cachedDevice.Id, _timeProvider.GetLocalNow());
        }
        else
        {
          _logger.LogDebug(
            "Skipping offline update for device {DeviceId}. Current connection is {CurrentConnectionId}, closing {ClosingConnectionId}.",
            cachedDevice.Id,
            currentConnectionId,
            Context.ConnectionId);
        }
      }
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error while handling agent disconnect.");
    }
    finally
    {
      await base.OnDisconnectedAsync(exception);
    }
  }

  public async Task<HubResult<InternalDtos.DeviceResponseDto>> UpdateDeviceSigned(SignedDto<DeviceUpdateRequestDto> signedDto)
  {
    try
    {
      var agentDto = signedDto.Dto;
      var options = _appOptions.Value;

      var storedPublicKey = await _appDb.Devices
        .Where(x => x.Id == agentDto.Id)
        .Select(x => x.PublicKey)
        .FirstOrDefaultAsync();

      if (string.IsNullOrEmpty(storedPublicKey) && !options.AllowAgentsToSelfBootstrap)
      {
        _logger.LogWarning("Rejecting update from unknown device {DeviceId}. Self-bootstrap is disabled.", agentDto.Id);
        return Fail("Unknown device.");
      }

      // Once a device has a key, only that key is trusted. Otherwise anyone could claim the device's id.
      var publicKeyBase64 = string.IsNullOrEmpty(storedPublicKey)
        ? signedDto.PublicKey
        : storedPublicKey;

      var keyResult = _keyProvider.ValidatePublicKeyBase64(publicKeyBase64);
      if (!keyResult.IsSuccess)
      {
        _logger.LogWarning("Invalid public key for device {DeviceId}: {Reason}", agentDto.Id, keyResult.Reason);
        return Fail(keyResult.Reason);
      }

      if (!_keyProvider.Verify(signedDto, keyResult.Value))
      {
        _logger.LogWarning("Signature verification failed for device {DeviceId} ({DeviceName}).", agentDto.Id, agentDto.Name);
        return Fail("Signature verification failed.");
      }

      if (options.AgentClockSkewTolerance is { } clockSkew &&
          !_keyProvider.VerifyTimestamp(signedDto, clockSkew))
      {
        _logger.LogWarning(
          "Timestamp expired for device {DeviceId}. Are the server and device clocks synchronized?",
          agentDto.Id);
        return Fail("Timestamp expired.");
      }

      if (options.AllowAgentsToSelfBootstrap && agentDto.TenantId == Guid.Empty)
      {
        // Only on a single-tenant server is it obvious where a self-registered device belongs.
        var tenantIds = await _appDb.Tenants
          .Select(x => x.Id)
          .Take(2)
          .ToListAsync();

        switch (tenantIds.Count)
        {
          case 0:
            return Fail("No tenants found.");
          case > 1:
            return Fail("Self-bootstrap is only allowed on single-tenant servers. Use an installer key instead.");
        }

        agentDto = agentDto with { TenantId = tenantIds[0] };
      }

      if (agentDto.TenantId == Guid.Empty ||
          !await _appDb.Tenants.AnyAsync(x => x.Id == agentDto.TenantId))
      {
        return Fail("Invalid tenant ID.");
      }

      // The IP and connection id come from the connection itself, never from what the agent claims.
      var connectionContext = new DeviceConnectionContext(
        ConnectionId: Context.ConnectionId,
        RemoteIpAddress: Context.GetHttpContext()?.Connection.RemoteIpAddress,
        LastSeen: _timeProvider.GetLocalNow(),
        IsOnline: true);

      var saveResult = await SaveDevice(agentDto, connectionContext, publicKeyBase64, options.AllowAgentsToSelfBootstrap);
      if (!saveResult.IsSuccess)
      {
        return Fail(saveResult.Reason);
      }

      Device = saveResult.Value.ToInternalResponseDto(isOutdated: false);
      return HubResult.Ok(Device);
    }
    catch (Exception ex)
    {
      _logger.LogError(ex, "Error while updating signed device.");
      return Fail("An error occurred while updating the device.");
    }
  }

  private static HubResult<InternalDtos.DeviceResponseDto> Fail(string reason)
  {
    return HubResult.Fail<InternalDtos.DeviceResponseDto>(reason);
  }

  private async Task<Result<Device>> SaveDevice(
    DeviceUpdateRequestDto agentDto,
    DeviceConnectionContext connectionContext,
    string? publicKeyBase64,
    bool allowSelfBootstrap)
  {
    if (allowSelfBootstrap)
    {
      var device = await _deviceManager.AddOrUpdate(agentDto, connectionContext, publicKeyBase64);
      return Result.Ok(device);
    }

    // Without self-bootstrap, agents may only update devices that were registered some other way.
    return await _deviceManager.UpdateDevice(agentDto, connectionContext, publicKeyBase64);
  }
}
