using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Invites;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Invites;
using StealthDesk.Web.Client.Pages.Account;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The invites page an administrator uses, and the page an invite link opens.</summary>
public class InvitePagesTests : AccountTestContext
{
  private static readonly Guid Tenant = Guid.NewGuid();

  public InvitePagesTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<InviteApi>();
    JSInterop.Mode = JSRuntimeMode.Loose;
  }

  private static CurrentUser Admin => new()
  {
    Id = Guid.NewGuid(),
    Email = "admin@example.com",
    TenantId = Tenant,
    Permissions = [PermissionNames.TenantUsersRead, PermissionNames.TenantUsersWrite],
  };

  private static TenantInvite Invite(string email) =>
    new(Guid.NewGuid(), new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), email, $"http://localhost/invite-confirmation/{new string('a', 64)}");

  [Fact]
  public void Inviting_SendsTheEmail_ShowsTheLinkToCopy_AndListsTheInvite()
  {
    var joao = Invite("joao@example.com");
    Api.RespondWith(Admin);
    Api.RespondWith(new TenantInviteList());
    var page = RenderInvites();
    page.WaitForAssertion(() => Assert.Contains("No pending invites", page.Markup));

    Fill(page, "Invite a new user", "joao@example.com");
    Api.Respond(HttpStatusCode.Created, joao);
    Api.RespondWith(new TenantInviteList { Items = [joao] });
    page.Find("form.sd-invite-form").Submit();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.Equal($"POST {Routes.Invites}?tenantId={Tenant}", Api.Requests[2]);
    Assert.Contains("\"inviteeEmail\":\"joao@example.com\"", Api.Bodies[2]);
    Assert.Contains("joao@example.com is invited", page.Markup);

    page.FindAll("tr[data-invite='joao@example.com'] button").Single(x => x.TextContent.Trim() == "Copy link").Click();

    Assert.Equal(joao.InviteUrl, JSInterop.Invocations["navigator.clipboard.writeText"].Single().Arguments[0]);
    page.WaitForAssertion(() => Assert.Contains("Copied", page.Find("tr[data-invite='joao@example.com']").TextContent));
  }

  [Fact]
  public void AnEmailThatCantBeInvited_SaysWhyUnderTheField()
  {
    Api.RespondWith(Admin);
    Api.RespondWith(new TenantInviteList());
    var page = RenderInvites();
    page.WaitForAssertion(() => Assert.Contains("No pending invites", page.Markup));

    Fill(page, "Invite a new user", "admin@example.com");
    Api.Respond(HttpStatusCode.Conflict, new { detail = "This email already has an account." });
    page.Find("form.sd-invite-form").Submit();

    page.WaitForAssertion(() => Assert.Equal("This email already has an account.", ErrorOf(page, "Invite a new user").TextContent));
  }

  [Fact]
  public void Delete_AsksFirst_ThenRemovesTheInvite()
  {
    var joao = Invite("joao@example.com");
    Api.RespondWith(Admin);
    Api.RespondWith(new TenantInviteList { Items = [joao] });
    var page = RenderInvites();
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));

    page.FindAll("tr[data-invite='joao@example.com'] button").Single(x => x.TextContent.Trim() == "Delete").Click();
    Assert.Contains("The link for joao@example.com stops working", page.Find(".sd-dialog").TextContent);
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new TenantInviteList());
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Delete").Click();

    page.WaitForAssertion(() => Assert.Contains("No pending invites", page.Markup));
    Assert.Equal($"DELETE {Routes.Invite(joao.Id)}?tenantId={Tenant}", Api.Requests[2]);
  }

  [Fact]
  public void Accepting_SendsTheCodeEmailAndPassword_ThenOffersToSignIn()
  {
    Api.Respond(HttpStatusCode.Unauthorized);
    var page = RenderConfirmation("abc123");
    page.WaitForAssertion(() => Assert.Contains("Accept the invitation", page.Markup));

    Fill(page, "Email", "joao@example.com");
    Fill(page, "Password", "Brand-new-Passw0rd");
    Fill(page, "Confirm password", "Brand-new-Passw0rd");
    Api.Respond(HttpStatusCode.NoContent);
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Your account is ready", page.Markup));
    Assert.Equal($"POST {Routes.AcceptInvite}", Api.Requests[1]);
    Assert.Contains("\"activationCode\":\"abc123\"", Api.Bodies[1]);
    Assert.Contains("\"email\":\"joao@example.com\"", Api.Bodies[1]);
  }

  [Fact]
  public void AWrongInvite_SaysSo_AndMismatchedPasswordsNeverLeave()
  {
    Api.Respond(HttpStatusCode.Unauthorized);
    var page = RenderConfirmation("abc123");
    page.WaitForAssertion(() => Assert.Contains("Accept the invitation", page.Markup));

    Fill(page, "Email", "joao@example.com");
    Fill(page, "Password", "Brand-new-Passw0rd");
    Fill(page, "Confirm password", "Different-Passw0rd");
    page.Find("form").Submit();
    Assert.Equal("The passwords don't match.", ErrorOf(page, "Confirm password").TextContent);
    Assert.Single(Api.Requests);

    Fill(page, "Confirm password", "Brand-new-Passw0rd");
    Api.Respond(HttpStatusCode.NotFound);
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("doesn't exist, was already used, or isn't for this email", page.Markup));
  }

  [Fact]
  public void SignedIn_TheLinkAsksToSignOutFirst()
  {
    Api.RespondWith(Admin);

    var page = RenderConfirmation("abc123");

    page.WaitForAssertion(() => Assert.Contains("Sign out to accept an invitation", page.Markup));
    Assert.Empty(page.FindAll("form"));
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderInvites() =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.Invites>());

  private IRenderedComponent<CascadingAuthenticationState> RenderConfirmation(string code) =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<InviteConfirmation>(p => p.Add(c => c.Code, code)));
}
