using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StealthDesk.Web.Server.Options;

namespace StealthDesk.Web.Server.Tests;

public class AppOptionsTests
{
  [Fact]
  public void AppOptions_BindsFromConfiguration()
  {
    var configuration = new ConfigurationBuilder()
      .AddInMemoryCollection(new Dictionary<string, string?>
      {
        ["AppOptions:AgentClockSkewTolerance"] = "00:00:30",
        ["AppOptions:AllowAgentsToSelfBootstrap"] = "true"
      })
      .Build();

    var options = configuration
      .GetSection(AppOptions.SectionKey)
      .Get<AppOptions>();

    Assert.NotNull(options);
    Assert.Equal(TimeSpan.FromSeconds(30), options.AgentClockSkewTolerance);
    Assert.True(options.AllowAgentsToSelfBootstrap);
  }

  [Fact]
  public void AppOptions_WhenToleranceIsMissing_DisablesTimestampCheck()
  {
    var configuration = new ConfigurationBuilder().Build();

    var options = configuration
      .GetSection(AppOptions.SectionKey)
      .Get<AppOptions>() ?? new AppOptions();

    Assert.Null(options.AgentClockSkewTolerance);
    Assert.False(options.AllowAgentsToSelfBootstrap);
  }

  [Fact]
  public void AppOptions_InDevelopment_AllowsSelfBootstrap()
  {
    using var factory = new TestAppFactory();

    var options = factory.Services.GetRequiredService<IOptions<AppOptions>>().Value;

    Assert.True(options.AllowAgentsToSelfBootstrap);
    Assert.Equal(TimeSpan.FromMinutes(1), options.AgentClockSkewTolerance);
  }
}
