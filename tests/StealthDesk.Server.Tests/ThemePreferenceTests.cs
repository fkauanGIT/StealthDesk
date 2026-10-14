using System.Net;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Server.Tests.Infrastructure;

namespace StealthDesk.Server.Tests;

/// <summary>The theme kept with each account, and the one the sign-in pages use before anyone signs in.</summary>
public class ThemePreferenceTests
{
  private const string BeforeThemes = "20261013180640_AddServiceAccounts";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task ANewAccount_StartsOnAuto_AndItsChoiceFollowsItToAnotherSession()
  {
    using var server = await ServerHost.OnPostgresAsync();
    await TestAccounts.CreateUserAsync(server, "admin@example.com");
    using var first = await ClientForAsync(server, "admin@example.com");

    var before = await MeAsync(first);
    var chosen = await first.PutAsJsonAsync(Routes.AccountTheme, new ThemeUpdate(ThemeMode.Light), Cancel);
    using var second = await ClientForAsync(server, "admin@example.com");

    Assert.Equal(ThemeMode.Auto, before.Theme);
    Assert.Equal(HttpStatusCode.NoContent, chosen.StatusCode);
    Assert.Equal(ThemeMode.Light, (await MeAsync(second)).Theme);
  }

  [Fact]
  public async Task OnlyAKnownTheme_IsKept_AndOnlyByItsOwnSignedInUser()
  {
    using var server = ServerHost.InMemory();
    await TestAccounts.CreateUserAsync(server, "admin@example.com");
    using var client = await ClientForAsync(server, "admin@example.com");
    using var anonymous = TestAccounts.Client(server);

    var unknown = await client.PutAsJsonAsync(Routes.AccountTheme, new { theme = 7 }, Cancel);
    var signedOut = await anonymous.PutAsJsonAsync(Routes.AccountTheme, new ThemeUpdate(ThemeMode.Dark), Cancel);

    Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    Assert.Equal(ThemeMode.Auto, (await MeAsync(client)).Theme);
  }

  [Theory]
  [InlineData(null, ThemeMode.Auto)]
  [InlineData("Light", ThemeMode.Light)]
  [InlineData("Dark", ThemeMode.Dark)]
  public async Task TheSignInPages_UseTheServersDefaultTheme(string? configured, ThemeMode expected)
  {
    using var server = configured is null ? ServerHost.InMemory() : ServerHost.InMemory().With("Accounts:DefaultTheme", configured);
    using var anonymous = TestAccounts.Client(server);

    var settings = await anonymous.GetFromJsonAsync<AccountSettings>(Routes.AuthSettings, Cancel);

    Assert.Equal(expected, settings!.DefaultTheme);
  }

  // Accounts from before themes existed follow their device until they choose.
  [Fact]
  public async Task ExistingAccounts_StartOnAuto_WhenThemesArrive()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var user = await TestAccounts.CreateUserAsync(server, "admin@example.com");

    await MigrateAsync(server, BeforeThemes);
    await MigrateAsync(server, null);
    var stored = await server.WithDbAsync(db => db.Database
      .SqlQuery<string>($"SELECT \"Theme\" AS \"Value\" FROM users WHERE \"Id\" = {user.Id}")
      .SingleAsync(Cancel));

    Assert.Equal(nameof(ThemeMode.Auto), stored);
  }

  private static async Task MigrateAsync(ServerHost server, string? target)
  {
    await using var scope = server.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<StealthDeskDb>().GetService<IMigrator>().MigrateAsync(target, Cancel);
  }

  private static async Task<CurrentUser> MeAsync(HttpClient client) =>
    (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!;

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email)
  {
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", await TestAccounts.SignInForCookieAsync(server, email));
    return client;
  }
}
