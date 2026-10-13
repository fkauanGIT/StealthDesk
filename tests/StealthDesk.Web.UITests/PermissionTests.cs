using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>Permissions in the browser: granting to a group, what a member then sees, a deny taking it away, and no lockout.</summary>
public class PermissionTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task AGroupsGrant_ReachesItsMember_UntilADirectDenyTakesItAway()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    Assert.Equal(201, (await app.Context.APIRequest.PostAsync("api/v1/users", new APIRequestContextOptions
    {
      DataObject = new { userName = "tech@example.com", password = Password },
    })).Status);

    // A group with the technician in it.
    await app.ClickLinkAsync("User groups");
    await app.ClickAsync("New group");
    await app.FillAsync("Name", "Support");
    await app.ClickAsync("Create");
    await app.ClickAsync("Add members");
    await app.Page.GetByLabel("tech@example.com").CheckAsync();
    await app.Page.Locator(".sd-dialog").GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
    await Expect(app.Page.Locator("tr[data-member='tech@example.com']")).ToBeVisibleAsync();

    // The group may read the tenant's users.
    await app.Page.GetByRole(AriaRole.Link, new() { Name = "Permissions" }).Last.ClickAsync();
    await Expect(app.Heading("Permissions")).ToBeVisibleAsync();
    await app.ClickAsync("Add permission");
    await Dialog(app).GetByLabel("Permission", new() { Exact = true }).SelectOptionAsync("tenant.users.read");
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
    await Expect(app.Page.Locator("tr[data-assignment='tenant.users.read']")).ToBeVisibleAsync();
    await app.ScreenshotAsync("group-granted");

    // Where the technician's access comes from.
    await app.Page.GetByRole(AriaRole.Button, new() { Name = "User", Exact = true }).ClickAsync();
    await app.Page.GetByLabel("User", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "tech@example.com" });
    await Expect(app.Page.GetByText("No permissions")).ToBeHiddenAsync();
    await app.Page.Locator(".sd-card").Filter(new() { HasText = "Check a permission" }).GetByLabel("Permission").SelectOptionAsync("tenant.users.read");
    await app.ClickAsync("Check");
    await Expect(app.Page.Locator(".sd-alert").Filter(new() { HasText = "Allowed." })).ToContainTextAsync("user group assignment");
    await app.ScreenshotAsync("check");
    await app.SignOutAsync();

    await app.SignInAsync("tech@example.com", Password);
    await Expect(app.Page.GetByRole(AriaRole.Link, new() { Name = "Users" })).ToBeVisibleAsync();
    await app.SignOutAsync();

    // A direct deny wins over what the group gives.
    await app.SignInAsync("admin@example.com", Password);
    await app.ClickLinkAsync("Permissions");
    await app.Page.GetByLabel("User", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "tech@example.com" });
    await app.ClickAsync("Add permission");
    await Dialog(app).GetByLabel("Permission", new() { Exact = true }).SelectOptionAsync("tenant.users.read");
    await Dialog(app).GetByLabel("Effect").SelectOptionAsync("Deny");
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
    await Expect(app.Page.Locator("tr[data-assignment='tenant.users.read'][data-effect='Deny']")).ToBeVisibleAsync();
    await app.SignOutAsync();

    await app.SignInAsync("tech@example.com", Password);
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
    await Expect(app.Page.GetByRole(AriaRole.Link, new() { Name = "Users" })).ToHaveCountAsync(0);
    await app.SignOutAsync();

    // Nobody locks themselves out.
    await app.SignInAsync("admin@example.com", Password);
    await app.ClickLinkAsync("Permissions");
    await app.Page.GetByLabel("User", new() { Exact = true }).SelectOptionAsync(new SelectOptionValue { Label = "admin@example.com" });
    var own = app.Page.Locator("tr[data-assignment='tenant.permissions.write']");
    await own.Locator("input[type=checkbox]").First.CheckAsync();
    await app.ClickAsync("Delete 1");
    await Dialog(app).GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
    await Expect(app.Page.Locator(".sd-alert").First).ToContainTextAsync("You can't remove 'Manage permissions' from yourself");
    await Expect(own).ToBeVisibleAsync();
    await app.ScreenshotAsync("no-lockout");
  }

  private static ILocator Dialog(UiApp app) => app.Page.Locator(".sd-dialog");
}
