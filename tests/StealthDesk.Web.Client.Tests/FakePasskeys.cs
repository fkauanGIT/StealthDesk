using StealthDesk.Web.Client.Accounts;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The browser's WebAuthn without a browser: the test decides what the authenticator answers.</summary>
internal sealed class FakePasskeys : IPasskeyBridge
{
  private readonly List<string> _calls = [];

  public bool Supported { get; set; }

  public bool AutofillSupported { get; set; }

  /// <summary>What the next prompt answers; autofill waits until the test answers it.</summary>
  public PasskeyOutcome Answer { get; set; } = new("{\"id\":\"credential\"}", null);

  public TaskCompletionSource<PasskeyOutcome> Autofill { get; } = new();

  /// <summary>"create", "get", "autofill" and "abort", in order, with the options passed.</summary>
  public IReadOnlyList<string> Calls => [.. _calls];

  public Task<bool> IsSupportedAsync() => Task.FromResult(Supported);

  public Task<bool> IsAutofillSupportedAsync() => Task.FromResult(AutofillSupported);

  public Task<PasskeyOutcome> CreateAsync(string creationOptionsJson)
  {
    _calls.Add($"create {creationOptionsJson}");
    return Task.FromResult(Answer);
  }

  public Task<PasskeyOutcome> GetAsync(string requestOptionsJson, bool autofill = false)
  {
    _calls.Add($"{(autofill ? "autofill" : "get")} {requestOptionsJson}");
    return autofill ? Autofill.Task : Task.FromResult(Answer);
  }

  public Task AbortAsync()
  {
    _calls.Add("abort");
    Autofill.TrySetResult(new PasskeyOutcome(null, "AbortError"));
    return Task.CompletedTask;
  }
}
