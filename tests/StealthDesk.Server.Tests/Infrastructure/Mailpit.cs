using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>A real SMTP server in Docker whose API lists what it received. One for the whole test run.</summary>
public static class Mailpit
{
  private const int SmtpPort = 1025;
  private const int ApiPort = 8025;

  private static readonly Lazy<Task<IContainer>> _container = new(StartAsync);

  public static async Task<(string Host, int Port)> SmtpAsync()
  {
    var container = await _container.Value;
    return (container.Hostname, container.GetMappedPublicPort(SmtpPort));
  }

  public static async Task<Uri> ApiAsync()
  {
    var container = await _container.Value;
    return new Uri($"http://{container.Hostname}:{container.GetMappedPublicPort(ApiPort)}/");
  }

  private static async Task<IContainer> StartAsync()
  {
    var container = new ContainerBuilder("axllent/mailpit:v1.27")
      .WithPortBinding(SmtpPort, assignRandomHostPort: true)
      .WithPortBinding(ApiPort, assignRandomHostPort: true)
      .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(ApiPort).ForPath("/api/v1/info")))
      .Build();

    await container.StartAsync();
    return container;
  }
}
