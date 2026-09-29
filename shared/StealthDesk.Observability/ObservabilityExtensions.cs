using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace StealthDesk.Observability;

public static class ObservabilityExtensions
{

  private const string LivenessTag = "liveness";

  /// <summary>
  /// Health checks plus OpenTelemetry logs, metrics and traces. Telemetry is only exported when
  /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured; otherwise it stays in-process.
  /// </summary>
  /// <param name="instanceId">Identifies this instance among others of the same service (e.g. a device id).</param>
  public static IHostApplicationBuilder AddObservability(this IHostApplicationBuilder builder, string serviceName, string? instanceId = null)
  {
    builder.Services
      .AddHealthChecks()
      .AddCheck("process", () => HealthCheckResult.Healthy(), [LivenessTag]);

    void DescribeService(ResourceBuilder resource) =>
      resource.AddService(serviceName, serviceNamespace: "stealthdesk", serviceInstanceId: instanceId);

    builder.Logging.AddOpenTelemetry(logging =>
    {
      var resource = ResourceBuilder.CreateDefault();
      DescribeService(resource);
      logging.SetResourceBuilder(resource);
      logging.IncludeFormattedMessage = true;
      logging.IncludeScopes = true;
    });

    var telemetry = builder.Services.AddOpenTelemetry()
      .ConfigureResource(DescribeService)
      .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation())
      .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation(options => options.Filter = context => !IsHealthProbe(context.Request.Path))
        .AddHttpClientInstrumentation());

    if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
    {
      telemetry.UseOtlpExporter();
    }

    return builder;
  }

  /// <summary>
  /// <c>/health</c>: every check must pass (ready for traffic). <c>/alive</c>: only liveness checks (process responsive).
  /// </summary>
  public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapHealthChecks("/health");
    endpoints.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = check => check.Tags.Contains(LivenessTag) });
    return endpoints;
  }

  private static bool IsHealthProbe(Microsoft.AspNetCore.Http.PathString path) =>
    path.StartsWithSegments("/health") || path.StartsWithSegments("/alive");
}
