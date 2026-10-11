using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>User groups in the browser: creating one, managing its members, renaming and deleting it, all logged.</summary>
public class UserGroupTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task Administrator_ManagesAGroupAndItsMembers()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    var created = await app.Context.APIRequest.PostAsync("api/v1/users", new APIRequestContextOptions
    {
      DataObject = new { userName = "tech@example.com", password = Password },
    });
    Assert.Equal(201, created.Status);

    await app.ClickLinkAsync("User groups");
    await Expect(app.Page.GetByText("No groups yet")).ToBeVisibleAsync();
    await app.ClickAsync("New group");
    await app.FillAsync("Name", "Support");
    await app.FillAsync("Description", "First line");
    await app.ClickAsync("Create");

    // Creating opens the group.
    await Expect(app.Heading("Support")).ToBeVisibleAsync();
    await Expect(app.Page.Locator("[data-description]")).ToHaveTextAsync("First line");
    await app.ClickAsync("Add members");
    await app.Page.GetByLabel("tech@example.com").CheckAsync();
    await app.Page.Locator(".sd-dialog").GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
    await Expect(app.Page.Locator("tr[data-member='tech@example.com']")).ToBeVisibleAsync();
    await app.ScreenshotAsync("members");

    await app.ClickAsync("Edit");
    await app.FillAsync("Name", "Help desk");
    await app.ClickAsync("Save");
    await Expect(app.Heading("Help desk")).ToBeVisibleAsync();

    await app.Page.Locator("tr[data-member='tech@example.com']").GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
    await app.Page.Locator(".sd-dialog").GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
    await Expect(app.Page.GetByText("No members yet")).ToBeVisibleAsync();

    await app.Page.Locator(".sd-back").ClickAsync();
    await Expect(app.Page.Locator("tr[data-group='Help desk']")).ToBeVisibleAsync();
    await app.Page.Locator("tr[data-group='Help desk']").GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
    await app.Page.Locator(".sd-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete" }).ClickAsync();
    await Expect(app.Page.GetByText("No groups yet")).ToBeVisibleAsync();

    await app.ClickLinkAsync("Authorization log");
    foreach (var action in new[] { "user-group-created", "user-group-members-added", "user-group-updated", "user-group-members-removed", "user-group-deleted" })
    {
      await Expect(app.Page.Locator(".sd-log-table tbody tr").Filter(new() { HasText = action })).ToHaveCountAsync(1);
    }

    await app.ScreenshotAsync("log");
  }
}
