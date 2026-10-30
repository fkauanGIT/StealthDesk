using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using StealthDesk.Branding;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace StealthDesk.DesktopClient;

/// <summary>
/// A standard Windows message box on this session's screen, which closes by itself. Sent through Terminal Services,
/// so it shows even when the client runs as SYSTEM, and nothing waits for the user to close it.
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal sealed unsafe class SessionMessageBox(ILogger<SessionMessageBox> logger) : IUserNotice
{
  public static readonly TimeSpan ShownFor = TimeSpan.FromSeconds(10);

  public void Show(string message)
  {
    var title = Brand.Name;
    MESSAGEBOX_RESULT response;
    BOOL sent;
    fixed (char* titleText = title)
    fixed (char* messageText = message)
    {
      sent = PInvoke.WTSSendMessage(
        HANDLE.Null,
        (uint)CurrentSession.Id,
        titleText,
        (uint)(title.Length * sizeof(char)),
        messageText,
        (uint)(message.Length * sizeof(char)),
        MESSAGEBOX_STYLE.MB_OK | MESSAGEBOX_STYLE.MB_ICONINFORMATION,
        (uint)ShownFor.TotalSeconds,
        &response,
        bWait: false);
    }

    if (!sent)
    {
      logger.LogWarning("The message couldn't be shown to the user (Windows error {Error}).", Marshal.GetLastPInvokeError());
    }
  }
}
