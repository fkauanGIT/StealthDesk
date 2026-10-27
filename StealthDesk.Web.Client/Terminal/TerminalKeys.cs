using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace StealthDesk.Web.Client.Terminal;

/// <summary>The keys the terminal's input handles itself, and its caret, through terminal.js.</summary>
public interface ITerminalKeys
{
  Task AttachAsync(ElementReference input);

  Task<int> CaretAsync(ElementReference input);

  Task PlaceCaretAsync(ElementReference input, int position);

  /// <summary>Holds back Enter's own action in <paramref name="element"/>, whose Enter the page handles.</summary>
  Task HoldEnterAsync(ElementReference element);
}

public sealed class TerminalKeys(IJSRuntime js) : ITerminalKeys, IAsyncDisposable
{
  private Task<IJSObjectReference>? _module;

  public async Task AttachAsync(ElementReference input) =>
    await (await ModuleAsync()).InvokeVoidAsync("attach", input);

  public async Task<int> CaretAsync(ElementReference input) =>
    await (await ModuleAsync()).InvokeAsync<int>("caret", input);

  public async Task PlaceCaretAsync(ElementReference input, int position) =>
    await (await ModuleAsync()).InvokeVoidAsync("placeCaret", input, position);

  public async Task HoldEnterAsync(ElementReference element) =>
    await (await ModuleAsync()).InvokeVoidAsync("holdEnter", element);

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
