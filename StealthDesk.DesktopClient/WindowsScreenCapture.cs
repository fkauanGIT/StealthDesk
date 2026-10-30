using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.Graphics.Dxgi.Common;

namespace StealthDesk.DesktopClient;

/// <summary>
/// Captures a display with DXGI desktop duplication, which hands over each new frame from the graphics card with what
/// changed in it, and falls back to GDI wherever that can't work: no card that duplicates, a display it lost, or a
/// whole frame asked for while nothing changed. A display DXGI failed on stays on GDI for a while.
/// </summary>
[SupportedOSPlatform("windows8.0")]
internal sealed class WindowsScreenCapture(ILogger logger) : IScreenCapture
{
  public const string Mode = "DXGI";

  private static readonly TimeSpan FaultedFor = TimeSpan.FromSeconds(10);

  private readonly GdiScreenCapture _gdi = new();
  private readonly Dictionary<string, DateTimeOffset> _faulted = [];
  private DuplicatedOutput? _output;

  public IReadOnlyList<DisplayInfo> Displays() => _gdi.Displays();

  public CursorKind Cursor() => WindowsCursor.Current();

  public CaptureResult Capture(DisplayInfo display, bool fullFrame, bool drawCursor)
  {
    if (OutputFor(display) is { } output)
    {
      try
      {
        var captured = output.Capture(display, fullFrame, drawCursor);
        if (captured is not null)
        {
          return captured;
        }
      }
      catch (COMException ex) when (ex.HResult == DuplicatedOutput.AccessLost)
      {
        // The desktop switched or the display mode changed: duplicate it again next time.
        logger.LogDebug("DXGI lost access to {Display}; duplicating it again.", display.Id);
        Release();
      }
      catch (Exception ex)
      {
        logger.LogWarning(ex, "DXGI failed on {Display}; using GDI for {FaultedFor}: {Error}", display.Id, FaultedFor, ex.Message);
        _faulted[display.Id] = DateTimeOffset.UtcNow + FaultedFor;
        Release();
      }
    }

    // Nothing new from DXGI, but the whole screen is needed: GDI copies it as it is.
    return _gdi.Capture(display, fullFrame, drawCursor);
  }

  public void Dispose() => Release();

  private DuplicatedOutput? OutputFor(DisplayInfo display)
  {
    if (_faulted.TryGetValue(display.Id, out var until) && until > DateTimeOffset.UtcNow)
    {
      return null;
    }

    if (_output?.DisplayId == display.Id)
    {
      return _output;
    }

    Release();
    try
    {
      _output = DuplicatedOutput.Create(display.Id);
      if (_output is null)
      {
        _faulted[display.Id] = DateTimeOffset.UtcNow + FaultedFor;
      }
    }
    catch (Exception ex)
    {
      logger.LogInformation("DXGI can't duplicate {Display}; using GDI for {FaultedFor}: {Error}", display.Id, FaultedFor, ex.Message);
      _faulted[display.Id] = DateTimeOffset.UtcNow + FaultedFor;
    }

    return _output;
  }

  private void Release()
  {
    _output?.Dispose();
    _output = null;
  }

  /// <summary>One display duplicated on the card that drives it, and a texture the CPU can read its frames from.</summary>
  private sealed unsafe class DuplicatedOutput : IDisposable
  {
    public const int AccessLost = unchecked((int)0x887A0026);
    private const int WaitTimeout = unchecked((int)0x887A0027);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private readonly DXGI_MODE_ROTATION _rotation;
    private readonly byte[]?[] _buffers = new byte[2][];
    private ID3D11Texture2D? _staging;
    private Frame? _clean;
    private int _next;

    private DuplicatedOutput(string displayId, ID3D11Device device, ID3D11DeviceContext context, IDXGIOutputDuplication duplication, DXGI_MODE_ROTATION rotation)
    {
      DisplayId = displayId;
      _device = device;
      _context = context;
      _duplication = duplication;
      _rotation = rotation;
    }

    public string DisplayId { get; }

    /// <summary>The display duplicated, or none when no card's output is that display.</summary>
    public static DuplicatedOutput? Create(string displayId)
    {
      var factoryId = typeof(IDXGIFactory1).GUID;
      PInvoke.CreateDXGIFactory1(&factoryId, out var created).ThrowOnFailure();
      var factory = (IDXGIFactory1)created;
      try
      {
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Succeeded; a++)
        {
          for (uint o = 0; adapter.EnumOutputs(o, out var output).Succeeded; o++)
          {
            var description = output.GetDesc();
            if (description.DeviceName.ToString() != displayId)
            {
              Marshal.ReleaseComObject(output);
              continue;
            }

            PInvoke.D3D11CreateDevice(
              adapter,
              D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_UNKNOWN,
              HMODULE.Null,
              0,
              null,
              0,
              PInvoke.D3D11_SDK_VERSION,
              out var device,
              null,
              out var context).ThrowOnFailure();
            ((IDXGIOutput1)output).DuplicateOutput(device, out var duplication);
            Marshal.ReleaseComObject(output);
            Marshal.ReleaseComObject(adapter);
            return new DuplicatedOutput(displayId, device, context, duplication, description.Rotation);
          }

          Marshal.ReleaseComObject(adapter);
        }

        return null;
      }
      finally
      {
        Marshal.ReleaseComObject(factory);
      }
    }

    /// <summary>The next frame, or none when nothing changed since the last one.</summary>
    /// <param name="drawCursor">
    /// Draw the pointer into it. The card doesn't: it is drawn over a clean copy of the screen, and when only the
    /// pointer moved, over the last one. The frames are compared then, as the pointer moves between them.
    /// </param>
    public CaptureResult? Capture(DisplayInfo display, bool fullFrame, bool drawCursor)
    {
      DXGI_OUTDUPL_FRAME_INFO info;
      IDXGIResource resource;
      try
      {
        _duplication.AcquireNextFrame(0, out info, out resource);
      }
      catch (COMException ex) when (ex.HResult == WaitTimeout)
      {
        return fullFrame ? null : CaptureResult.NoChange(Mode);
      }

      try
      {
        if (info.AccumulatedFrames == 0)
        {
          // Only the pointer moved: the screen is the same, but where the pointer is drawn isn't.
          if (drawCursor && info.LastMouseUpdateTime != 0 && _clean is { } clean)
          {
            return WithCursor(clean, display);
          }

          return fullFrame ? null : CaptureResult.NoChange(Mode);
        }

        var texture = (ID3D11Texture2D)resource;
        D3D11_TEXTURE2D_DESC description;
        texture.GetDesc(&description);
        var staging = StagingFor(description);
        _context.CopyResource(staging, texture);
        var frame = Copy(staging, (int)description.Width, (int)description.Height);
        var rotated = _rotation is DXGI_MODE_ROTATION.DXGI_MODE_ROTATION_ROTATE90 or DXGI_MODE_ROTATION.DXGI_MODE_ROTATION_ROTATE180 or DXGI_MODE_ROTATION.DXGI_MODE_ROTATION_ROTATE270;
        if (rotated)
        {
          var turns = _rotation switch
          {
            DXGI_MODE_ROTATION.DXGI_MODE_ROTATION_ROTATE90 => 1,
            DXGI_MODE_ROTATION.DXGI_MODE_ROTATION_ROTATE180 => 2,
            _ => 3,
          };
          frame = frame.Rotated(turns, new byte[frame.Pixels.Length]);
        }

        if (drawCursor)
        {
          _clean = new Frame(frame.Width, frame.Height, (byte[])frame.Pixels.Clone());
          WindowsCursor.Draw(frame, display);
          return new CaptureResult(frame, null, Mode);
        }

        _clean = null;

        // What changed is in the card's upright frame: turned frames are compared instead.
        return new CaptureResult(frame, fullFrame || rotated ? null : Changes(info.TotalMetadataBufferSize), Mode);
      }
      finally
      {
        Marshal.ReleaseComObject(resource);
        _duplication.ReleaseFrame();
      }
    }

    public void Dispose()
    {
      if (_staging is not null)
      {
        Marshal.ReleaseComObject(_staging);
      }

      Marshal.ReleaseComObject(_duplication);
      Marshal.ReleaseComObject(_context);
      Marshal.ReleaseComObject(_device);
    }

    // What the card says it drew, and where it moved areas to: a dragged window shows up only as a move. None when it
    // says nothing, and the frames are compared instead.
    private Region[]? Changes(uint metadataBytes)
    {
      if (metadataBytes == 0)
      {
        return null;
      }

      var buffer = NativeMemory.Alloc(metadataBytes);
      try
      {
        var changes = new List<Region>();
        _duplication.GetFrameMoveRects(metadataBytes, (DXGI_OUTDUPL_MOVE_RECT*)buffer, out var used);
        var moves = new ReadOnlySpan<DXGI_OUTDUPL_MOVE_RECT>(buffer, (int)(used / (uint)sizeof(DXGI_OUTDUPL_MOVE_RECT)));
        foreach (var move in moves)
        {
          changes.Add(ToRegion(move.DestinationRect));
        }

        _duplication.GetFrameDirtyRects(metadataBytes, (RECT*)buffer, out used);
        foreach (var dirty in new ReadOnlySpan<RECT>(buffer, (int)(used / (uint)sizeof(RECT))))
        {
          changes.Add(ToRegion(dirty));
        }

        return [.. changes];
      }
      finally
      {
        NativeMemory.Free(buffer);
      }
    }

    private static Region ToRegion(RECT rect) => new(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);

    private ID3D11Texture2D StagingFor(D3D11_TEXTURE2D_DESC frame)
    {
      if (_staging is not null)
      {
        D3D11_TEXTURE2D_DESC current;
        _staging.GetDesc(&current);
        if (current.Width == frame.Width && current.Height == frame.Height)
        {
          return _staging;
        }

        Marshal.ReleaseComObject(_staging);
        _staging = null;
      }

      var description = new D3D11_TEXTURE2D_DESC
      {
        Width = frame.Width,
        Height = frame.Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        SampleDesc = new DXGI_SAMPLE_DESC { Count = 1 },
        Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
        CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
      };
      _device.CreateTexture2D(description, null, out var staging);
      _staging = staging;
      return staging;
    }

    // The clean screen with the pointer drawn on it.
    private CaptureResult WithCursor(Frame clean, DisplayInfo display)
    {
      var buffer = NextBuffer(clean.Pixels.Length);
      clean.Pixels.CopyTo(buffer, 0);
      var frame = new Frame(clean.Width, clean.Height, buffer);
      WindowsCursor.Draw(frame, display);
      return new CaptureResult(frame, null, Mode);
    }

    // Two buffers taken in turn, as GDI does: a frame stays as it was until the capture after next.
    private byte[] NextBuffer(int size)
    {
      var buffer = _buffers[_next];
      if (buffer?.Length != size)
      {
        buffer = new byte[size];
        _buffers[_next] = buffer;
      }

      _next = 1 - _next;
      return buffer;
    }

    private Frame Copy(ID3D11Texture2D staging, int width, int height)
    {
      var buffer = NextBuffer(width * height * Frame.BytesPerPixel);
      D3D11_MAPPED_SUBRESOURCE mapped;
      _context.Map(staging, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);
      try
      {
        var stride = width * Frame.BytesPerPixel;
        for (var y = 0; y < height; y++)
        {
          new ReadOnlySpan<byte>((byte*)mapped.pData + (y * mapped.RowPitch), stride).CopyTo(buffer.AsSpan(y * stride, stride));
        }
      }
      finally
      {
        _context.Unmap(staging, 0);
      }

      return new Frame(width, height, buffer);
    }
  }
}
