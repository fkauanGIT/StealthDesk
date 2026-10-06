using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Layout;

namespace StealthDesk.Web.Client.Tests;

/// <summary>Knowing who is signed in, and sending everyone else to sign in.</summary>
public class SignInStateTests : AccountTestContext
{
  private readonly FakeLiveUpdates _live = new();

  public SignInStateTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<DeviceStore>();
    Services.AddSingleton<ILiveUpdates>(_live);
  }

  private static CurrentUser Ana => new()
  {
    Id = Guid.NewGuid(),
    Email = "ana@example.com",
    TenantId = Guid.NewGuid(),
    IsServerAdministrator = true,
    IsTenantAdministrator = true,
  };

  [Fact]
  public async Task SignedIn_ComesFromTheServerWithTheTenantAndRoles()
  {
    var ana = Ana;
    Api.RespondWith(ana);

    var state = await Services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();

    Assert.True(state.User.Identity!.IsAuthenticated);
    Assert.Equal("ana@example.com", state.User.Identity.Name);
    Assert.True(state.User.HasClaim(ServerAuthenticationState.TenantIdClaim, ana.TenantId.ToString()));
    Assert.True(state.User.HasClaim(ServerAuthenticationState.ServerAdministratorClaim, "true"));
  }

  [Fact]
  public async Task NoSession_IsSignedOut()
  {
    Api.Respond(HttpStatusCode.Unauthorized);

    var state = await Services.GetRequiredService<AuthenticationStateProvider>().GetAuthenticationStateAsync();

    Assert.False(state.User.Identity!.IsAuthenticated);
  }

  [Fact]
  public async Task MarkSignedOut_DoesNotAskTheServer()
  {
    var state = Services.GetRequiredService<ServerAuthenticationState>();

    state.MarkSignedOut();
    var current = await state.GetAuthenticationStateAsync();

    Assert.False(current.User.Identity!.IsAuthenticated);
    Assert.Empty(Api.Requests);
  }

  [Theory]
  [InlineData("devices/42", "account/sign-in?returnUrl=%2Fdevices%2F42")]
  [InlineData("", "account/sign-in")]
  [InlineData("account/register", "account/sign-in")]
  [InlineData("account/manage/email", "account/sign-in?returnUrl=%2Faccount%2Fmanage%2Femail")]
  public void RedirectToSignIn_RemembersWhereTheUserWasGoing(string from, string expected)
  {
    Navigation.NavigateTo(from);

    Render<RedirectToSignIn>();

    Assert.Equal(expected, Location);
  }

  [Theory]
  [InlineData("/devices/42", "devices/42")]
  [InlineData("//evil.example", "")]
  [InlineData("/\\evil.example", "")]
  [InlineData("https://evil.example", "")]
  [InlineData(null, "")]
  public void LocalUrl_KeepsOnlyAddressesOnThisSite(string? returnUrl, string expected) =>
    Assert.Equal(expected, LocalUrl.Or(returnUrl));

  [Fact]
  public async Task ExpiredSessionOnTheList_RaisesSessionExpired()
  {
    var store = Services.GetRequiredService<DeviceStore>();
    var expired = false;
    store.SessionExpired += () => expired = true;
    Api.Respond(HttpStatusCode.Unauthorized);

    await store.LoadAsync();

    Assert.True(expired);
    Assert.Null(store.Devices);
    Assert.Null(store.Error);
  }

  [Fact]
  public async Task ExpiredSessionOnADevice_RaisesSessionExpired()
  {
    var store = Services.GetRequiredService<DeviceStore>();
    var expired = false;
    store.SessionExpired += () => expired = true;
    Api.Respond(HttpStatusCode.Unauthorized);

    var lookup = await store.FindAsync(Guid.NewGuid());

    Assert.True(expired);
    Assert.True(lookup.IsFailed);
  }

  [Fact]
  public void Layout_ShowsWhoIsSignedIn()
  {
    Api.RespondWith(Ana);

    var layout = Render<CascadingAuthenticationState>(x => x.AddChildContent<MainLayout>());

    layout.WaitForAssertion(() => Assert.Equal("ana@example.com", layout.Find(".sd-user-name").TextContent));
    Assert.Equal("Server administrator", layout.Find(".sd-user-role").TextContent);
  }

  [Fact]
  public void Layout_LinksTheUserToTheirAccountSettings()
  {
    Api.RespondWith(Ana);

    var layout = Render<CascadingAuthenticationState>(x => x.AddChildContent<MainLayout>());

    layout.WaitForAssertion(() => Assert.Equal("account/manage", layout.Find(".sd-user-link").GetAttribute("href")));
  }

  [Fact]
  public void MustChangePassword_SendsTheUserToChangeItFirst()
  {
    Api.RespondWith(Ana with { MustChangePassword = true });
    Navigation.NavigateTo("devices/42");

    var layout = Render<CascadingAuthenticationState>(x => x.AddChildContent<MainLayout>());

    layout.WaitForAssertion(() => Assert.Equal("account/password-change-required", Location));
  }

  [Fact]
  public void WithoutAForcedChange_TheUserStaysWhereTheyAre()
  {
    Api.RespondWith(Ana);
    Navigation.NavigateTo("devices/42");

    var layout = Render<CascadingAuthenticationState>(x => x.AddChildContent<MainLayout>());

    layout.WaitForAssertion(() => layout.Find(".sd-user"));
    Assert.Equal("devices/42", Location);
  }

  [Fact]
  public void SignOut_EndsTheSessionAndTheLiveConnection()
  {
    Api.RespondWith(Ana);
    Api.Respond(HttpStatusCode.NoContent);
    Navigation.NavigateTo("devices/42");
    var layout = Render<CascadingAuthenticationState>(x => x.AddChildContent<MainLayout>());
    layout.WaitForAssertion(() => layout.Find(".sd-user button"));

    layout.Find(".sd-user button").Click();

    layout.WaitForAssertion(() => Assert.Equal("account/sign-in", Location));
    Assert.Contains("POST /api/auth/sign-out", Api.Requests);
    Assert.True(_live.Stopped);
    layout.WaitForAssertion(() => Assert.Empty(layout.FindAll(".sd-user")));
  }

  [Fact]
  public void ExpiredSession_SendsTheUserToSignInAndBack()
  {
    Api.RespondWith(Ana);
    Navigation.NavigateTo("devices/42");
    var layout = Render<CascadingAuthenticationState>(x => x.AddChildContent<MainLayout>());
    layout.WaitForAssertion(() => layout.Find(".sd-user"));

    Services.GetRequiredService<DeviceStore>().ReportSessionExpired();

    layout.WaitForAssertion(() => Assert.Equal("account/sign-in?returnUrl=%2Fdevices%2F42", Location));
    Assert.True(_live.Stopped);
  }
}
