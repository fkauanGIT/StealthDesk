using Microsoft.JSInterop;

namespace StealthDesk.Web.Client.Accounts;

/// <summary>What the authenticator answered: the credential as JSON, or why there is none.</summary>
public sealed record PasskeyOutcome(string? CredentialJson, string? Error)
{
  public bool Succeeded => CredentialJson is not null;

  /// <summary>Why it failed, in words for the page.</summary>
  public string Message => Error switch
  {
    // The browser reports a closed prompt and a phone that couldn't connect the same way, on purpose.
    "NotAllowedError" or "AbortError" =>
      "No passkey was used. The prompt was closed, or the device couldn't finish — for a phone, check that Bluetooth is on for both devices.",
    "InvalidStateError" => "This device already has a passkey for your account.",
    "SecurityError" => "Passkeys need this site's real address, over HTTPS or on localhost.",
    "NotSupportedError" => "This browser doesn't support passkeys.",
    _ => "The passkey didn't work. Try again.",
  };
}

/// <summary>The browser's WebAuthn API, through passkeys.js.</summary>
public interface IPasskeyBridge
{
  Task<bool> IsSupportedAsync();

  Task<bool> IsAutofillSupportedAsync();

  Task<PasskeyOutcome> CreateAsync(string creationOptionsJson);

  /// <param name="autofill">Wait for the user to pick a passkey in the email field instead of prompting now.</param>
  Task<PasskeyOutcome> GetAsync(string requestOptionsJson, bool autofill = false);

  /// <summary>Cancels a ceremony that is waiting, e.g. autofill when the user signs in another way.</summary>
  Task AbortAsync();
}

public sealed class PasskeyBridge(IJSRuntime js) : IPasskeyBridge, IAsyncDisposable
{
  private Task<IJSObjectReference>? _module;

  public async Task<bool> IsSupportedAsync() => await (await ModuleAsync()).InvokeAsync<bool>("isSupported");

  public async Task<bool> IsAutofillSupportedAsync() => await (await ModuleAsync()).InvokeAsync<bool>("isAutofillSupported");

  public Task<PasskeyOutcome> CreateAsync(string creationOptionsJson) => RunAsync("create", creationOptionsJson);

  public Task<PasskeyOutcome> GetAsync(string requestOptionsJson, bool autofill = false) => RunAsync("get", requestOptionsJson, autofill);

  public async Task AbortAsync()
  {
    if (_module is not null)
    {
      await (await _module).InvokeVoidAsync("abort");
    }
  }

  public async ValueTask DisposeAsync()
  {
    if (_module is not null)
    {
      try
      {
        var module = await _module;
        await module.InvokeVoidAsync("abort");
        await module.DisposeAsync();
      }
      catch (JSDisconnectedException)
      {
        // The page is already gone.
      }
    }
  }

  private async Task<PasskeyOutcome> RunAsync(string ceremony, params object[] args)
  {
    // Autofill may wait for the whole visit: no timeout on the call.
    var result = await (await ModuleAsync()).InvokeAsync<Result>(ceremony, CancellationToken.None, args);
    return new PasskeyOutcome(result.CredentialJson, result.Error);
  }

  private Task<IJSObjectReference> ModuleAsync() =>
    _module ??= js.InvokeAsync<IJSObjectReference>("import", "./js/passkeys.js").AsTask();

  private sealed record Result(string? CredentialJson, string? Error);
}
