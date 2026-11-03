using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace StealthDesk.DesktopClient;

/// <summary>Reads and writes the session's clipboard, as text. Calls are serialized: the clipboard is one shared thing.</summary>
internal interface IClipboard
{
    Task<string?> GetTextAsync();

    Task SetTextAsync(string text);
}

[SupportedOSPlatform("windows6.1")]
internal sealed class WindowsClipboard(ILogger<WindowsClipboard> logger) : IClipboard
{
    private const uint CF_UNICODETEXT = 13;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<string?> GetTextAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!PInvoke.OpenClipboard(HWND.Null))
            {
                logger.LogDebug("Couldn't open the clipboard to read it.");
                return null;
            }

            try
            {
                var handle = PInvoke.GetClipboardData(CF_UNICODETEXT);
                return handle == default ? null : Marshal.PtrToStringUni(handle);
            }
            finally
            {
                PInvoke.CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Reading the clipboard failed.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetTextAsync(string text)
    {
        await _gate.WaitAsync();
        var memory = nint.Zero;
        try
        {
            if (!PInvoke.OpenClipboard(HWND.Null))
            {
                logger.LogDebug("Couldn't open the clipboard to write it.");
                return;
            }

            try
            {
                PInvoke.EmptyClipboard();
                // The clipboard owns the memory once the set succeeds, so it isn't freed here.
                memory = Marshal.StringToHGlobalUni(text);
                if (PInvoke.SetClipboardData(CF_UNICODETEXT, new HANDLE(memory)) != default)
                {
                    memory = nint.Zero;
                }
            }
            finally
            {
                PInvoke.CloseClipboard();
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Writing the clipboard failed.");
        }
        finally
        {
            if (memory != nint.Zero)
            {
                Marshal.FreeHGlobal(memory);
            }

            _gate.Release();
        }
    }
}
