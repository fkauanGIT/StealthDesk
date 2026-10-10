using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.UserGroups;
using StealthDesk.Contracts.Users;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.UserGroups;
using StealthDesk.Web.Client.Users;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The user group pages: the list, creating and deleting, and a group's members.</summary>
public class UserGroupPagesTests : AccountTestContext
{
  private static readonly Guid Tenant = Guid.NewGuid();
  private static readonly DateTimeOffset Created = new(2026, 10, 10, 9, 0, 0, TimeSpan.Zero);

  public UserGroupPagesTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<UserGroupApi>();
    Services.AddSingleton<UserApi>();
    JSInterop.Mode = JSRuntimeMode.Loose;
  }

  private static CurrentUser Me(params string[] permissions) => new()
  {
    Id = Guid.NewGuid(),
    Email = "admin@example.com",
    TenantId = Tenant,
    Permissions = [PermissionNames.TenantUserGroupsRead, .. permissions],
  };

  private static UserGroupDetail Support(params UserGroupMember[] members) =>
    new(Guid.NewGuid(), "Support", "First line", Created, members);

  private static UserGroupMember Member(string name) => new(Guid.NewGuid(), name, null);

  [Fact]
  public void List_ShowsTheGroups_AndCreatingOneOpensIt()
  {
    var support = Support();
    Api.RespondWith(Me(PermissionNames.TenantUserGroupsWrite));
    Api.RespondWith(new UserGroupList { Items = [new UserGroupSummary(Guid.NewGuid(), "Finance", null, Created, 3)] });
    var page = RenderList();
    page.WaitForAssertion(() => Assert.Equal("3", page.Find("tr[data-group='Finance'] [data-cell=members]").TextContent));

    Button(page, "New group").Click();
    Fill(page, "Name", "Support");
    Fill(page, "Description", "First line");
    Api.Respond(HttpStatusCode.Created, support);
    Button(page, "Create").Click();

    page.WaitForAssertion(() => Assert.Equal($"user-groups/{support.Id}", Location));
    Assert.Equal($"POST {Routes.UserGroups}?tenantId={Tenant}", Api.Requests[2]);
    Assert.Contains("\"name\":\"Support\"", Api.Bodies[2]);
    Assert.Contains("\"description\":\"First line\"", Api.Bodies[2]);
  }

  [Fact]
  public void ATakenName_IsExplainedInTheDialog()
  {
    Api.RespondWith(Me(PermissionNames.TenantUserGroupsWrite));
    Api.RespondWith(new UserGroupList());
    var page = RenderList();
    page.WaitForAssertion(() => Assert.Contains("No groups yet", page.Markup));

    Button(page, "New group").Click();
    Fill(page, "Name", "Support");
    Api.Respond(HttpStatusCode.Conflict, new { detail = "A group with that name already exists." });
    Button(page, "Create").Click();

    page.WaitForAssertion(() => Assert.Contains("A group with that name already exists.", page.Find(".sd-dialog").TextContent));
  }

  [Fact]
  public void WithoutTheWritePermission_NoGroupCanBeCreatedOrDeleted()
  {
    Api.RespondWith(Me());
    Api.RespondWith(new UserGroupList { Items = [new UserGroupSummary(Guid.NewGuid(), "Finance", null, Created, 0)] });

    var page = RenderList();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Trim() is "New group" or "Delete");
  }

  [Fact]
  public void Members_AreListed_AddedFromTheTenantsUsers_AndRemoved()
  {
    var ana = Member("ana@example.com");
    var group = Support(ana);
    var bia = new TenantUser(Guid.NewGuid(), "bia@example.com", "bia@example.com", Created, []);
    Api.RespondWith(Me(PermissionNames.UserGroupAssignUsers, PermissionNames.TenantUsersRead));
    Api.RespondWith(group);
    var page = RenderDetails(group.Id);
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tr[data-member='ana@example.com']")));

    Api.RespondWith(new TenantUserList { Items = [new TenantUser(ana.UserId, ana.UserName, ana.UserName, Created, []), bia] });
    Button(page, "Add members").Click();
    page.WaitForAssertion(() => Assert.Single(page.FindAll(".sd-member-picker input")));
    page.Find(".sd-member-picker input").Change(true);
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(Support(ana, new UserGroupMember(bia.Id, bia.UserName, null)) with { Id = group.Id });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Add").Click();

    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));
    Assert.Equal($"POST {Routes.UserGroupMembers(group.Id)}?tenantId={Tenant}", Api.Requests[3]);
    Assert.Contains(bia.Id.ToString(), Api.Bodies[3]);

    page.Find("tr[data-member='ana@example.com'] button").Click();
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(Support(new UserGroupMember(bia.Id, bia.UserName, null)) with { Id = group.Id });
    page.FindAll(".sd-dialog button").Single(x => x.TextContent.Trim() == "Remove").Click();

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.Equal($"DELETE {Routes.UserGroupMembers(group.Id)}?tenantId={Tenant}", Api.Requests[5]);
    Assert.Contains(ana.UserId.ToString(), Api.Bodies[5]);
  }

  [Fact]
  public void Edit_SavesTheNameAndDescription()
  {
    var group = Support();
    Api.RespondWith(Me(PermissionNames.TenantUserGroupsWrite));
    Api.RespondWith(group);
    var page = RenderDetails(group.Id);
    page.WaitForAssertion(() => Assert.Equal("Support", page.Find("h1").TextContent));

    Button(page, "Edit").Click();
    Assert.Equal("Support", Input(page, "Name").GetAttribute("value"));
    Fill(page, "Name", "Help desk");
    Api.RespondWith(group with { Name = "Help desk" });
    Button(page, "Save").Click();

    page.WaitForAssertion(() => Assert.Equal("Help desk", page.Find("h1").TextContent));
    Assert.Equal($"PUT {Routes.UserGroup(group.Id)}?tenantId={Tenant}", Api.Requests[2]);
  }

  [Fact]
  public void WithoutTheAssignPermission_MembersCantBeChanged()
  {
    var group = Support(Member("ana@example.com"));
    Api.RespondWith(Me());
    Api.RespondWith(group);

    var page = RenderDetails(group.Id);

    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Trim() is "Add members" or "Remove" or "Edit");
  }

  [Fact]
  public void AGroupThatIsGone_SaysSo()
  {
    Api.RespondWith(Me());
    Api.Respond(HttpStatusCode.NotFound);

    var page = RenderDetails(Guid.NewGuid());

    page.WaitForAssertion(() => Assert.Contains("Group not found", page.Markup));
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderList() =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.UserGroups>());

  private IRenderedComponent<CascadingAuthenticationState> RenderDetails(Guid id) =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.UserGroupDetails>(p => p.Add(c => c.Id, id)));

  private static AngleSharp.Dom.IElement Button(IRenderedComponent<CascadingAuthenticationState> page, string text) =>
    page.FindAll("button").First(x => x.TextContent.Trim() == text);
}
