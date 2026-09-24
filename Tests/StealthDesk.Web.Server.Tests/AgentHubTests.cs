using System.Net;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Libraries.Api.Contracts.Dtos;
using StealthDesk.Libraries.Api.Contracts.Dtos.HubDtos;
using StealthDesk.Libraries.Api.Contracts.Enums;
using StealthDesk.Libraries.Api.Contracts.Hubs;
using StealthDesk.Libraries.Shared.Constants;
using StealthDesk.Libraries.Shared.Services.Encryption;
using StealthDesk.Web.Server.Data;
using StealthDesk.Web.Server.Tests.Helpers;
using InternalDtos = StealthDesk.Libraries.Api.Contracts.Dtos.ServerApi.Internal;
using V1Dtos = StealthDesk.Libraries.Api.Contracts.Dtos.ServerApi.V1;

namespace StealthDesk.Web.Server.Tests;

public class AgentHubTests
{
  [Fact]
  public async Task Negotiate_ReturnsOk()
  {
    using var factory = new TestAppFactory();
    using var client = factory.CreateClient();

    var response = await client.PostAsync(
      $"{AppConstants.AgentHubPath}/negotiate?negotiateVersion=1",
      content: null,
      TestContext.Current.CancellationToken);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
  }

  [Fact]
  public async Task UpdateDeviceSigned_WithValidSignature_StoresDeviceOnline()
  {
    using var factory = new TestAppFactory();
    await factory.Services.CreateTestTenant();
    var keyProvider = factory.Services.GetRequiredService<IEd25519KeyProvider>();
    var keyPair = keyProvider.GenerateKeyPair();
    var deviceDto = CreateDeviceDto(Guid.NewGuid());

    await using var connection = await ConnectAgent(factory);
    var result = await SendHeartbeat(connection, keyProvider.Sign(deviceDto, keyPair.PrivateKey, ToBase64(keyPair.PublicKey)));

    Assert.True(result.IsSuccess, result.Reason);
    var listedDevice = await GetListedDevice(factory, deviceDto.Id);
    Assert.NotNull(listedDevice);
    Assert.True(listedDevice.IsOnline);
    Assert.Equal(ToBase64(keyPair.PublicKey), await GetStoredPublicKey(factory, deviceDto.Id));
  }

  [Fact]
  public async Task UpdateDeviceSigned_WhenSignatureDoesNotMatchEnvelopeKey_Fails()
  {
    using var factory = new TestAppFactory();
    await factory.Services.CreateTestTenant();
    var keyProvider = factory.Services.GetRequiredService<IEd25519KeyProvider>();
    var signingKeyPair = keyProvider.GenerateKeyPair();
    var envelopeKeyPair = keyProvider.GenerateKeyPair();
    var deviceDto = CreateDeviceDto(Guid.NewGuid());

    await using var connection = await ConnectAgent(factory);
    var result = await SendHeartbeat(
      connection,
      keyProvider.Sign(deviceDto, signingKeyPair.PrivateKey, ToBase64(envelopeKeyPair.PublicKey)));

    Assert.False(result.IsSuccess);
    Assert.Equal("Signature verification failed.", result.Reason);
    Assert.Null(await GetListedDevice(factory, deviceDto.Id));
  }

  [Fact]
  public async Task UpdateDeviceSigned_WhenExistingDeviceUsesNewKey_Fails()
  {
    using var factory = new TestAppFactory();
    await factory.Services.CreateTestTenant();
    var keyProvider = factory.Services.GetRequiredService<IEd25519KeyProvider>();
    var originalKeyPair = keyProvider.GenerateKeyPair();
    var newKeyPair = keyProvider.GenerateKeyPair();
    var deviceDto = CreateDeviceDto(Guid.NewGuid());

    await using var connection = await ConnectAgent(factory);
    var firstResult = await SendHeartbeat(
      connection,
      keyProvider.Sign(deviceDto, originalKeyPair.PrivateKey, ToBase64(originalKeyPair.PublicKey)));
    var secondResult = await SendHeartbeat(
      connection,
      keyProvider.Sign(deviceDto, newKeyPair.PrivateKey, ToBase64(newKeyPair.PublicKey)));

    Assert.True(firstResult.IsSuccess, firstResult.Reason);
    Assert.False(secondResult.IsSuccess);
    Assert.Equal("Signature verification failed.", secondResult.Reason);
    Assert.Equal(ToBase64(originalKeyPair.PublicKey), await GetStoredPublicKey(factory, deviceDto.Id));
  }

  [Fact]
  public async Task UpdateDeviceSigned_WhenTimestampIsOutsideTolerance_Fails()
  {
    using var factory = new TestAppFactory();
    await factory.Services.CreateTestTenant();
    var keyProvider = factory.Services.GetRequiredService<IEd25519KeyProvider>();
    var keyPair = keyProvider.GenerateKeyPair();
    var deviceDto = CreateDeviceDto(Guid.NewGuid());

    // Signs with a clock five minutes ahead, beyond the one-minute tolerance in Development.
    var skewedKeyProvider = new Ed25519KeyProvider(
      new OffsetTimeProvider(TimeSpan.FromMinutes(5)),
      NullLogger<Ed25519KeyProvider>.Instance);

    await using var connection = await ConnectAgent(factory);
    var result = await SendHeartbeat(
      connection,
      skewedKeyProvider.Sign(deviceDto, keyPair.PrivateKey, ToBase64(keyPair.PublicKey)));

    Assert.False(result.IsSuccess);
    Assert.Equal("Timestamp expired.", result.Reason);
  }

  [Fact]
  public async Task Disconnect_MarksDeviceOffline()
  {
    using var factory = new TestAppFactory();
    await factory.Services.CreateTestTenant();
    var keyProvider = factory.Services.GetRequiredService<IEd25519KeyProvider>();
    var keyPair = keyProvider.GenerateKeyPair();
    var deviceDto = CreateDeviceDto(Guid.NewGuid());

    var connection = await ConnectAgent(factory);
    var result = await SendHeartbeat(connection, keyProvider.Sign(deviceDto, keyPair.PrivateKey, ToBase64(keyPair.PublicKey)));
    Assert.True(result.IsSuccess, result.Reason);

    await connection.DisposeAsync();

    // The server handles the disconnect in the background, so wait for it to catch up.
    var isOffline = await WaitUntil(async () => await GetListedDevice(factory, deviceDto.Id) is { IsOnline: false });
    Assert.True(isOffline, "The device was not marked offline after the connection closed.");
  }

  private static async Task<HubConnection> ConnectAgent(TestAppFactory factory)
  {
    var hubUrl = new Uri(factory.Server.BaseAddress, AppConstants.AgentHubPath);

    var connection = new HubConnectionBuilder()
      .WithUrl(hubUrl, options =>
      {
        // The test server has no network listener, so requests go straight to its in-memory handler.
        // That handler can't upgrade to WebSockets, hence long polling.
        options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
      })
      .AddMessagePackProtocol()
      .Build();

    await connection.StartAsync(TestContext.Current.CancellationToken);
    return connection;
  }

  private static DeviceUpdateRequestDto CreateDeviceDto(Guid deviceId)
  {
    return new DeviceUpdateRequestDto(
      Id: deviceId,
      TenantId: Guid.Empty,
      Name: "TEST-PC",
      AgentVersion: "1.0.0",
      Is64Bit: true,
      OsArchitecture: Architecture.X64,
      OsDescription: "Windows 11",
      Platform: SystemPlatform.Windows,
      ProcessorCount: 8,
      CpuUtilization: 0.25,
      TotalMemory: 16,
      TotalStorage: 512,
      UsedMemory: 8,
      UsedStorage: 256,
      CurrentUsers: [],
      MacAddresses: [],
      LocalIpV4: "10.0.0.2",
      LocalIpV6: "",
      Drives: []);
  }

  private static async Task<V1Dtos.DeviceResponseDto?> GetListedDevice(TestAppFactory factory, Guid deviceId)
  {
    using var client = factory.CreateClient();
    var devices = await client.GetFromJsonAsync<List<V1Dtos.DeviceResponseDto>>(
      "/api/v1/devices",
      TestContext.Current.CancellationToken);

    return devices?.FirstOrDefault(x => x.Id == deviceId);
  }

  private static async Task<string?> GetStoredPublicKey(TestAppFactory factory, Guid deviceId)
  {
    using var scope = factory.Services.CreateScope();
    var appDb = scope.ServiceProvider.GetRequiredService<AppDb>();

    return await appDb.Devices
      .Where(x => x.Id == deviceId)
      .Select(x => x.PublicKey)
      .FirstOrDefaultAsync(TestContext.Current.CancellationToken);
  }

  private static Task<HubResult<InternalDtos.DeviceResponseDto>> SendHeartbeat(
    HubConnection connection,
    SignedDto<DeviceUpdateRequestDto> signedDto)
  {
    return connection.InvokeAsync<HubResult<InternalDtos.DeviceResponseDto>>(
      nameof(IAgentHub.UpdateDeviceSigned),
      signedDto,
      TestContext.Current.CancellationToken);
  }

  private static string ToBase64(byte[] bytes) => Convert.ToBase64String(bytes);

  private static async Task<bool> WaitUntil(Func<Task<bool>> condition)
  {
    var timeout = TimeSpan.FromSeconds(5);
    var started = DateTime.UtcNow;

    while (DateTime.UtcNow - started < timeout)
    {
      if (await condition())
      {
        return true;
      }

      await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    return false;
  }

  private sealed class OffsetTimeProvider(TimeSpan offset) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + offset;
  }
}
