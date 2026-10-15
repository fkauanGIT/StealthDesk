using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Contracts.TenantSettings;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.TenantSettings;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The tenant settings page: the server's defaults shown, values saved together, and read-only for readers.</summary>
public class TenantSettingsPageTests : AccountTestContext
{
  public TenantSettingsPageTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<TenantSettingApi>();
  }

  private static CurrentUser Me(params string[] permissions) => new()
  {
    Id = Guid.NewGuid(),
    Email = "admin@example.com",
    TenantId = Guid.NewGuid(),
    Permissions = [PermissionNames.TenantSettingsRead, .. permissions],
  };

  private static TenantSettingList Settings(string? append = null, string? name = null, string? defaultAppend = "false", string? defaultName = null) => new()
  {
    Items =
    [
      new TenantSetting(TenantSettingNames.AppendInstanceName, append, defaultAppend),
      new TenantSetting(TenantSettingNames.InstanceName, name, defaultName),
    ],
  };

  [Fact]
  public void TheDefaults_AreShown_AndChoicesSavedTogether()
  {
    Api.RespondWith(Me(PermissionNames.TenantSettingsWrite));
    Api.RespondWith(Settings());
    var page = RenderPage();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("form")));

    Assert.Contains("Server default (off)", page.Find(".sd-segmented").TextContent);
    Assert.Equal("Made from the server's address: localhost", Input(page, "Instance name").GetAttribute("placeholder"));
    Assert.Equal("Install commands won't name an instance.", page.Find("[data-preview]").TextContent);

    page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim() == "On").Click();
    Fill(page, "Instance name", "office");
    Assert.Equal("Install commands will add --instance office.", page.Find("[data-preview]").TextContent);
    Api.RespondWith(Settings(append: "true", name: "office"));
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Settings saved.", page.Markup));
    Assert.Equal($"PUT {Routes.TenantSettings}", Api.Requests[2]);
    Assert.Contains($"\"{TenantSettingNames.AppendInstanceName}\":\"true\"", Api.Bodies[2]);
    Assert.Contains($"\"{TenantSettingNames.InstanceName}\":\"office\"", Api.Bodies[2]);
  }

  [Fact]
  public void BackToTheDefault_SendsEmptyValues()
  {
    Api.RespondWith(Me(PermissionNames.TenantSettingsWrite));
    Api.RespondWith(Settings(append: "true", name: "office", defaultAppend: "true", defaultName: "lab"));
    var page = RenderPage();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("form")));
    Assert.Equal("Server default: lab", Input(page, "Instance name").GetAttribute("placeholder"));

    page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim().StartsWith("Server default", StringComparison.Ordinal)).Click();
    Fill(page, "Instance name", string.Empty);
    Assert.Equal("Install commands will add --instance lab.", page.Find("[data-preview]").TextContent);
    Api.RespondWith(Settings(defaultAppend: "true", defaultName: "lab"));
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("Settings saved.", page.Markup));
    Assert.Contains($"\"{TenantSettingNames.AppendInstanceName}\":\"\"", Api.Bodies[2]);
    Assert.Contains($"\"{TenantSettingNames.InstanceName}\":\"\"", Api.Bodies[2]);
  }

  [Fact]
  public void AnInvalidName_IsCaughtBeforeSaving_AndTheServersRefusalShown()
  {
    Api.RespondWith(Me(PermissionNames.TenantSettingsWrite));
    Api.RespondWith(Settings());
    var page = RenderPage();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("form")));

    Fill(page, "Instance name", "default");
    page.Find("form").Submit();
    page.WaitForAssertion(() => Assert.Contains("is used when no instance is given", page.Markup));
    Assert.Equal(2, Api.Requests.Count);

    Fill(page, "Instance name", "office");
    Api.Respond(HttpStatusCode.Conflict);
    page.Find("form").Submit();

    page.WaitForAssertion(() => Assert.Contains("changed while saving", page.Markup));
  }

  [Fact]
  public void WithoutWrite_TheSettingsAreOnlyShown()
  {
    Api.RespondWith(Me());
    Api.RespondWith(Settings(append: "true", name: "office"));

    var page = RenderPage();

    page.WaitForAssertion(() => Assert.Equal("Install commands will add --instance office.", page.Find("[data-preview]").TextContent));
    Assert.Empty(page.FindAll("form"));
    Assert.Contains("Only someone who manages the tenant's settings", page.Markup);
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderPage() =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<Pages.TenantSettings>());
}
