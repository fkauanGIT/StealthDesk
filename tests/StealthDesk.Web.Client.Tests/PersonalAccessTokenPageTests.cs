using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.PersonalAccessTokens;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.UserGroups;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Permissions;
using StealthDesk.Web.Client.PersonalAccessTokens;
using StealthDesk.Web.Client.ServiceAccounts;
using StealthDesk.Web.Client.UserGroups;
using StealthDesk.Web.Client.Users;

namespace StealthDesk.Web.Client.Tests;

/// <summary>
/// The access tokens page: creating one and seeing its secret once, what the list tells about each, renaming and
/// deleting, and granting a token its permissions.
/// </summary>
public class PersonalAccessTokenPageTests : AccountTestContext
{
  private static readonly Guid Tenant = Guid.NewGuid();
  private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

  public PersonalAccessTokenPageTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<PersonalAccessTokenApi>();
    Services.AddSingleton<ServiceAccountApi>();
    Services.AddSingleton<PermissionApi>();
    Services.AddSingleton<UserGroupApi>();
    Services.AddSingleton<UserApi>();
    Services.AddSingleton<TimeProvider>(new FixedClock(Now));
    JSInterop.Mode = JSRuntimeMode.Loose;
  }

  private static CurrentUser Me(params string[] permissions) => new()
  {
    Id = Guid.NewGuid(),
    Email = "tech@example.com",
    TenantId = Tenant,
    Permissions = [PermissionNames.PersonalAccessTokenSelfRead, .. permissions],
  };

  private static PersonalAccessTokenSummary Token(
    string name,
    PersonalAccessTokenMode mode = PersonalAccessTokenMode.Restricted,
    DateTimeOffset? expires = null,
    DateTimeOffset? lastUsed = null,
    int permissions = 0) =>
    new(Guid.NewGuid(), Guid.NewGuid(), name, mode, Now.AddDays(-10), expires, lastUsed, permissions);

  [Fact]
  public void Creating_SendsTheChoices_ShowsTheSecretOnce_ThenOpensTheTokensPermissions()
  {
    var backup = Token("Nightly backup", expires: Now.AddDays(365));
    Api.RespondWith(Me(PermissionNames.PersonalAccessTokenSelfWrite, PermissionNames.TenantPermissionsRead));
    Api.RespondWith(new PersonalAccessTokenList());
    var page = RenderTokens();
    page.WaitForAssertion(() => Assert.Contains("No access tokens", page.Markup));

    Fill(page, "Token name", "Nightly backup");
    Input(page, "Expires").Change("365");
    Api.Respond(HttpStatusCode.Created, new CreatedPersonalAccessToken(backup, $"{backup.Id}:secret-shown-once"));
    Api.RespondWith(new PersonalAccessTokenList { Items = [backup] });
    page.Find("form.sd-token-form").Submit();

    page.WaitForAssertion(() => Assert.Equal($"{backup.Id}:secret-shown-once", page.Find("[data-secret]").TextContent));
    Assert.Equal($"POST {Routes.PersonalAccessTokens}", Api.Requests[2]);
    Assert.Contains("\"name\":\"Nightly backup\"", Api.Bodies[2]);
    Assert.Contains($"\"mode\":{(int)PersonalAccessTokenMode.Restricted}", Api.Bodies[2]);
    Assert.Contains("\"expiresInDays\":365", Api.Bodies[2]);
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.Empty(Input(page, "Token name").GetAttribute("value") ?? string.Empty);

    Button(page, "Copy token").Click();
    Assert.Equal($"{backup.Id}:secret-shown-once", JSInterop.Invocations["navigator.clipboard.writeText"].Single().Arguments[0]);
    page.WaitForAssertion(() => Assert.NotNull(page.FindAll("button").FirstOrDefault(x => x.TextContent.Trim() == "Copied")));
    Button(page, "Done").Click();

    page.WaitForAssertion(() => Assert.Equal($"permissions?kind=PersonalAccessToken&id={backup.Id}", Location));
    Assert.DoesNotContain("secret-shown-once", page.Markup);
  }

  [Fact]
  public void AnInheritingToken_NeverExpiring_StaysOnThePage_AndSaysItHasYourPermissions()
  {
    var everything = Token("Everything", PersonalAccessTokenMode.InheritOwner);
    Api.RespondWith(Me(PermissionNames.PersonalAccessTokenSelfWrite, PermissionNames.TenantPermissionsRead));
    Api.RespondWith(new PersonalAccessTokenList());
    var page = RenderTokens();
    page.WaitForAssertion(() => Assert.Contains("No access tokens", page.Markup));

    Fill(page, "Token name", "Everything");
    page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim() == "Inherit owner").Click();
    Input(page, "Expires").Change(string.Empty);
    Api.Respond(HttpStatusCode.Created, new CreatedPersonalAccessToken(everything, $"{everything.Id}:x"));
    Api.RespondWith(new PersonalAccessTokenList { Items = [everything] });
    page.Find("form.sd-token-form").Submit();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-secret]")));
    Button(page, "Done").Click();

    Assert.Contains($"\"mode\":{(int)PersonalAccessTokenMode.InheritOwner}", Api.Bodies[2]);
    Assert.Contains("\"expiresInDays\":null", Api.Bodies[2]);
    Assert.Equal(string.Empty, Location);
    var row = page.Find("tr[data-token='Everything']");
    Assert.Contains("Inherit owner", row.TextContent);
    Assert.Contains("Yours", row.TextContent);
    Assert.Contains("Never", row.QuerySelector("[data-cell=expires]")!.TextContent);
    Assert.DoesNotContain("Edit permissions", row.TextContent);
  }

  [Fact]
  public void TheList_TellsExpiredFromCurrent_AndWhenEachWasLastUsed()
  {
    Api.RespondWith(Me(PermissionNames.TenantPermissionsRead));
    Api.RespondWith(new PersonalAccessTokenList
    {
      Items =
      [
        Token("Old", expires: Now.AddMinutes(-1), permissions: 2),
        Token("Current", expires: Now.AddDays(30), lastUsed: Now.AddHours(-2), permissions: 3),
      ],
    });

    var page = RenderTokens();

    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));
    var old = page.Find("tr[data-token='Old']");
    var current = page.Find("tr[data-token='Current']");
    Assert.Contains("Expired", old.QuerySelector("[data-cell=expires]")!.TextContent);
    Assert.Contains("Never", old.TextContent);
    Assert.DoesNotContain("Expired", current.TextContent);
    Assert.Contains(Now.AddDays(30).ToLocalTime().ToString("yyyy-MM-dd"), current.QuerySelector("[data-cell=expires]")!.TextContent);
    Assert.Contains("3", current.TextContent);
    Assert.Contains("Edit permissions", current.TextContent);
    // Reading only: nothing to create, rename or delete with.
    Assert.Empty(page.FindAll("form.sd-token-form"));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Trim() is "Rename" or "Delete");
  }

  [Fact]
  public void ARefusedToken_SaysWhyUnderTheName()
  {
    Api.RespondWith(Me(PermissionNames.PersonalAccessTokenSelfWrite));
    Api.RespondWith(new PersonalAccessTokenList());
    var page = RenderTokens();
    page.WaitForAssertion(() => Assert.Contains("No access tokens", page.Markup));

    page.Find("form.sd-token-form").Submit();
    Assert.Equal("Enter a name for the token.", ErrorOf(page, "Token name").TextContent);
    Fill(page, "Token name", "Script");
    Api.Respond(HttpStatusCode.BadRequest, new { detail = "Inherit-owner tokens can only be created from a signed-in session." });
    page.Find("form.sd-token-form").Submit();

    page.WaitForAssertion(() => Assert.Equal("Inherit-owner tokens can only be created from a signed-in session.", ErrorOf(page, "Token name").TextContent));
    Assert.Empty(page.FindAll("[data-secret]"));
  }

  [Fact]
  public void Renaming_AndDeleting_AskFirst_ThenSendTheChange()
  {
    var script = Token("Script");
    Api.RespondWith(Me(PermissionNames.PersonalAccessTokenSelfWrite));
    Api.RespondWith(new PersonalAccessTokenList { Items = [script] });
    var page = RenderTokens();
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));

    page.FindAll("tr[data-token='Script'] button").Single(x => x.TextContent.Trim() == "Rename").Click();
    Fill(page, "New name", "Nightly backup");
    Api.RespondWith(script with { Name = "Nightly backup" });
    Api.RespondWith(new PersonalAccessTokenList { Items = [script with { Name = "Nightly backup" }] });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Rename").Click();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("tr[data-token='Nightly backup']")));

    page.FindAll("tr[data-token='Nightly backup'] button").Single(x => x.TextContent.Trim() == "Delete").Click();
    Assert.Contains("Anything still using Nightly backup stops working", page.Find(".sd-dialog").TextContent);
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new PersonalAccessTokenList());
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Delete").Click();

    page.WaitForAssertion(() => Assert.Contains("No access tokens", page.Markup));
    Assert.Equal($"PUT {Routes.PersonalAccessToken(script.Id)}", Api.Requests[2]);
    Assert.Contains("\"name\":\"Nightly backup\"", Api.Bodies[2]);
    Assert.Equal($"DELETE {Routes.PersonalAccessToken(script.Id)}", Api.Requests[4]);
  }

  [Fact]
  public void ThePermissionsPage_PicksFromYourTokens_AndManagesTheirAssignments()
  {
    var script = Token("Script");
    var everything = Token("Everything", PersonalAccessTokenMode.InheritOwner);
    Navigation.NavigateTo($"permissions?kind=PersonalAccessToken&id={script.Id}");
    Api.RespondWith(Me(PermissionNames.TenantPermissionsRead));
    Api.RespondWith(new PersonalAccessTokenList { Items = [script, everything] });
    Api.RespondWith(new PermissionCatalogList { Items = [new(PermissionNames.TenantUsersRead, "Tenant", "Read users", "View the tenant's users.", [PermissionScopeKind.Tenant])] });
    Api.RespondWith(new PermissionPresetList());
    Api.RespondWith(new List<DeviceSummary>());
    Api.RespondWith(new UserGroupList());
    Api.RespondWith(new PermissionAssignmentList());

    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.Permissions>());

    page.WaitForAssertion(() => Assert.Contains($"principalKind=PersonalAccessToken&principalId={script.Id}", string.Join(" ", Api.Requests)));
    Assert.Equal($"GET {Routes.PersonalAccessTokens}", Api.Requests[1]);
    Assert.Equal("true", page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim() == "Access token").GetAttribute("aria-pressed"));
    var options = page.FindAll("select option").Select(x => x.TextContent.Trim()).ToList();
    Assert.Contains("Choose an access token", options);
    Assert.Contains("Script", options);
    Assert.Contains("Everything (inherits your permissions)", options);
    page.WaitForAssertion(() => Assert.Contains("No permissions", page.Markup));
    Assert.DoesNotContain("Check a permission", page.Markup);
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderTokens() =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.PersonalAccessTokens>());

  private static AngleSharp.Dom.IElement Button(IRenderedComponent<CascadingAuthenticationState> page, string text) =>
    page.FindAll("button").First(x => x.TextContent.Trim() == text);

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => now;
  }
}
