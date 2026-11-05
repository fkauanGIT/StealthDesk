using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.DesktopClient;
using StealthDesk.RemoteControl;

namespace StealthDesk.Agent.Tests;

/// <summary>
/// The real privacy screen on this machine: a window Windows itself reports as covering every display, excluded
/// from this process's own GDI capture exactly as a remote control session's capture would be. Needs Windows 10
/// 2004 or later and a desktop.
/// </summary>
[SupportedOSPlatform("windows10.0.19041")]
public sealed class RealPrivacyScreenTests : IDisposable
{
  private const string ClassName = "StealthDeskPrivacyScreen";

  private readonly ListLogger _logger = new();
  private readonly WindowsPrivacyScreen _screen;

  public RealPrivacyScreenTests()
  {
    Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041), "WDA_EXCLUDEFROMCAPTURE needs Windows 10 2004 or later.");
    Assert.SkipWhen(GdiScreenCapture.ListDisplays().Count == 0, "This session has no display to capture.");
    _screen = new WindowsPrivacyScreen(_logger);
  }

  [Fact]
  public void WhileActive_TheWindowCoversTheVirtualScreen_AndIsExcludedFromThisProcesssOwnCapture()
  {
    Assert.True(_screen.SetActive(true), $"The privacy screen couldn't be set, so it must have stayed off: {string.Join(" | ", _logger.Messages)}");

    var window = FindWindowW(ClassName, null);
    Assert.NotEqual(0, window);
    Assert.True(IsWindowVisible(window));
    GetWindowRect(window, out var rect);
    Assert.Equal(
      (GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN), GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN)),
      (rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top));

    // This is the same capture a remote control session streams to the viewer: it must see the real desktop, never
    // the black overlay, which is the whole point of excluding the window from capture.
    var primary = GdiScreenCapture.ListDisplays().Single(x => x.IsPrimary);
    using var capture = new GdiScreenCapture();
    var captured = capture.Capture(primary, fullFrame: true, drawCursor: false);
    Assert.NotNull(captured.Frame);
    Assert.Contains(captured.Frame!.Pixels, pixel => pixel != 0);

    Assert.False(_screen.SetActive(false));
    Assert.False(IsWindowVisible(window));
  }

  public void Dispose() => _screen.Dispose();

  private const int SM_XVIRTUALSCREEN = 76;
  private const int SM_YVIRTUALSCREEN = 77;
  private const int SM_CXVIRTUALSCREEN = 78;
  private const int SM_CYVIRTUALSCREEN = 79;

  [StructLayout(LayoutKind.Sequential)]
  private struct RECT
  {
    public int left, top, right, bottom;
  }

  [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  private static extern nint FindWindowW(string className, string? windowName);

  [DllImport("user32.dll")]
  private static extern bool IsWindowVisible(nint window);

  [DllImport("user32.dll")]
  private static extern bool GetWindowRect(nint window, out RECT rect);

  [DllImport("user32.dll")]
  private static extern int GetSystemMetrics(int index);

  private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<WindowsPrivacyScreen>
  {
    public System.Collections.Concurrent.ConcurrentQueue<string> Messages { get; } = new();

    public IDisposable? BeginScope<TState>(TState state)
      where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
      Messages.Enqueue(formatter(state, exception));
  }
}
