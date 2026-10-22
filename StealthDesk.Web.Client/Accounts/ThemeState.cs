using Microsoft.JSInterop;
using StealthDesk.Contracts.Accounts;

namespace StealthDesk.Web.Client.Accounts;

/// <summary>The page's theme in the browser, through theme.js.</summary>
public interface IThemeBridge
{
  /// <summary>The choice this browser remembers, or null when none was made here.</summary>
  Task<ThemeMode?> StoredAsync();

  Task ApplyAsync(ThemeMode mode);
}

public sealed class ThemeBridge(IJSRuntime js) : IThemeBridge, IAsyncDisposable
{
  private Task<IJSObjectReference>? _module;

  public async Task<ThemeMode?> StoredAsync() =>
    Enum.TryParse<ThemeMode>(await (await ModuleAsync()).InvokeAsync<string?>("stored"), ignoreCase: true, out var mode) ? mode : null;

  public async Task ApplyAsync(ThemeMode mode) =>
    await (await ModuleAsync()).InvokeVoidAsync("apply", mode.ToString().ToLowerInvariant());

  public async ValueTask DisposeAsync()
  {
    if (_module is not null)
    {
      try
      {
        await (await _module).DisposeAsync();
      }
      catch (JSDisconnectedException)
      {
        // The page is already gone.
      }
    }
  }

  private Task<IJSObjectReference> ModuleAsync() =>
    _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/theme.js").AsTask();
}

/// <summary>
/// The theme the web client shows. Signed in, it is the user's own, kept with their account; before signing in, the
/// one this browser last used, or the server's default.
/// </summary>
public sealed class ThemeState(IThemeBridge bridge, AccountApi api)
{
  public ThemeMode Mode { get; private set; }

  public event Action? Changed;

  /// <summary>Shows the theme saved with the signed-in user's account.</summary>
  public Task UseAccountThemeAsync(ThemeMode mode) => ShowAsync(mode);

  /// <summary>Before signing in: the theme this browser last used, or else the server's default.</summary>
  public async Task UseBrowserThemeAsync(ThemeMode serverDefault) => await ShowAsync(await bridge.StoredAsync() ?? serverDefault);

  /// <summary>Shows the chosen theme at once and keeps it with the account; put back when it can't be kept.</summary>
  public async Task<AccountResult> ChooseAsync(ThemeMode mode)
  {
    var previous = Mode;
    await ShowAsync(mode);
    var saved = await api.SetThemeAsync(mode);
    if (!saved.Succeeded)
    {
      await ShowAsync(previous);
    }

    return saved;
  }

  private async Task ShowAsync(ThemeMode mode)
  {
    Mode = mode;
    await bridge.ApplyAsync(mode);
    Changed?.Invoke();
  }
}
