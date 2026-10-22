namespace StealthDesk.Web.Server.Permissions;

/// <summary>
/// The endpoint or hub method checks this permission itself, because a policy can't: a list is filtered, a single
/// resource must be loaded before it can be checked, or any of several permissions lets the caller in.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class ChecksPermissionAttribute(string permission) : Attribute
{
  public string Permission { get; } = permission;
}

/// <summary>A signed-in user may call it without any permission, for the reason given (e.g. it only touches their own account).</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class NoPermissionAttribute(string reason) : Attribute
{
  public const string OwnAccount = "It only reads or changes the signed-in user's own account.";

  public string Reason { get; } = reason;
}

public static class PermissionEndpointExtensions
{
  public static TBuilder ChecksPermission<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder =>
    builder.WithMetadata(new ChecksPermissionAttribute(permission));

  public static TBuilder NoPermission<TBuilder>(this TBuilder builder, string reason) where TBuilder : IEndpointConventionBuilder =>
    builder.WithMetadata(new NoPermissionAttribute(reason));

  public static TBuilder OwnAccount<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
    builder.NoPermission(NoPermissionAttribute.OwnAccount);
}
