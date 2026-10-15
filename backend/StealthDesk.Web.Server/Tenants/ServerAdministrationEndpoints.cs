using System.Security.Claims;
using System.Text.Encodings.Web;
using StealthDesk.Branding;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.ServerSettings;
using StealthDesk.Contracts.Tenants;
using StealthDesk.Web.Server.Email;
using StealthDesk.Web.Server.Permissions;

namespace StealthDesk.Web.Server.Tenants;

/// <summary>What the server's administrators manage: its tenants (<c>server.tenants.*</c>) and its settings (<c>server.settings.write</c>).</summary>
public static class ServerAdministrationEndpoints
{
  public static IEndpointRouteBuilder MapServerAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
  {
    var one = $"{Routes.Tenants}/{{id:guid}}";

    endpoints.MapGet(Routes.Tenants, async (TenantService tenants, CancellationToken cancellationToken) =>
        Results.Ok(new TenantList { Items = await tenants.ListAsync(cancellationToken) }))
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.ServerTenantsRead));

    endpoints.MapGet(one, (Guid id, TenantService tenants, CancellationToken cancellationToken) => tenants.GetAsync(id, cancellationToken))
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.ServerTenantsRead));

    endpoints.MapPost(Routes.Tenants, (CreateTenantRequest request, TenantService tenants, CancellationToken cancellationToken) =>
        tenants.CreateAsync(request.Name, cancellationToken))
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.ServerTenantsWrite));

    endpoints.MapPut(one, (Guid id, RenameTenantRequest request, TenantService tenants, CancellationToken cancellationToken) =>
        tenants.RenameAsync(id, request.Name, cancellationToken))
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.ServerTenantsWrite));

    endpoints.MapDelete(one, async (Guid id, ClaimsPrincipal caller, TenantService tenants, CancellationToken cancellationToken) =>
        Principal.From(caller) is { } actor ? await tenants.DeleteAsync(actor, id, cancellationToken) : Results.Forbid())
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.ServerTenantsDelete));

    // Everyone signed in sees the alert; only the server's administrators change it.
    endpoints.MapGet(Routes.ServerAlert, async (StealthDeskDb db, CancellationToken cancellationToken) =>
        await db.ServerAlerts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == ServerAlertRecord.SingletonId, cancellationToken) is { } alert
          ? Results.Ok(ToContract(alert))
          : Results.NoContent())
      .RequireAuthorization()
      .WithMetadata(new NoPermissionAttribute("Every signed-in user is shown the server's alert."));

    endpoints.MapPut(Routes.ServerAlert, async (ServerAlert request, StealthDeskDb db, TimeProvider clock, CancellationToken cancellationToken) =>
      {
        var message = request.Message?.Trim() ?? string.Empty;
        if (message.Length > ServerAlert.MessageMax || (request.IsEnabled && message.Length == 0) || !Enum.IsDefined(request.Severity))
        {
          return Results.Problem(
            $"An alert shown needs a message of at most {ServerAlert.MessageMax} characters and a known severity.",
            statusCode: StatusCodes.Status400BadRequest);
        }

        var alert = await db.ServerAlerts.FirstOrDefaultAsync(x => x.Id == ServerAlertRecord.SingletonId, cancellationToken);
        if (alert is null)
        {
          alert = new ServerAlertRecord { Id = ServerAlertRecord.SingletonId };
          db.ServerAlerts.Add(alert);
        }

        alert.Message = message;
        alert.Severity = request.Severity;
        alert.IsDismissible = request.IsDismissible;
        alert.ComesBack = request.ComesBack;
        alert.IsEnabled = request.IsEnabled;
        alert.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return Results.Ok(ToContract(alert));
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.ServerSettingsWrite));

    // The message only ever goes to the administrator's own address, never one from the request.
    endpoints.MapPost(Routes.ServerTestEmail, async (ClaimsPrincipal caller, StealthDeskDb db, IEmailTransport email, CancellationToken cancellationToken) =>
      {
        if (Principal.From(caller) is not { Kind: PermissionPrincipalKind.User } actor
          || await db.Users.IgnoreQueryFilters().Where(x => x.Id == actor.Id).Select(x => x.Email).FirstOrDefaultAsync(cancellationToken) is not { Length: > 0 } to)
        {
          return Results.Problem("Only a signed-in user with an email address can receive the test message.", statusCode: StatusCodes.Status400BadRequest);
        }

        var delivery = await email.TrySendAsync(
          new EmailMessage(to, $"{Brand.Name} test email", $"<p>This is a test message from your {HtmlEncoder.Default.Encode(Brand.Name)} server. Email works.</p>"),
          cancellationToken);
        return delivery.Status switch
        {
          EmailDeliveryStatus.Sent => Results.Ok(new TestEmailResult(to, "Sent.")),
          EmailDeliveryStatus.Disabled => Results.Ok(new TestEmailResult(to, "Email sending is turned off; the message went to the server's log.")),
          _ => Results.Problem($"The message couldn't be sent: {delivery.Error}", statusCode: StatusCodes.Status502BadGateway),
        };
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.ServerSettingsWrite));

    return endpoints;
  }

  private static ServerAlert ToContract(ServerAlertRecord x) => new(x.Message, x.Severity, x.IsDismissible, x.ComesBack, x.IsEnabled);
}
