using System.Text.Json;
using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>An invite from start to finish: the administrator invites, the invited person opens the link and joins.</summary>
public class InviteTests
{
  private const string Password = "Choose-a-Passw0rd";
  private const string JoaoPassword = "Joao-s-own-Passw0rd";

  [Fact]
  public async Task InvitedPerson_OpensTheLink_ChoosesAPassword_AndJoinsTheTenant()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);

    await app.ClickLinkAsync("Invites");
    await Expect(app.Heading("Invites")).ToBeVisibleAsync();
    await app.FillAsync("Invite a new user", "joao@example.com");
    await app.ClickAsync("Invite");
    await Expect(app.Page.Locator(".sd-alert")).ToContainTextAsync("joao@example.com is invited");
    await Expect(app.Page.Locator("tr[data-invite='joao@example.com']")).ToBeVisibleAsync();
    await app.ScreenshotAsync("invited");

    // The link the copy button would copy, read the way the page gets it.
    var link = await InviteLinkAsync(app, "joao@example.com");
    await app.SignOutAsync();

    await app.Page.GotoAsync(link);
    await Expect(app.Heading("Accept the invitation")).ToBeVisibleAsync();
    await app.FillAsync("Email", "Joao@Example.com");
    await app.FillAsync("Password", JoaoPassword);
    await app.FillAsync("Confirm password", JoaoPassword);
    await app.ClickAsync("Join");
    await Expect(app.Heading("You've joined")).ToBeVisibleAsync();
    await app.ScreenshotAsync("joined");

    // The same link can't be used twice.
    await app.Page.GotoAsync(link);
    await app.FillAsync("Email", "joao@example.com");
    await app.FillAsync("Password", JoaoPassword);
    await app.FillAsync("Confirm password", JoaoPassword);
    await app.ClickAsync("Join");
    await Expect(app.Alert).ToContainTextAsync("doesn't exist, was already used");

    await app.GoAsync("account/sign-in");
    await app.SignInAsync("joao@example.com", JoaoPassword);
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
    await Expect(app.Page.Locator(".sd-user-name")).ToHaveTextAsync("joao@example.com");
    // The baseline only: nothing to administer yet.
    await Expect(app.Page.GetByRole(AriaRole.Link, new() { Name = "Users" })).ToHaveCountAsync(0);
    await Expect(app.Page.GetByRole(AriaRole.Link, new() { Name = "Invites" })).ToHaveCountAsync(0);
    await app.SignOutAsync();

    await app.SignInAsync("admin@example.com", Password);
    await app.ClickLinkAsync("Users");
    await Expect(app.Page.Locator("tr[data-user='joao@example.com']")).ToBeVisibleAsync();
    await app.ClickLinkAsync("Invites");
    await Expect(app.Page.GetByText("No pending invites")).ToBeVisibleAsync();
  }

  private static async Task<string> InviteLinkAsync(UiApp app, string email)
  {
    var response = await app.Context.APIRequest.GetAsync("api/v1/invites");
    Assert.Equal(200, response.Status);
    using var json = JsonDocument.Parse(await response.TextAsync());
    return json.RootElement.GetProperty("items").EnumerateArray()
      .Single(x => x.GetProperty("inviteeEmail").GetString() == email)
      .GetProperty("inviteUrl").GetString()!;
  }
}
