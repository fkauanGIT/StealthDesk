using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;

namespace StealthDesk.Agent.Core.Terminal;

/// <summary>
/// How PowerShell reads what programs (ping, ipconfig, cmd) write. Windows programs write in the system's OEM code
/// page (850 on a Portuguese Windows) when their output is taken, with a console or without; PowerShell reads it with
/// the console's encoding, which without a console, as for the installed service, is the ANSI one (1252), and in a
/// UTF-8 console UTF-8: accented letters came out garbled.
/// </summary>
internal static class ProgramOutput
{
  /// <summary>The system's OEM code page, the one Windows programs write in.</summary>
  public static int OemCodePage => (int)GetOEMCP();

  /// <summary>Reads programs' output in the OEM code page, for the whole agent; nothing to do elsewhere than Windows.</summary>
  public static void UseOemCodePage(ILogger logger)
  {
    if (!OperatingSystem.IsWindows())
    {
      return;
    }

    try
    {
      Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
      var oem = Encoding.GetEncoding(OemCodePage);
      if (Console.OutputEncoding.CodePage != oem.CodePage)
      {
        // Also the code page of a console the agent runs in, so its own log lines there read the same way.
        Console.OutputEncoding = oem;
      }
    }
    catch (Exception ex)
    {
      logger.LogWarning(ex, "Couldn't read programs' output in the OEM code page; accented letters they write may show garbled.");
    }
  }

  // The system's, as programs use it, rather than the culture's.
  [DllImport("kernel32.dll")]
  private static extern uint GetOEMCP();
}
