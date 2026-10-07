using System.Text.Json;
using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>The account settings: profile, password, personal data and deleting the account.</summary>
public class AccountSettingsTests
{
  private const string Password = "Choose-a-Passw0rd";
  private const string NewPassword = "Second-Passw0rd";

  [Fact]
  public async Task Profile_SavesThePhoneNumber_AndPasswordChangeKeepsTheSession()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);

    // The name in the sidebar opens the settings.
    await app.Page.Locator(".sd-user-link").ClickAsync();
    await Expect(app.Page.GetByLabel("Email", new() { Exact = true })).ToHaveValueAsync("admin@example.com");

    await app.FillAsync("Phone number", "call me");
    await app.ClickAsync("Save");
    await Expect(app.ErrorOf("Phone number")).ToContainTextAsync("valid phone number");
    await app.FillAsync("Phone number", "+55 11 98765-4321");
    await app.ClickAsync("Save");
    await Expect(app.Alert).ToContainTextAsync("Your profile has been updated.");
    await app.Page.ReloadAsync();
    await Expect(app.Page.GetByLabel("Phone number", new() { Exact = true })).ToHaveValueAsync("+55 11 98765-4321");

    await app.ClickLinkAsync("Password");
    await app.FillAsync("Current password", "Wrong-Passw0rd");
    await app.FillAsync("New password", NewPassword);
    await app.FillAsync("Confirm new password", NewPassword);
    await app.ClickAsync("Update password");
    await Expect(app.ErrorOf("Current password")).ToHaveTextAsync("Incorrect password.");
    await app.FillAsync("Current password", Password);
    await app.ClickAsync("Update password");
    await Expect(app.Alert).ToContainTextAsync("Your password has been changed.");

    // This session keeps working, and the new password is the one that signs in.
    await app.GoAsync("");
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
    await app.SignOutAsync();
    await app.SignInAsync("admin@example.com", NewPassword);
    await Expect(app.Heading("Devices")).ToBeVisibleAsync();
  }

  [Fact]
  public async Task PersonalData_Downloads_AndDeletingTheAccountNeedsThePassword()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    await app.GoAsync("account/manage/personal-data");

    var download = await app.Page.RunAndWaitForDownloadAsync(() =>
      app.Page.GetByRole(AriaRole.Link, new() { Name = "Download my data" }).ClickAsync());
    var file = Path.Combine(UiPaths.Results, "PersonalData.json");
    await download.SaveAsAsync(file);
    var data = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).RootElement;
    Assert.Equal("PersonalData.json", download.SuggestedFilename);
    Assert.Equal("admin@example.com", data.GetProperty("Email").GetString());

    await app.ClickLinkAsync("Delete my account");
    await app.FillAsync("Password", "Wrong-Passw0rd");
    await app.ClickAsync("Delete data and close my account");
    await Expect(app.ErrorOf("Password")).ToHaveTextAsync("Incorrect password.");
    await app.FillAsync("Password", Password);
    await app.ClickAsync("Delete data and close my account");

    await Expect(app.Alert).ToContainTextAsync("Your account has been deleted.");
    await app.SignInAsync("admin@example.com", Password);
    await Expect(app.Alert).ToContainTextAsync("Email or password is wrong.");
  }
}
