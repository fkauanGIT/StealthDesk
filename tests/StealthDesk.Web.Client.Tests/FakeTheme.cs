using StealthDesk.Contracts.Accounts;
using StealthDesk.Web.Client.Accounts;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The page's theme without a browser: what this browser remembers, and every theme shown, in order.</summary>
internal sealed class FakeTheme : IThemeBridge
{
  private readonly List<ThemeMode> _shown = [];

  public ThemeMode? Stored { get; set; }

  public IReadOnlyList<ThemeMode> Shown => [.. _shown];

  public Task<ThemeMode?> StoredAsync() => Task.FromResult(Stored);

  public Task ApplyAsync(ThemeMode mode)
  {
    _shown.Add(mode);
    Stored = mode;
    return Task.CompletedTask;
  }
}
