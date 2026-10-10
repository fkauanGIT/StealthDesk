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

    await app.GoAsync("invites");
    await Expect(app.Heading("Invites")).ToBeVisibleAsync();
    await AssertFitsAsync(app, "invites");

    await app.GoAsync("users");
    await Expect(app.Page.Locator("tbody tr")).ToHaveCountAsync(1);
    await AssertFitsAsync(app, "users");

    foreach (var (page, heading) in new[] { ("authorization-logs", "Authorization log"), ("server/authorization-logs", "Server authorization log") })
    {
      await app.GoAsync(page);
      await Expect(app.Page.Locator(".sd-log-table")).ToBeVisibleAsync();
      await Expect(app.Heading(heading)).ToBeVisibleAsync();
      await AssertFitsAsync(app, page.Replace('/', '-'));
    }
  }

  private static async Task AssertFitsAsync(UiApp app, string name)
  {
    var widths = await app.Page.EvaluateAsync<int[]>("[document.documentElement.scrollWidth, window.innerWidth]");
    var culprits = "";
    if (widths[0] > widths[1])
    {
      await app.ScreenshotAsync($"too-wide-{name}");

      // The outermost elements that reach past the screen, to say what to fix.
      culprits = await app.Page.EvaluateAsync<string>("""
        () => [...document.querySelectorAll('body *')]
          .filter(e => e.getBoundingClientRect().right > window.innerWidth + 0.5)
          .filter(e => !e.parentElement || e.parentElement.getBoundingClientRect().right <= window.innerWidth + 0.5)
          .slice(0, 5)
          .map(e => `${e.tagName.toLowerCase()}${e.className ? '.' + [...e.classList].join('.') : ''} (${Math.round(e.getBoundingClientRect().right)}px)`)
          .join(', ')
        """);
    }

    Assert.True(widths[0] <= widths[1], $"{name} is {widths[0]}px wide on a {widths[1]}px screen: {culprits}");
  }
}
