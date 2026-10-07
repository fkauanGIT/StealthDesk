using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>Microsoft and GitHub: buttons only for configured providers, leading to the provider with this server's app.</summary>
public class ExternalProviderTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task WithoutProviders_NoButtonsAndNoLinkedAccounts()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    await app.GoAsync("account/manage");

    await Expect(app.Page.Locator(".sd-manage-nav")).ToContainTextAsync("Personal data");
    await Expect(app.Page.Locator(".sd-manage-nav")).Not.ToContainTextAsync("Linked accounts");
    await app.SignOutAsync();
    await Expect(app.Page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Continue with") })).ToHaveCountAsync(0);
  }

  [Fact]
  public async Task ConfiguredProviders_LeadToTheirSignInWithThisServersApp()
  {
    await using var app = await UiApp.StartAsync(
      ("Accounts:GitHubClientId", "test-github"),
      ("Accounts:GitHubClientSecret", "secret"),
      ("Accounts:MicrosoftClientId", "test-microsoft"),
      ("Accounts:MicrosoftClientSecret", "secret"));

    // The server's redirect to the provider is read, not followed: the providers' sites stay out of the test.
    var reached = new List<string>();
    await app.Page.RouteAsync(url => url.Contains("/api/auth/external/", StringComparison.Ordinal)
      || url.Contains("/api/account/logins/link/", StringComparison.Ordinal), async route =>
    {
      var response = await route.FetchAsync(new RouteFetchOptions { MaxRedirects = 0 });
      reached.Add(response.Headers["location"]);
      await route.FulfillAsync(new RouteFulfillOptions { Status = 200, Body = "Left for the provider." });
    });

    await app.GoAsync("account/sign-in");
    await Expect(app.Page.GetByRole(AriaRole.Button, new() { NameRegex = new("^Continue with") })).ToHaveCountAsync(2);
    await app.ClickAsync("Continue with GitHub");
    await Expect(app.Page.GetByText("Left for the provider.")).ToBeVisibleAsync();

    var github = Uri.UnescapeDataString(reached.Single());
    Assert.StartsWith("https://github.com/login/oauth/authorize?", github, StringComparison.Ordinal);
    Assert.Contains("client_id=test-github", github, StringComparison.Ordinal);
    Assert.Contains($"redirect_uri={app.Server.BaseAddress}signin-github", github, StringComparison.Ordinal);

    // Linking from the settings goes to the provider too.
    await app.RegisterAsync("admin@example.com", Password);
    await app.GoAsync("account/manage");
    await app.ClickLinkAsync("Linked accounts");
    await app.ClickAsync("Link Microsoft");
    await Expect(app.Page.GetByText("Left for the provider.")).ToBeVisibleAsync();
    var microsoft = Uri.UnescapeDataString(reached.Last());
    Assert.StartsWith("https://login.microsoftonline.com/", microsoft, StringComparison.Ordinal);
    Assert.Contains("client_id=test-microsoft", microsoft, StringComparison.Ordinal);
  }
}
