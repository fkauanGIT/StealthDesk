using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StealthDesk.Libraries.Api.Contracts.Constants;
using StealthDesk.Libraries.Api.Contracts.Dtos.ServerApi.V1;
using StealthDesk.Web.Server.Data;
using StealthDesk.Web.Server.Extensions.Dtos.V1;

namespace StealthDesk.Web.Server.Api.V1;

[Route(HttpConstants.V1.DevicesEndpoint)]
[ApiController]
public class DevicesController : ControllerBase
{
  [HttpGet]
  public async IAsyncEnumerable<DeviceResponseDto> Get(
    [FromServices] AppDb appDb,
    [EnumeratorCancellation] CancellationToken cancellationToken)
  {
    var query = appDb.Devices.AsNoTracking();

    await foreach (var device in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
    {
      yield return device.ToV1ResponseDto(isOutdated: false);
    }
  }
}
