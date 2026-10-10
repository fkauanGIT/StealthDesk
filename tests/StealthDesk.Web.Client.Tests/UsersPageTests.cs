using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.Users;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Users;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The users page: the tenant's users, searching them and deleting one.</summary>
public class UsersPageTests : AccountTestContext
{
  private static readonly Guid Tenant = Guid.NewGuid();
  private static readonly Guid MeId = Guid.NewGuid();

  public UsersPageTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<UserApi>();
    JSInterop.Mode = JSRuntimeMode.Loose;
  }

  private static CurrentUser Me(params string[] permissions) => new()
  {
    Id = MeId,
    Email = "admin@example.com",
    TenantId = Tenant,
    Permissions = [PermissionNames.TenantUsersRead, .. permissions],
  };

  private static TenantUser User(string email, Guid? id = null, int permissions = 2) =>
    new(id ?? Guid.NewGuid(), email, email, new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero), [.. Enumerable.Range(0, permissions).Select(x => $"p{x}")]);

  [Fact]
  public void Users_AreListedWithWhatTheyHold_AndCanBeSearched()
  {
    Api.RespondWith(Me());
    Api.RespondWith(new TenantUserList { Items = [User("admin@example.com", MeId, 35), User("tech@example.com")] });

    var page = RenderPage();

    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));
    Assert.Equal("35", page.Find("tr[data-user='admin@example.com'] [data-cell=permissions]").TextContent);
    Assert.Contains($"users?tenantId={Tenant}", Api.Requests[1]);

    page.Find("#user-search").Input("TECH");

    Assert.Equal("tech@example.com", page.Find("tbody tr").GetAttribute("data-user"));
  }

  [Fact]
  public void Delete_AsksFirst_ThenRemovesTheUserAndReloads()
  {
    var tech = User("tech@example.com");
    Api.RespondWith(Me(PermissionNames.TenantUsersDelete));
    Api.RespondWith(new TenantUserList { Items = [User("admin@example.com", MeId), tech] });
    var page = RenderPage();
    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));

    Assert.True(DeleteButton(page, "admin@example.com").HasAttribute("disabled"));
    DeleteButton(page, "tech@example.com").Click();
    Assert.Contains("tech@example.com will no longer be able to sign in", page.Find(".sd-dialog").TextContent);

    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new TenantUserList { Items = [User("admin@example.com", MeId)] });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Delete").Click();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.Equal($"DELETE {Routes.User(tech.Id)}?tenantId={Tenant}", Api.Requests[2]);
    Assert.Empty(page.FindAll(".sd-dialog"));
  }

  [Fact]
  public void WithoutTheDeletePermission_NoDeleteIsOffered()
  {
    Api.RespondWith(Me());
    Api.RespondWith(new TenantUserList { Items = [User("tech@example.com")] });

    var page = RenderPage();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Trim() == "Delete");
  }

  [Fact]
  public void ARefusedDelete_SaysWhy()
  {
    Api.RespondWith(Me(PermissionNames.TenantUsersDelete));
    Api.RespondWith(new TenantUserList { Items = [User("tech@example.com")] });
    var page = RenderPage();
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));

    DeleteButton(page, "tech@example.com").Click();
    Api.Respond(HttpStatusCode.Forbidden);
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Delete").Click();

    page.WaitForAssertion(() => Assert.Contains("Your account can't do that.", page.Markup));
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderPage() =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.Users>());

  private static AngleSharp.Dom.IElement DeleteButton(IRenderedComponent<CascadingAuthenticationState> page, string email) =>
    page.Find($"tr[data-user='{email}']").QuerySelectorAll("button").Single(x => x.TextContent.Trim() == "Delete");
}
