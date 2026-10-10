using System.Net;
using StealthDesk.Contracts.Accounts;
using StealthDesk.Contracts.AuthorizationLogs;
using StealthDesk.Contracts.Invites;
using StealthDesk.Contracts.Permissions;
using StealthDesk.Server.Tests.Infrastructure;
using StealthDesk.Web.Server.Email;

namespace StealthDesk.Server.Tests;

/// <summary>Inviting people into a tenant: creating, listing and deleting invites, and accepting one from its link.</summary>
public class InviteApiTests
{
  private const string NewPassword = "Brand-new-Passw0rd";

  private static CancellationToken Cancel => TestContext.Current.CancellationToken;

  [Fact]
  public async Task Invite_CreatesTheAccountInTheTenant_AndEmailsTheLink()
  {
    var emails = new CapturedEmails();
    using var server = ServerHost.InMemory().WithCapturedEmails(emails);
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var invite = await InviteAsync(admin, tenant, "  Joao@Example.com ");

    Assert.Equal("joao@example.com", invite.InviteeEmail);
    var code = Code(invite);
    Assert.Equal(64, code.Length);
    Assert.All(code, x => Assert.True(char.IsAsciiLetterOrDigit(x)));
    var account = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(x => x.Email == "joao@example.com"));
    Assert.Equal(tenant, account.TenantId);
    Assert.True(account.EmailConfirmed);
    var message = emails.To("joao@example.com");
    Assert.Equal($"/{Routes.InviteConfirmationPage}/{code}", CapturedEmails.LinkIn(message));
  }

  [Fact]
  public async Task Invite_WhenTheEmailCantBeSent_StillStands()
  {
    using var server = ServerHost.InMemory().WithServices(x => x.AddSingleton<IEmailTransport, FailingTransport>());
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var invite = await InviteAsync(admin, tenant, "joao@example.com");

    Assert.Equal(64, Code(invite).Length);
    Assert.Single(await ListAsync(admin, tenant));
  }

  [Theory]
  [InlineData("admin@example.com", HttpStatusCode.Conflict)]
  [InlineData("ADMIN@example.com", HttpStatusCode.Conflict)]
  [InlineData("not-an-email", HttpStatusCode.BadRequest)]
  [InlineData("   ", HttpStatusCode.BadRequest)]
  public async Task Invite_RefusesAnEmailThatCantBeInvited(string email, HttpStatusCode expected)
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");

    var response = await admin.PostAsJsonAsync(Invites(tenant), new CreateInviteRequest(email), Cancel);

    Assert.Equal(expected, response.StatusCode);
    Assert.Empty(await ListAsync(admin, tenant));
  }

  [Fact]
  public async Task Invite_RefusesAnEmailInvitedOrRegisteredInAnotherTenant()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (first, firstTenant) = await RegisterAsync(server, "first@example.com");
    var (second, secondTenant) = await RegisterAsync(server, "second@example.com");
    await InviteAsync(first, firstTenant, "joao@example.com");

    var invitedElsewhere = await second.PostAsJsonAsync(Invites(secondTenant), new CreateInviteRequest("joao@example.com"), Cancel);
    var registeredElsewhere = await second.PostAsJsonAsync(Invites(secondTenant), new CreateInviteRequest("first@example.com"), Cancel);

    Assert.Equal(HttpStatusCode.Conflict, invitedElsewhere.StatusCode);
    Assert.Equal(HttpStatusCode.Conflict, registeredElsewhere.StatusCode);
  }

  // Deleting the invited account on the users page leaves the invite behind; the address stays taken until the
  // invite is deleted too.
  [Fact]
  public async Task Invite_RefusesAnEmailWithAPendingInvite_EvenAfterItsAccountWasDeleted()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    await InviteAsync(admin, tenant, "joao@example.com");
    var account = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(x => x.Email == "joao@example.com"));
    Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Routes.User(account.Id) + $"?tenantId={tenant}", Cancel)).StatusCode);

    var again = await admin.PostAsJsonAsync(Invites(tenant), new CreateInviteRequest("joao@example.com"), Cancel);

    Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    Assert.Contains("pending invite", await again.Content.ReadAsStringAsync(Cancel));
  }

  [Fact]
  public async Task List_ShowsTheLinkOnlyToThoseWhoMayManageUsers()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var invite = await InviteAsync(admin, tenant, "joao@example.com");
    var reader = await TestAccounts.CreateUserAsync(server, "reader@example.com", tenant);
    await TestPermissions.AssignAsync(server, reader, PermissionNames.TenantUsersRead, PermissionScopeKind.Tenant, tenant);
    using var readerClient = await ClientForAsync(server, "reader@example.com");

    var forAdmin = Assert.Single(await ListAsync(admin, tenant));
    var forReader = Assert.Single(await ListAsync(readerClient, tenant));

    Assert.Equal(invite.InviteUrl, forAdmin.InviteUrl);
    Assert.Equal("joao@example.com", forReader.InviteeEmail);
    Assert.DoesNotContain(Code(invite), forReader.InviteUrl);
    Assert.EndsWith($"/{Routes.InviteConfirmationPage}", forReader.InviteUrl);
  }

  [Fact]
  public async Task Delete_RemovesTheInviteAndItsUnusedAccount()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var invite = await InviteAsync(admin, tenant, "joao@example.com");

    var deleted = await admin.DeleteAsync(Routes.Invite(invite.Id) + $"?tenantId={tenant}", Cancel);
    var again = await admin.DeleteAsync(Routes.Invite(invite.Id) + $"?tenantId={tenant}", Cancel);
    var accept = await AcceptAsync(server, Code(invite), "joao@example.com");

    Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, accept.StatusCode);
    Assert.False(await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().AnyAsync(x => x.Email == "joao@example.com")));
  }

  [Fact]
  public async Task Delete_AnotherTenantsInvite_IsForbidden()
  {
    using var server = ServerHost.InMemory().With("Accounts:EnablePublicRegistration", "true");
    var (first, firstTenant) = await RegisterAsync(server, "first@example.com");
    var (second, secondTenant) = await RegisterAsync(server, "second@example.com");
    var invite = await InviteAsync(first, firstTenant, "joao@example.com");

    var response = await second.DeleteAsync(Routes.Invite(invite.Id) + $"?tenantId={secondTenant}", Cancel);

    Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    Assert.Single(await ListAsync(first, firstTenant));
  }

  [Fact]
  public async Task Accept_SetsThePassword_KeepsTheUserInTheInvitingTenant_AndGrantsTheBaseline()
  {
    using var server = await ServerHost.OnPostgresAsync();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var invite = await InviteAsync(admin, tenant, "joao@example.com");
    var account = await server.WithDbAsync(db => db.Users.IgnoreQueryFilters().SingleAsync(x => x.Email == "joao@example.com"));
    var seededBefore = await server.WithDbAsync(db => db.PermissionAssignments.IgnoreQueryFilters().CountAsync(x => x.PrincipalId == account.Id));

    var accepted = await AcceptAsync(server, Code(invite), " JOAO@example.com ");
    var again = await AcceptAsync(server, Code(invite), "joao@example.com");

    Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    using var joao = await ClientForAsync(server, "joao@example.com", NewPassword);
    var me = await joao.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel);
    Assert.Equal(tenant, me!.TenantId);
    Assert.Equal(PermissionPresets.PermissionsOf(PermissionPresets.SelfService).Order(), me.Permissions.Order());
    Assert.Empty(await ListAsync(admin, tenant));

    var log = await server.WithDbAsync(db => db.AuthorizationChanges.Where(x => x.OwningTenantId == tenant).ToListAsync());
    Assert.Equal(seededBefore, log.Count(x => x.Action == AuthorizationChangeActions.PermissionAssignmentDeleted));
    Assert.Equal(2, log.Count(x => x.Action == AuthorizationChangeActions.PermissionAssignmentsSeeded && x.TargetId == account.Id));
  }

  [Theory]
  [InlineData(true, "other@example.com", NewPassword, HttpStatusCode.NotFound)]
  [InlineData(false, "joao@example.com", NewPassword, HttpStatusCode.NotFound)]
  [InlineData(true, "joao@example.com", "short", HttpStatusCode.BadRequest)]
  public async Task Accept_WithAWrongCodeEmailOrPassword_ChangesNothing(bool rightCode, string email, string password, HttpStatusCode expected)
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var invite = await InviteAsync(admin, tenant, "joao@example.com");

    var response = await AcceptAsync(server, rightCode ? Code(invite) : new string('x', 64), email, password);

    Assert.Equal(expected, response.StatusCode);
    Assert.Single(await ListAsync(admin, tenant));
    using var anonymous = TestAccounts.Client(server);
    Assert.Equal(HttpStatusCode.Unauthorized, (await TestAccounts.SignInAsync(anonymous, "joao@example.com", NewPassword)).StatusCode);
  }

  [Fact]
  public async Task WithoutThePermissions_TheInviteRoutesAreForbidden()
  {
    using var server = ServerHost.InMemory();
    var (admin, tenant) = await RegisterAsync(server, "admin@example.com");
    var invite = await InviteAsync(admin, tenant, "joao@example.com");
    await TestAccounts.CreateUserAsync(server, "plain@example.com", tenant);
    using var plain = await ClientForAsync(server, "plain@example.com");

    Assert.Equal(HttpStatusCode.Forbidden, (await plain.GetAsync(Invites(tenant), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.PostAsJsonAsync(Invites(tenant), new CreateInviteRequest("x@example.com"), Cancel)).StatusCode);
    Assert.Equal(HttpStatusCode.Forbidden, (await plain.DeleteAsync(Routes.Invite(invite.Id) + $"?tenantId={tenant}", Cancel)).StatusCode);
  }

  private static string Invites(Guid tenantId) => $"{Routes.Invites}?tenantId={tenantId}";

  private static string Code(TenantInvite invite) => invite.InviteUrl[(invite.InviteUrl.LastIndexOf('/') + 1)..];

  private static async Task<TenantInvite> InviteAsync(HttpClient client, Guid tenant, string email)
  {
    var response = await client.PostAsJsonAsync(Invites(tenant), new CreateInviteRequest(email), Cancel);
    Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<TenantInvite>(Cancel))!;
  }

  private static async Task<IReadOnlyList<TenantInvite>> ListAsync(HttpClient client, Guid tenant)
  {
    var response = await client.GetAsync(Invites(tenant), Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    return (await response.Content.ReadFromJsonAsync<TenantInviteList>(Cancel))!.Items;
  }

  private static async Task<HttpResponseMessage> AcceptAsync(ServerHost server, string code, string email, string password = NewPassword)
  {
    using var anonymous = TestAccounts.Client(server);
    return await anonymous.PostAsJsonAsync(Routes.AcceptInvite, new AcceptInviteRequest(code, email, password), Cancel);
  }

  private static async Task<(HttpClient Client, Guid TenantId)> RegisterAsync(ServerHost server, string email)
  {
    using var anonymous = TestAccounts.Client(server);
    var response = await anonymous.PostAsJsonAsync($"{Routes.Auth}/register", new { email, password = TestAccounts.Password }, Cancel);
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var client = await ClientForAsync(server, email);
    return (client, (await client.GetFromJsonAsync<CurrentUser>(Routes.CurrentUser, Cancel))!.TenantId);
  }

  private static async Task<HttpClient> ClientForAsync(ServerHost server, string email, string password = TestAccounts.Password)
  {
    using var anonymous = TestAccounts.Client(server);
    var signIn = await TestAccounts.SignInAsync(anonymous, email, password);
    Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
    var client = TestAccounts.Client(server);
    client.DefaultRequestHeaders.Add("Cookie", TestAccounts.SessionCookie(signIn)!);
    return client;
  }

  private sealed class FailingTransport : IEmailTransport
  {
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
      throw new InvalidOperationException("The mail server is down.");
  }
}
