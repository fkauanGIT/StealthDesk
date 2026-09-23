using StealthDesk.Libraries.DataRedaction;
using StealthDesk.Web.Server.Services.DeviceManagement;

namespace StealthDesk.Web.Server.Startup;

public static class WebApplicationBuilderExtensions
{
  public static WebApplicationBuilder AddStealthDeskServer(this WebApplicationBuilder builder)
  {
    builder.AddServiceDefaults(ServiceNames.Stealthdesk);

    builder.Services.AddStarRedactor();

    if (builder.Configuration.GetValue<bool>("UseInMemoryDatabase"))
    {
      builder.AddStealthDeskInMemoryDb();
    }
    else
    {
      builder.AddStealthDeskPostgresDb();
    }

    builder.Services.AddControllers();
    builder.Services.AddOutputCache();

    builder.Services.AddScoped<IDeviceManager, DeviceManager>();

    return builder;
  }
}
