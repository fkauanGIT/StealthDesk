using StealthDesk.Libraries.Api.Contracts.Dtos;
using StealthDesk.Libraries.Api.Contracts.Dtos.HubDtos;

namespace StealthDesk.Libraries.Api.Contracts.Hubs;

public interface IAgentHub
{
  Task<HubResult<DeviceResponseDto>> UpdateDeviceSigned(SignedDto<DeviceUpdateRequestDto> signedDto);
}
