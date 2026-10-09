using System.Net;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Web.Client.Accounts;
using StealthDesk.Web.Client.AuthorizationLogs;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The authorization log panel: entries, before and after, filters and paging.</summary>
public class AuthorizationLogPanelTests : AccountTestContext
{
  private static readonly Guid Tenant = Guid.Parse("11111111-2222-3333-4444-555555555555");

  public AuthorizationLogPanelTests()
  {
    Services.AddSingleton<SessionGuard>();
    Services.AddSingleton<AuthorizationLogApi>();
  }

  [Fact]
  public void Entries_ShowWhatWhoAndWhen_AndExpandToBeforeAndAfter()
  {
    var entry = Entry(after: "{\"count\":30,\"presets\":[\"Baseline\"]}");
    Api.RespondWith(PageOf(1, entry));

    var panel = Render<AuthorizationLogPanel>(x => x.Add(p => p.TenantId, Tenant));

    panel.WaitForAssertion(() => Assert.Contains("permission-assignments-seeded", panel.Markup));
    Assert.Contains(entry.TargetId.ToString()!, panel.Markup);
    Assert.Contains("10.0.0.5", panel.Markup);
    Assert.Empty(panel.FindAll(".sd-log-json"));

    panel.Find("button[aria-label='Show before and after']").Click();

    var json = panel.FindAll(".sd-log-json");
    Assert.Equal("(none)", json[0].TextContent);
    Assert.Contains("\"count\": 30", json[1].TextContent);
    Assert.Equal("true", panel.Find("button[aria-label='Hide before and after']").GetAttribute("aria-expanded"));
  }

  [Fact]
  public void Filters_AreSentToTheServer_FromTheFirstPage()
  {
    Api.RespondWith(PageOf(0));
    var panel = Render<AuthorizationLogPanel>(x => x.Add(p => p.TenantId, Tenant));
    panel.WaitForAssertion(() => Assert.Contains("No changes recorded", panel.Markup));

    Select(panel, "Action", AuthorizationChangeActions.PermissionAssignmentsSeeded);
    Select(panel, "Actor", AuthorizationChangeActors.System);
    Fill(panel, "Actor or target ID", " 3f2c ");
    Fill(panel, "From", "2026-10-01");
    Api.RespondWith(PageOf(0));
    panel.Find("form").Submit();

    panel.WaitForAssertion(() => Assert.Equal(2, Api.Requests.Count));
    var sent = Api.Requests[1];
    Assert.StartsWith($"GET {Routes.AuthorizationLogs}?page=0&pageSize=25", sent);
    Assert.Contains("action=permission-assignments-seeded", sent);
    Assert.Contains("actorKind=system", sent);
    Assert.Contains("search=3f2c", sent);
    Assert.Contains("from=2026-10-01T00%3A00%3A00", sent);
    Assert.Contains($"tenantId={Tenant}", sent);
    Assert.DoesNotContain("targetKind", sent);
  }

  [Fact]
  public void Paging_MovesThroughThePages_AndAPageSizeChangeStartsOver()
  {
    Api.RespondWith(PageOf(30, [.. Enumerable.Range(0, 25).Select(_ => Entry())]));
    var panel = Render<AuthorizationLogPanel>(x => x.Add(p => p.TenantId, Tenant));
    panel.WaitForAssertion(() => Assert.Equal("1–25 of 30", panel.Find("[data-pager=range]").TextContent));
    Assert.True(Button(panel, "Previous").HasAttribute("disabled"));

    Api.RespondWith(PageOf(30, [.. Enumerable.Range(0, 5).Select(_ => Entry())]));
    Button(panel, "Next").Click();

    panel.WaitForAssertion(() => Assert.Equal("26–30 of 30", panel.Find("[data-pager=range]").TextContent));
    Assert.Contains("page=1&pageSize=25", Api.Requests[1]);
    Assert.True(Button(panel, "Next").HasAttribute("disabled"));

    Api.RespondWith(PageOf(30, [.. Enumerable.Range(0, 30).Select(_ => Entry())]));
    Select(panel, "Rows per page", "50");

    panel.WaitForAssertion(() => Assert.Contains("page=0&pageSize=50", Api.Requests[2]));
  }

  [Fact]
  public void Refused_SaysWhy()
  {
    Api.Respond(HttpStatusCode.Forbidden);

    var panel = Render<AuthorizationLogPanel>(x => x.Add(p => p.TenantId, Tenant));

    panel.WaitForAssertion(() => Assert.Contains("Your account can't read this log.", panel.Markup));
  }

  [Fact]
  public void ExpiredSession_SendsTheUserToSignIn()
  {
    Navigation.NavigateTo("authorization-logs");
    Api.Respond(HttpStatusCode.Unauthorized);

    Render<AuthorizationLogPanel>(x => x.Add(p => p.TenantId, Tenant));

    Assert.Equal("account/sign-in?returnUrl=%2Fauthorization-logs", Location);
  }

  [Fact]
  public void QueryString_LeavesOutEmptyFilters_AndEscapesText()
  {
    var query = AuthorizationLogApi.QueryString(new AuthorizationChangeQuery { Search = "a&b c", Page = 2, PageSize = 100 });

    Assert.Equal("page=2&pageSize=100&search=a%26b%20c", query);
  }

  private static void Select(IRenderedComponent<AuthorizationLogPanel> panel, string label, string value) =>
    Input(panel, label).Change(value);

  private static AngleSharp.Dom.IElement Button(IRenderedComponent<AuthorizationLogPanel> panel, string text) =>
    panel.FindAll("button").Single(x => x.TextContent.Trim() == text);

  private static AuthorizationChangeEntry Entry(string? after = null) => new(
    Guid.NewGuid(),
    AuthorizationChangeActions.PermissionAssignmentsSeeded,
    AuthorizationChangeActors.System,
    null,
    AuthorizationChangeTargets.User,
    Guid.NewGuid(),
    Tenant,
    "10.0.0.5",
    new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero),
    null,
    after);

  private static AuthorizationChangePage PageOf(int total, params AuthorizationChangeEntry[] items) =>
    new() { Items = items, TotalItems = total };
}
