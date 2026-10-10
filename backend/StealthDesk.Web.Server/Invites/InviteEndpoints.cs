using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Invites;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Server.Accounts;
using StealthDesk.Web.Server.AuthorizationLogs;
using StealthDesk.Web.Server.Email;
using StealthDesk.Web.Server.Permissions;
using StealthDesk.Web.Server.Users;

namespace StealthDesk.Web.Server.Invites;

/// <summary>
/// Inviting people into a tenant. Inviting creates the account in the tenant right away, with a password nobody
/// knows; the invite link lets whoever has it, with the invited email, choose that password. The link is shown to
/// whoever may manage users, and emailed when the server sends email.
/// </summary>
public static class InviteEndpoints
{
  private const string CodeAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

  public static IEndpointRouteBuilder MapInviteEndpoints(this IEndpointRouteBuilder endpoints)
  {
    endpoints.MapPost(Routes.Invites, CreateAsync)
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUsersWrite));

    // Readers of the tenant's users see who is invited; only those who may manage users get the link, which is
    // enough to take over the account.
    endpoints.MapGet(Routes.Invites, async (
        Guid? tenantId,
        HttpRequest request,
        ClaimsPrincipal caller,
        IPermissionEvaluator evaluator,
        StealthDeskDb db,
        CancellationToken cancellationToken) =>
      {
        if (Principal.From(caller) is not { } principal || principal.TenantFor(tenantId ?? Guid.Empty) is not { } tenant)
        {
          return Results.Forbid();
        }

        var managesUsers = (await evaluator.EvaluateAsync(principal, PermissionNames.TenantUsersWrite, Resource.Tenant(tenant), cancellationToken)).Allowed;
        var invites = await db.TenantInvites
          .IgnoreQueryFilters()
          .Where(x => x.TenantId == tenant)
          .OrderBy(x => x.CreatedAt)
          .ToListAsync(cancellationToken);

        return Results.Ok(new TenantInviteList
        {
          Items = [.. invites.Select(x => ToContract(x, request, managesUsers))],
        });
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUsersRead));

    endpoints.MapDelete($"{Routes.Invites}/{{id:guid}}", async (
        Guid id,
        Guid? tenantId,
        ClaimsPrincipal caller,
        UserManager<UserRecord> users,
        StealthDeskDb db) =>
      {
        if (Principal.From(caller)?.TenantFor(tenantId ?? Guid.Empty) is not { } tenant)
        {
          return Results.Forbid();
        }

        if (await db.TenantInvites.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id) is not { } invite)
        {
          return Results.NotFound();
        }

        if (invite.TenantId != tenant)
        {
          return Results.Forbid();
        }

        db.TenantInvites.Remove(invite);
        await db.SaveChangesAsync();

        // The account was made for the invite and never used: it goes with it. Only from the invite's own tenant.
        if (await users.FindByEmailAsync(invite.InviteeEmail) is { } user && user.TenantId == invite.TenantId)
        {
          await users.DeleteAsync(user);
        }

        return Results.NoContent();
      })
      .RequireAuthorization(PermissionPolicies.For(PermissionNames.TenantUsersWrite));

    endpoints.MapPost(Routes.AcceptInvite, AcceptAsync).AllowAnonymous();

    return endpoints;
  }

  private static async Task<IResult> CreateAsync(
    Guid? tenantId,
    CreateInviteRequest body,
    HttpRequest request,
    ClaimsPrincipal caller,
    UserManager<UserRecord> users,
    PermissionSeeder seeder,
    StealthDeskDb db,
    AccountEmails emails,
    IOptions<IdentityOptions> identity,
    ILogger<TenantInviteRecord> logger,
    CancellationToken cancellationToken)
  {
    if (Principal.From(caller)?.TenantFor(tenantId ?? Guid.Empty) is not { } tenant)
    {
      return Results.Forbid();
    }

    var email = Normalize(body.InviteeEmail);
    if (email.Length is 0 or > TenantInviteRecord.EmailMax || !email.Contains('@'))
    {
      return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(body.InviteeEmail)] = ["Enter a valid email address."] });
    }

    // Across every tenant: an address belongs to one account, which belongs to one tenant.
    if (await db.TenantInvites.IgnoreQueryFilters().AnyAsync(x => x.InviteeEmail == email, cancellationToken))
    {
      return Results.Problem("This email already has a pending invite.", statusCode: StatusCodes.Status409Conflict);
    }

    if (await db.Users.IgnoreQueryFilters().AnyAsync(x => x.NormalizedEmail == users.NormalizeEmail(email), cancellationToken))
    {
      return Results.Problem("This email already has an account.", statusCode: StatusCodes.Status409Conflict);
    }

    var user = new UserRecord { UserName = email, Email = email, TenantId = tenant };
    var created = await TenantMembers.CreateAsync(users, seeder, user, UnknownPassword(identity.Value.Password), [], cancellationToken);
    if (!created.Succeeded)
    {
      return created.Errors.Any(x => x.Code is nameof(IdentityErrorDescriber.DuplicateUserName) or nameof(IdentityErrorDescriber.DuplicateEmail))
        ? Results.Problem("This email already has an account.", statusCode: StatusCodes.Status409Conflict)
        : ManageEndpoints.ValidationProblem(created);
    }

    var invite = new TenantInviteRecord
    {
      TenantId = tenant,
      InviteeEmail = email,
      ActivationCode = RandomNumberGenerator.GetString(CodeAlphabet, TenantInviteRecord.ActivationCodeLength),
    };
    db.TenantInvites.Add(invite);
    await db.SaveChangesAsync(cancellationToken);

    var contract = ToContract(invite, request, includeCode: true);
    try
    {
      await emails.SendInviteAsync(email, contract.InviteUrl);
    }
    catch (Exception ex)
    {
      // The invite stands: its link can still be copied and shared another way.
      logger.LogWarning(ex, "Could not email the invite for {Email}; its link can still be shared.", email);
    }

    return Results.Created($"{Routes.Invites}?tenantId={tenant}", contract);
  }

  private static async Task<IResult> AcceptAsync(
    AcceptInviteRequest body,
    UserManager<UserRecord> users,
    PermissionSeeder seeder,
    IAuthorizationChangeFactory changes,
    StealthDeskDb db,
    ILogger<TenantInviteRecord> logger,
    CancellationToken cancellationToken)
  {
    var email = Normalize(body.Email);
    var invite = await db.TenantInvites
      .IgnoreQueryFilters()
      .FirstOrDefaultAsync(x => x.ActivationCode == body.ActivationCode && x.InviteeEmail == email, cancellationToken);
    if (invite is null || await users.FindByEmailAsync(email) is not { } user || user.TenantId != invite.TenantId)
    {
      // The same answer for a wrong code and a wrong email: trying reveals neither.
      logger.LogWarning("An invite was accepted with a code and email that don't match one.");
      return Results.NotFound();
    }

    var reset = await users.ResetPasswordAsync(user, await users.GeneratePasswordResetTokenAsync(user), body.Password);
    if (!reset.Succeeded)
    {
      return ManageEndpoints.ValidationProblem(reset);
    }

    // The account starts over in the tenant: whatever it was granted before is removed, each removal logged, and
    // the baseline granted again below.
    var stale = await db.PermissionAssignments
      .IgnoreQueryFilters()
      .Where(x => x.PrincipalKind == PermissionPrincipalKind.User && x.PrincipalId == user.Id)
      .ToListAsync(cancellationToken);
    foreach (var assignment in stale)
    {
      db.AuthorizationChanges.Add(changes.Create(
        AuthorizationChangeActions.PermissionAssignmentDeleted,
        actor: null,
        AuthorizationChangeTargets.PermissionAssignment,
        assignment.Id,
        assignment.OwningTenantId,
        before: PermissionAssignmentSnapshot.Of(assignment)));
    }

    db.PermissionAssignments.RemoveRange(stale);
    db.UserGroupMembers.RemoveRange(await db.UserGroupMembers.IgnoreQueryFilters().Where(x => x.UserId == user.Id).ToListAsync(cancellationToken));
    db.TenantInvites.Remove(invite);
    await db.SaveChangesAsync(cancellationToken);

    await seeder.SeedAsync(user.Id, invite.TenantId, PermissionPresets.Baseline, cancellationToken);
    logger.LogInformation("{Email} accepted the invite into tenant {TenantId}.", email, invite.TenantId);
    return Results.NoContent();
  }

  private static TenantInvite ToContract(TenantInviteRecord invite, HttpRequest request, bool includeCode)
  {
    var page = $"{request.Scheme}://{request.Host}{request.PathBase}/{Routes.InviteConfirmationPage}";
    return new TenantInvite(invite.Id, invite.CreatedAt, invite.InviteeEmail, includeCode ? $"{page}/{invite.ActivationCode}" : page);
  }

  private static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

  // Long, random and never shown: the account can't be signed in to until the invite is accepted.
  private static string UnknownPassword(PasswordOptions rules) => TemporaryPasswords.Generate(new PasswordOptions
  {
    RequiredLength = Math.Max(rules.RequiredLength, TenantInviteRecord.ActivationCodeLength),
    RequiredUniqueChars = rules.RequiredUniqueChars,
    RequireDigit = true,
    RequireLowercase = true,
    RequireUppercase = true,
    RequireNonAlphanumeric = rules.RequireNonAlphanumeric,
  });
}
