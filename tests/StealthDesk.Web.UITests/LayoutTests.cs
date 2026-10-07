using StealthDesk.Web.UITests.Infrastructure;

namespace StealthDesk.Web.UITests;

/// <summary>The pages on a phone-sized screen: nothing wider than the screen.</summary>
public class LayoutTests
{
  private const string Password = "Choose-a-Passw0rd";

  [Fact]
  public async Task OnAPhone_NoPageScrollsSideways()
  {
    await using var app = await UiApp.StartAsync();
    await app.Page.SetViewportSizeAsync(390, 844);

    await app.GoAsync("account/sign-in");
    await Expect(app.Heading("Sign in to StealthDesk")).ToBeVisibleAsync();
    await AssertFitsAsync(app, "sign-in");

    await app.RegisterAsync("admin@example.com", Password);
    await AssertFitsAsync(app, "devices");

    foreach (var page in new[] { "account/manage", "account/manage/email", "account/manage/password", "account/manage/passkeys", "account/manage/two-factor/enable", "account/manage/personal-data" })
    {
      await app.GoAsync(page);
      await Expect(app.Heading("Account settings")).ToBeVisibleAsync();
      await app.Page.WaitForLoadStateAsync(LoadState.NetworkIdle);
      await AssertFitsAsync(app, page.Replace('/', '-'));
    }
  }

  private static async Task AssertFitsAsync(UiApp app, string name)
  {
    var widths = await app.Page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, window.innerWidth]");
    if (widths[0] > widths[1])
    {
      await app.ScreenshotAsync($"too-wide-{name}");
    }

    Assert.True(widths[0] <= widths[1], $"{name} is {widths[0]}px wide on a {widths[1]}px screen.");
  }
}
