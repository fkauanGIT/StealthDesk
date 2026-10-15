using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.InstallerKeys;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.InstallerKeys;

namespace StealthDesk.Web.Client.Tests;

/// <summary>
/// The Deploy page, making a key or using one kept and building the agent's command, and the installer keys page:
/// what it tells about each key, its installs, renaming and revoking.
/// </summary>
public class InstallerKeyPagesTests : AccountTestContext
{
  private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

  public InstallerKeyPagesTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<InstallerKeyApi>();
    Services.AddSingleton<TimeProvider>(new FixedClock(Now));
    JSInterop.Mode = JSRuntimeMode.Loose;
  }

  private static CurrentUser Me(params string[] permissions) => new()
  {
    Id = Guid.NewGuid(),
    Email = "tech@example.com",
    TenantId = Guid.NewGuid(),
    Permissions = [.. permissions],
  };

  private static InstallerKey Key(
    string? name,
    InstallerKeyType type = InstallerKeyType.Persistent,
    int? allowed = null,
    DateTimeOffset? expires = null,
    int uses = 0) =>
    new(Guid.NewGuid(), name, type, InstallerKeyCreatorKind.User, Guid.NewGuid(), "admin@example.com", Now.AddDays(-1), allowed, expires, uses);

  [Fact]
  public void Deploy_MakesAUsageBasedKey_AndBuildsTheCommandThatInstallsWithIt()
  {
    var key = Key("Front desk", InstallerKeyType.UsageBased, allowed: 3, expires: Now.AddHours(24));
    Api.RespondWith(Me(PermissionNames.AgentInstall, PermissionNames.InstallerKeyRead, PermissionNames.InstallerKeyWrite));
    Api.RespondWith(new InstallerKeyList());
    var page = RenderPage<Pages.Deploy>();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("form.sd-deploy-form")));

    Fill(page, "Name (optional)", "Front desk");
    Fill(page, "Uses", "3");
    Api.Respond(HttpStatusCode.Created, new CreatedInstallerKey(key, $"{key.Id}:shown-once"));
    page.Find("form.sd-deploy-form").Submit();

    page.WaitForAssertion(() => Assert.Equal(
      $@".\StealthDesk.Agent.exe run --server http://localhost --installer-key {key.Id}:shown-once",
      page.Find("[data-command]").TextContent));
    Assert.Equal($"POST {Routes.InstallerKeys}", Api.Requests[2]);
    Assert.Contains($"\"type\":{(int)InstallerKeyType.UsageBased}", Api.Bodies[2]);
    Assert.Contains("\"name\":\"Front desk\"", Api.Bodies[2]);
    Assert.Contains("\"allowedUses\":3", Api.Bodies[2]);
    Assert.Contains("\"expiresAt\":null", Api.Bodies[2]);
    Assert.Equal($"{key.Id}:shown-once", page.Find("[data-key]").TextContent);
    Assert.Contains("3 agents in all", page.Markup);

    var device = Guid.NewGuid();
    Fill(page, "Device ID (optional)", device.ToString());
    Button(page, "Copy command").Click();

    Assert.EndsWith($"--device-id {device}", page.Find("[data-command]").TextContent);
    Assert.Equal(page.Find("[data-command]").TextContent, JSInterop.Invocations["navigator.clipboard.writeText"].Single().Arguments[0]);
  }

  [Fact]
  public void Deploy_ATimeBasedKey_SendsWhenItExpires_AndAnUnusableNumberOfUsesIsCaught()
  {
    Api.RespondWith(Me(PermissionNames.AgentInstall, PermissionNames.InstallerKeyRead, PermissionNames.InstallerKeyWrite));
    Api.RespondWith(new InstallerKeyList());
    var page = RenderPage<Pages.Deploy>();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("form.sd-deploy-form")));

    Fill(page, "Uses", "0");
    page.Find("form.sd-deploy-form").Submit();
    page.WaitForAssertion(() => Assert.Contains("at least 1", page.Markup));
    Assert.Equal(2, Api.Requests.Count);

    page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim() == "Time-based").Click();
    Fill(page, "Expires", "2026-10-20T09:30");
    var key = Key(null, InstallerKeyType.TimeBased, expires: Now.AddDays(6));
    Api.Respond(HttpStatusCode.Created, new CreatedInstallerKey(key, $"{key.Id}:x"));
    page.Find("form.sd-deploy-form").Submit();

    page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-command]")));
    Assert.Contains($"\"type\":{(int)InstallerKeyType.TimeBased}", Api.Bodies[2]);
    Assert.Contains("\"expiresAt\":\"2026-10-20T09:30:00", Api.Bodies[2]);
    Assert.Contains("\"allowedUses\":null", Api.Bodies[2]);
  }

  [Fact]
  public void Deploy_ACommandWithAnInvalidDeviceId_CantBeCopied()
  {
    var key = Key("Lab");
    Api.RespondWith(Me(PermissionNames.AgentInstall, PermissionNames.InstallerKeyRead, PermissionNames.InstallerKeyWrite));
    Api.RespondWith(new InstallerKeyList());
    var page = RenderPage<Pages.Deploy>();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("form.sd-deploy-form")));
    page.FindAll(".sd-segmented button").Single(x => x.TextContent.Trim() == "Persistent").Click();
    Api.Respond(HttpStatusCode.Created, new CreatedInstallerKey(key, $"{key.Id}:x"));
    page.Find("form.sd-deploy-form").Submit();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-command]")));

    Fill(page, "Device ID (optional)", "not-a-guid");

    Assert.NotNull(ErrorOf(page, "Device ID (optional)"));
    Assert.True(Button(page, "Copy command").HasAttribute("disabled"));
    Assert.DoesNotContain("--device-id", page.Find("[data-command]").TextContent);
  }

  [Fact]
  public void Deploy_WithoutWrite_OffersTheKeysKept_AndTakesTheirSecret()
  {
    var key = Key("Lab", InstallerKeyType.TimeBased, expires: Now.AddDays(3));
    var other = Key("Office");
    Api.RespondWith(Me(PermissionNames.AgentInstall, PermissionNames.InstallerKeyRead));
    Api.RespondWith(new InstallerKeyList { Items = [key, other] });
    var page = RenderPage<Pages.Deploy>();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("select")));

    Assert.Empty(page.FindAll(".sd-segmented"));
    Fill(page, "Key or secret", $"{other.Id}:whatever");
    page.Find("form.sd-deploy-form").Submit();
    page.WaitForAssertion(() => Assert.Contains("isn't the one selected", page.Markup));

    Fill(page, "Key or secret", "kept-secret");
    page.Find("form.sd-deploy-form").Submit();

    page.WaitForAssertion(() => Assert.EndsWith($"--installer-key {key.Id}:kept-secret", page.Find("[data-command]").TextContent));
    Assert.Empty(page.FindAll("[data-key]"));
    Assert.Equal(2, Api.Requests.Count);
  }

  [Fact]
  public void TheKeysPage_TellsAboutEachKey()
  {
    Api.RespondWith(Me(PermissionNames.InstallerKeyRead));
    Api.RespondWith(new InstallerKeyList
    {
      Items =
      [
        Key("Front desk", InstallerKeyType.UsageBased, allowed: 3, expires: Now.AddHours(20), uses: 1),
        Key(null, InstallerKeyType.Persistent, uses: 7),
      ],
    });

    var page = RenderPage<Pages.InstallerKeys>();

    page.WaitForAssertion(() => Assert.Equal(2, page.FindAll("tbody tr").Count));
    var usage = page.Find("tr[data-key='Front desk']");
    Assert.Contains("Usage-based", usage.TextContent);
    Assert.Equal("1 of 3", usage.QuerySelector("[data-cell=uses]")!.TextContent.Trim());
    Assert.Contains("admin@example.com", usage.TextContent);
    var persistent = page.FindAll("tbody tr")[1];
    Assert.Contains("Unnamed", persistent.TextContent);
    Assert.Contains("Never", persistent.TextContent);
    Assert.Equal("7", persistent.QuerySelector("[data-cell=uses]")!.TextContent.Trim());
    Assert.DoesNotContain(page.FindAll("button"), x => x.TextContent.Trim() is "Rename" or "Revoke");
    Assert.Empty(page.FindAll("a[href=deploy]"));
  }

  [Fact]
  public void TheKeysPage_ShowsAKeysInstalls_RenamesAndRevokes()
  {
    var key = Key("Lab");
    var device = Guid.NewGuid();
    Api.RespondWith(Me(PermissionNames.InstallerKeyRead, PermissionNames.InstallerKeyWrite, PermissionNames.AgentInstall));
    Api.RespondWith(new InstallerKeyList { Items = [key] });
    var page = RenderPage<Pages.InstallerKeys>();
    page.WaitForAssertion(() => Assert.Single(page.FindAll("tbody tr")));
    Assert.NotNull(page.Find("a[href=deploy]"));

    Api.RespondWith(new InstallerKeyUsageList { Items = [new InstallerKeyUsage(Guid.NewGuid(), device, Now.AddHours(-2), "203.0.113.7")] });
    Button(page, "Usages").Click();
    page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-usages]")));
    Assert.Equal($"GET {Routes.InstallerKeyUsages(key.Id)}", Api.Requests[2]);
    Assert.Equal($"devices/{device}", page.Find("[data-usages] a").GetAttribute("href"));
    Assert.Contains("203.0.113.7", page.Find("[data-usages]").TextContent);
    Button(page, "Close").Click();

    Button(page, "Rename").Click();
    Fill(page, "Name", "  ");
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new InstallerKeyList { Items = [key with { Name = null }] });
    page.FindAll("button").Last(x => x.TextContent.Trim() == "Rename").Click();
    page.WaitForAssertion(() => Assert.Contains("Unnamed", page.Find("tbody").TextContent));
    Assert.Equal($"PUT {Routes.InstallerKey(key.Id)}", Api.Requests[3]);
    Assert.Contains("\"name\":null", Api.Bodies[3]);

    Button(page, "Revoke").Click();
    Api.Respond(HttpStatusCode.NoContent);
    Api.RespondWith(new InstallerKeyList());
    page.FindAll("button").Last(x => x.TextContent.Trim() == "Revoke").Click();

    page.WaitForAssertion(() => Assert.Contains("No installer keys", page.Markup));
    Assert.Equal($"DELETE {Routes.InstallerKey(key.Id)}", Api.Requests[5]);
  }

  private IRenderedComponent<CascadingAuthenticationState> RenderPage<TPage>()
    where TPage : Microsoft.AspNetCore.Components.IComponent =>
    Render<CascadingAuthenticationState>(x => x.AddChildContent<TPage>());

  private static AngleSharp.Dom.IElement Button(IRenderedComponent<CascadingAuthenticationState> page, string text) =>
    page.FindAll("button").First(x => x.TextContent.Trim() == text);

  private sealed class FixedClock(DateTimeOffset now) : TimeProvider
  {
    public override DateTimeOffset GetUtcNow() => now;
  }
}
