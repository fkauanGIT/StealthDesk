using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Server.Tests;

/// <summary>
/// Every API route and hub method says how it is authorized, so one added without a permission fails here instead
/// of quietly serving every signed-in user.
/// </summary>
public class EndpointPermissionTests
{
  [Fact]
  public void EveryApiAndHubRoute_IsAnonymousOrNamesItsPermission()
  {
    using var server = ServerHost.InMemory();
    var routes = server.Services.GetRequiredService<EndpointDataSource>().Endpoints
      .OfType<RouteEndpoint>()
      .Where(x => x.RoutePattern.RawText is { } path && (path.StartsWith("/api", StringComparison.Ordinal) || path.StartsWith("/hubs", StringComparison.Ordinal)))
      .ToList();

    var unexplained = routes.Where(x => Explain(x.Metadata) is null).Select(x => $"{Methods(x)} {x.RoutePattern.RawText}").ToList();

    Assert.NotEmpty(routes);
    Assert.True(unexplained.Count == 0, "Routes with no anonymous marker, permission policy, ChecksPermission or NoPermission:\n" + string.Join("\n", unexplained));
  }

  // Marking a whole group anonymous would silently open a route inside it that requires sign-in.
  [Fact]
  public void NoRoute_RequiresSignInAndIsAnonymousAtOnce()
  {
    using var server = ServerHost.InMemory();

    var contradictory = server.Services.GetRequiredService<EndpointDataSource>().Endpoints
      .OfType<RouteEndpoint>()
      .Where(x => x.Metadata.GetMetadata<IAllowAnonymous>() is not null && x.Metadata.GetOrderedMetadata<IAuthorizeData>().Count > 0)
      .Select(x => $"{Methods(x)} {x.RoutePattern.RawText}")
      .ToList();

    Assert.True(contradictory.Count == 0, "Routes that require sign-in but are also marked anonymous:\n" + string.Join("\n", contradictory));
  }

  [Fact]
  public void EveryMethodOfAUserHub_NamesItsPermission()
  {
    var unexplained = UserHubs()
      .SelectMany(hub => hub.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
        .Where(x => !x.IsSpecialName && x.Name is not (nameof(Hub.OnConnectedAsync) or nameof(Hub.OnDisconnectedAsync)))
        .Where(x => x.GetCustomAttribute<ChecksPermissionAttribute>() is null && x.GetCustomAttribute<NoPermissionAttribute>() is null)
        .Select(x => $"{hub.Name}.{x.Name}"))
      .ToList();

    Assert.NotEmpty(UserHubs());
    Assert.True(unexplained.Count == 0, "Hub methods with no ChecksPermission or NoPermission:\n" + string.Join("\n", unexplained));
  }

  [Fact]
  public void NamedPermissions_AreInTheCatalog()
  {
    using var server = ServerHost.InMemory();
    var fromRoutes = server.Services.GetRequiredService<EndpointDataSource>().Endpoints
      .SelectMany(x => x.Metadata.GetOrderedMetadata<ChecksPermissionAttribute>())
      .Select(x => x.Permission);
    var fromHubs = UserHubs()
      .SelectMany(x => x.GetMethods())
      .Select(x => x.GetCustomAttribute<ChecksPermissionAttribute>()?.Permission)
      .OfType<string>();

    var unknown = fromRoutes.Concat(fromHubs).Where(x => !PermissionCatalog.Contains(x)).Distinct().ToList();

    Assert.Empty(unknown);
  }

  private static string? Explain(EndpointMetadataCollection metadata)
  {
    if (metadata.GetMetadata<IAllowAnonymous>() is not null)
    {
      return "anonymous";
    }

    var authorize = metadata.GetOrderedMetadata<IAuthorizeData>();
    if (authorize.Count == 0)
    {
      return null;
    }

    if (authorize.Select(x => x.Policy is null ? null : PermissionPolicies.PermissionOf(x.Policy)).OfType<string>().FirstOrDefault() is { } permission)
    {
      return permission;
    }

    return metadata.GetMetadata<ChecksPermissionAttribute>()?.Permission ?? metadata.GetMetadata<NoPermissionAttribute>()?.Reason;
  }

  private static string Methods(RouteEndpoint endpoint) =>
    string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["ANY"]);

  // Hubs signed-in users call; the agent gateway is anonymous at the HTTP level because agents sign each report.
  private static List<Type> UserHubs() =>
    [.. typeof(PermissionCatalog).Assembly.GetTypes()
      .Where(x => !x.IsAbstract && typeof(Hub).IsAssignableFrom(x) && x.GetCustomAttribute<AllowAnonymousAttribute>() is null)];
}
