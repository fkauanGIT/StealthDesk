using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using StealthDesk.RemoteControl.Input;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;

namespace StealthDesk.DesktopClient;

/// <summary>
/// The session's input desktop through Win32. Following it needs a process that may open the secure desktops, which
/// the desktop client is as the local system.
/// </summary>
[SupportedOSPlatform("windows6.1")]
internal sealed unsafe class WindowsInputDesktop(ILogger<WindowsInputDesktop> logger) : IInputDesktop
{
    private const uint GenericAll = 0x10000000;

    private HDESK _following;

    public string? Name()
    {
        var desktop = PInvoke.OpenInputDesktop(0, true, (DESKTOP_ACCESS_FLAGS)GenericAll);
        if (desktop.IsNull)
        {
            return null;
        }

        try
        {
            return NameOf(desktop);
        }
        finally
        {
            PInvoke.CloseDesktop(desktop);
        }
    }

    public bool SwitchToInput()
    {
        var desktop = PInvoke.OpenInputDesktop(0, true, (DESKTOP_ACCESS_FLAGS)GenericAll);
        if (desktop.IsNull)
        {
            logger.LogDebug("Couldn't open the input desktop to follow it.");
            return false;
        }

        if (!PInvoke.SetThreadDesktop(desktop))
        {
            PInvoke.CloseDesktop(desktop);
            logger.LogDebug("Couldn't set the thread to the input desktop.");
            return false;
        }

        // The previous one is closed only after the thread moved off it.
        if (!_following.IsNull)
        {
            PInvoke.CloseDesktop(_following);
        }

        _following = desktop;
        return true;
    }

    private static string? NameOf(HDESK desktop)
    {
        var handle = new HANDLE(desktop.Value);
        uint needed = 0;
        PInvoke.GetUserObjectInformation(handle, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, null, 0, &needed);
        if (needed == 0)
        {
            return null;
        }

        var buffer = new byte[needed];
        fixed (byte* p = buffer)
        {
            if (!PInvoke.GetUserObjectInformation(handle, USER_OBJECT_INFORMATION_INDEX.UOI_NAME, p, needed, &needed))
            {
                return null;
            }

            return new string((char*)p).Trim('\0');
        }
    }
}
