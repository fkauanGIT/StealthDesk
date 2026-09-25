using StealthDesk.Libraries.DataRedaction;
using StealthDesk.Libraries.Shared.Services.Encryption;
using StealthDesk.Web.Server.Services.DeviceManagement;

namespace StealthDesk.Web.Server.Startup;

public static class WebApplicationBuilderExtensions
{
  public static WebApplicationBuilder AddStealthDeskServer(this WebApplicationBuilder builder)
  {
    builder.AddServiceDefaults(ServiceNames.Stealthdesk);

    builder.Services.AddStarRedactor();

    builder.Services.Configure<AppOptions>(
      builder.Configuration.GetSection(AppOptions.SectionKey));

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

    builder.Services
      .AddSignalR()
      .AddMessagePackProtocol();

    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<IEd25519KeyProvider, Ed25519KeyProvider>();
    builder.Services.AddScoped<IDeviceManager, DeviceManager>();

    return builder;
  }
}
