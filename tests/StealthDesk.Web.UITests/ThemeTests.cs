using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>
/// The theme: chosen in the menu, kept with the account so another browser opens with it, remembered by the browser
/// for the sign-in page, and the main pages captured in both themes.
/// </summary>
public class ThemeTests
{
  private const string Password = "Choose-a-Passw0rd";

  private static readonly string[] Pages = ["", "users", "permissions", "service-accounts", "account/manage"];

  [Fact]
  public async Task TheChosenTheme_FollowsTheAccount_ToAnotherBrowser()
  {
    await using var app = await UiApp.StartAsync();
    await app.RegisterAsync("admin@example.com", Password);
    var html = app.Page.Locator("html");

    await ChooseAsync(app, "Dark");
    await Expect(html).ToHaveAttributeAsync("data-theme", "dark");
    Assert.Equal(DarkGround, await GroundAsync(app.Page));
    await Capture(app, "dark");

    // On a light device, a new page starts dark before the app even runs: only index.html's script sets it.
    var bare = await app.Context.NewPageAsync();
    await bare.RouteAsync("**/_framework/**", route => route.AbortAsync());
    await bare.GotoAsync("");
    Assert.Equal("dark", await bare.Locator("html").GetAttributeAsync("data-theme"));
    await bare.CloseAsync();

    await ChooseAsync(app, "Light");
    await Expect(html).ToHaveAttributeAsync("data-theme", "light");
    await Expect(Theme(app, "Light")).ToHaveAttributeAsync("aria-pressed", "true");
    Assert.Equal(LightGround, await GroundAsync(app.Page));
    await Capture(app, "light");

    // Reloading starts light straight away: index.html applies the remembered theme before the app runs.
    await app.Page.ReloadAsync();
    await Expect(html).ToHaveAttributeAsync("data-theme", "light");

    // Signed out, this browser keeps the theme it last used.
    await app.SignOutAsync();
    await Expect(html).ToHaveAttributeAsync("data-theme", "light");
    await app.ScreenshotAsync("sign-in-light");

    // A browser that never chose anything opens dark (its device is dark), then takes the account's theme on sign-in.
    await using var other = await app.Context.Browser!.NewContextAsync(new BrowserNewContextOptions
    {
      BaseURL = app.Server.BaseAddress.ToString(),
      ColorScheme = ColorScheme.Dark,
      Locale = "en-US",
    });
    var page = await other.NewPageAsync();
    await page.GotoAsync("account/sign-in");
    await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
    await page.GetByLabel("Email").FillAsync("admin@example.com");
    await page.GetByLabel("Password", new() { Exact = true }).FillAsync(Password);
    await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
    await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Devices" })).ToBeVisibleAsync();
    await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");

    // Auto follows the device.
    await page.GetByRole(AriaRole.Group, new() { Name = "Theme" }).GetByRole(AriaRole.Button, new() { Name = "Auto" }).ClickAsync();
    await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
  }

  // The page backgrounds of tokens.css: the colors themselves, not only the attribute that picks them.
  private const string LightGround = "rgb(244, 241, 235)";
  private const string DarkGround = "rgb(14, 14, 15)";

  private static Task<string> GroundAsync(IPage page) =>
    page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");

  // The page shows a theme at once and saves it with the account after: the test waits for the save, as a person
  // does before reloading or signing in elsewhere, or a reload could find the account's previous theme.
  private static Task ChooseAsync(UiApp app, string mode) =>
    app.Page.RunAndWaitForResponseAsync(
      () => Theme(app, mode).ClickAsync(),
      response => response.Url.EndsWith(StealthDesk.Contracts.Routes.AccountTheme, StringComparison.Ordinal) && response.Request.Method == "PUT");

  private static ILocator Theme(UiApp app, string mode) =>
    app.Page.GetByRole(AriaRole.Group, new() { Name = "Theme" }).First.GetByRole(AriaRole.Button, new() { Name = mode });

  private static async Task Capture(UiApp app, string theme)
  {
    foreach (var page in Pages)
    {
      await app.GoAsync(page);
      await Expect(app.Page.Locator("h1").First).ToBeVisibleAsync();
      await app.ScreenshotAsync($"{theme}-{(page.Length == 0 ? "devices" : page.Replace('/', '-'))}");
    }
  }
}
