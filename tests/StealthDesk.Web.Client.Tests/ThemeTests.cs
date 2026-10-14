using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Layout;

namespace StealthDesk.Web.Client.Tests;

/// <summary>
/// The theme: the account's shown once signed in, chosen in the menu and kept with the account, put back when it
/// can't be kept, and before signing in the browser's own or the server's default.
/// </summary>
public class ThemeTests : AccountTestContext
{
  public ThemeTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<DeviceStore>();
    Services.AddSingleton<ILiveUpdates>(new FakeLiveUpdates());
  }

  private static CurrentUser Me(ThemeMode theme) => new()
  {
    Id = Guid.NewGuid(),
    Email = "admin@example.com",
    TenantId = Guid.NewGuid(),
    Theme = theme,
  };

  [Fact]
  public void SignedIn_TheAccountsThemeIsShown_AndPicked()
  {
    Theme.Stored = ThemeMode.Light;
    Api.RespondWith(Me(ThemeMode.Dark));

    var layout = RenderLayout();

    layout.WaitForAssertion(() => Assert.Equal([ThemeMode.Dark], Theme.Shown));
    Assert.Equal("true", Choice(layout, "Dark").GetAttribute("aria-pressed"));
    Assert.Equal("false", Choice(layout, "Light").GetAttribute("aria-pressed"));
  }

  [Fact]
  public void Choosing_ShowsItAtOnce_AndKeepsItWithTheAccount()
  {
    Api.RespondWith(Me(ThemeMode.Dark));
    var layout = RenderLayout();
    layout.WaitForAssertion(() => Assert.Equal([ThemeMode.Dark], Theme.Shown));

    Api.Respond(HttpStatusCode.NoContent);
    Choice(layout, "Light").Click();

    layout.WaitForAssertion(() => Assert.Equal("true", Choice(layout, "Light").GetAttribute("aria-pressed")));
    Assert.Equal([ThemeMode.Dark, ThemeMode.Light], Theme.Shown);
    Assert.Equal($"PUT {Routes.AccountTheme}", Api.Requests[1]);
    Assert.Contains($"\"theme\":{(int)ThemeMode.Light}", Api.Bodies[1]);
  }

  [Fact]
  public void AChoiceThatCantBeKept_IsPutBack_AndSaysSo()
  {
    Api.RespondWith(Me(ThemeMode.Dark));
    var layout = RenderLayout();
    layout.WaitForAssertion(() => Assert.Equal([ThemeMode.Dark], Theme.Shown));

    Api.Respond(HttpStatusCode.InternalServerError);
    Choice(layout, "Light").Click();

    layout.WaitForAssertion(() => Assert.NotNull(layout.Find(".sd-theme-picker [role=alert]")));
    Assert.Equal([ThemeMode.Dark, ThemeMode.Light, ThemeMode.Dark], Theme.Shown);
    Assert.Equal("true", Choice(layout, "Dark").GetAttribute("aria-pressed"));
  }

  [Theory]
  [InlineData(null, ThemeMode.Dark, ThemeMode.Dark)]
  [InlineData(ThemeMode.Light, ThemeMode.Dark, ThemeMode.Light)]
  [InlineData(ThemeMode.Auto, ThemeMode.Light, ThemeMode.Auto)]
  public async Task BeforeSigningIn_TheBrowsersOwnTheme_ElseTheServersDefault(ThemeMode? stored, ThemeMode serverDefault, ThemeMode expected)
  {
    Theme.Stored = stored;
    var state = Services.GetRequiredService<ThemeState>();

    await state.UseBrowserThemeAsync(serverDefault);

    Assert.Equal(expected, state.Mode);
    Assert.Equal([expected], Theme.Shown);
  }

  [Fact]
  public void TheAccountSettings_OfferTheThemeToo()
  {
    Api.RespondWith(Me(ThemeMode.Auto));
    Api.RespondWith(new AccountProfile { Id = Guid.NewGuid(), Email = "admin@example.com" });

    var page = Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.Account.Manage.Profile>());

    page.WaitForAssertion(() => Assert.Contains("Appearance", page.Markup));
    Assert.Equal(["Auto", "Light", "Dark"], page.FindAll(".sd-theme-picker .sd-segmented button").Select(x => x.TextContent.Trim()));
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderLayout() =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<MainLayout>());

  private static AngleSharp.Dom.IElement Choice(IRenderedComponent<CascadingAuthenticationState> page, string mode) =>
    page.FindAll(".sd-theme-picker .sd-segmented button").Single(x => x.TextContent.Trim() == mode);
}
