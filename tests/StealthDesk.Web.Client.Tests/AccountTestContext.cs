using AngleSharp.Dom;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StealthDesk.Web.Client.Accounts;

namespace StealthDesk.Web.Client.Tests;

/// <summary>A bUnit context with the account services wired to a fake API.</summary>
public abstract class AccountTestContext : BunitContext
{
  protected AccountTestContext()
  {
    // bUnit puts placeholders here; the real services decide from the state the fake server answers with.
    Services.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationService>();
    Services.RemoveAll<Microsoft.AspNetCore.Authorization.IAuthorizationPolicyProvider>();
    Services.AddAuthorizationCore();
    Services.AddSingleton(Api.CreateClient());
    Services.AddSingleton<AccountApi>();
    Services.AddSingleton<ServerAuthenticationState>();
    Services.AddSingleton<AuthenticationStateProvider>(sp => sp.GetRequiredService<ServerAuthenticationState>());
    Services.AddSingleton<IPasskeyBridge>(Passkeys);
  }

  /// <summary>Passkeys as the browser offers them; unsupported unless a test says otherwise.</summary>
  private protected FakePasskeys Passkeys { get; } = new();

  private protected FakeApi Api { get; } = new();

  protected NavigationManager Navigation => Services.GetRequiredService<NavigationManager>();

  /// <summary>The page the user ended up on, relative to the site.</summary>
  protected string Location => Navigation.ToBaseRelativePath(Navigation.Uri);

  /// <summary>Types into the field labelled <paramref name="label"/>, the way a browser reports it.</summary>
  protected static void Fill<T>(IRenderedComponent<T> page, string label, string value)
    where T : IComponent => Input(page, label).Change(value);

  protected static IElement Input<T>(IRenderedComponent<T> page, string label)
    where T : IComponent
  {
    var labelElement = page.FindAll("label").Single(x => x.TextContent.Trim() == label);
    return page.Find($"#{labelElement.GetAttribute("for")}");
  }

  protected static IElement ErrorOf<T>(IRenderedComponent<T> page, string label)
    where T : IComponent => page.Find($"#{Input(page, label).GetAttribute("aria-describedby")}");
}
