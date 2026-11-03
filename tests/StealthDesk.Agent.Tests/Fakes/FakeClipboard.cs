using StealthDesk.DesktopClient;

namespace StealthDesk.Agent.Tests.Fakes;

/// <summary>A clipboard in memory: what was set, and whatever a test says it holds to be read.</summary>
internal sealed class FakeClipboard : IClipboard
{
    public string? Text { get; set; }

    public Task<string?> GetTextAsync() => Task.FromResult(Text);

    public Task SetTextAsync(string text)
    {
        Text = text;
        return Task.CompletedTask;
    }
}
