using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace StealthDesk.Web.Client.Terminal;

/// <summary>The keys the terminal's input handles itself, through terminal.js.</summary>
public interface ITerminalKeys
{
  Task AttachAsync(ElementReference input);
}

public sealed class TerminalKeys(IJSRuntime js) : ITerminalKeys, IAsyncDisposable
{
  private Task<IJSObjectReference>? _module;

  public async Task AttachAsync(ElementReference input) =>
    await (await ModuleAsync()).InvokeVoidAsync("attach", input);

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
    _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/terminal.js").AsTask();
}
