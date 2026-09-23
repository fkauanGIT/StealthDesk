using StealthDesk.Libraries.DataRedaction;

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

    return builder;
  }
}
