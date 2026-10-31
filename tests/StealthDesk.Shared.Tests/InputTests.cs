using Microsoft.Extensions.Logging.Abstractions;
using StealthDesk.Contracts.RemoteControl;
using StealthDesk.RemoteControl.Input;

namespace StealthDesk.Shared.Tests;

public class KeyMapTests
{
  [Theory]
  [InlineData("KeyA", "a", VirtualKey.A)]
  [InlineData("KeyZ", "z", VirtualKey.Z)]
  [InlineData("Digit5", "5", VirtualKey.D5)]
  [InlineData("Numpad7", "7", VirtualKey.Numpad7)]
  [InlineData("F11", "F11", VirtualKey.F11)]
  [InlineData("Enter", "Enter", VirtualKey.Return)]
  [InlineData("ArrowLeft", "ArrowLeft", VirtualKey.Left)]
  [InlineData("Slash", "/", VirtualKey.Oem2)]
  public void TheCodeDecides_WhateverTheKey(string code, string key, VirtualKey expected)
  {
    Assert.Equal(expected, KeyMap.Resolve(key, code)?.Key);
  }

  [Fact]
  public void WithoutAKnownCode_TheKeyDecides()
  {
    Assert.Equal(VirtualKey.A, KeyMap.Resolve("a", code: "")?.Key);
    Assert.Equal(VirtualKey.A, KeyMap.Resolve("A", code: null)?.Key);
    Assert.Equal(VirtualKey.F5, KeyMap.Resolve("F5", "Unmapped")?.Key);
    Assert.Equal(VirtualKey.Space, KeyMap.Resolve(" ", "Unmapped")?.Key);
  }

  [Fact]
  public void AnUnknownKey_IsNotResolved()
  {
    Assert.Null(KeyMap.Resolve("Dead", "Unmapped"));
    Assert.Null(KeyMap.Resolve("", ""));
  }

  [Theory]
  [InlineData("NumpadEnter", "Enter", true)]
  [InlineData("Enter", "Enter", false)]
  [InlineData("ArrowUp", "ArrowUp", true)]
  [InlineData("ControlRight", "Control", true)]
  [InlineData("ControlLeft", "Control", false)]
  [InlineData("KeyA", "a", false)]
  public void TheExtendedKeys_AreMarked(string code, string key, bool extended)
  {
    Assert.Equal(extended, KeyMap.Resolve(key, code)?.Extended);
  }
}

public class PointerMathTests
{
  private static readonly DisplayInfo Secondary = new("\\\\.\\DISPLAY2", "Second", Left: 1920, Top: -200, Width: 1280, Height: 1024, IsPrimary: false);

  [Fact]
  public void TheFractionLands_OnTheDisplaysPixels()
  {
    Assert.Equal((1920, -200), PointerMath.ToPixel(Secondary, 0, 0));
    Assert.Equal((1920 + 1279, -200 + 1023), PointerMath.ToPixel(Secondary, 1, 1));
    Assert.Equal((1920 + 640, -200 + 512), PointerMath.ToPixel(Secondary, 0.5, 0.5));
  }

  [Fact]
  public void OutOfRange_LandsOnTheEdge()
  {
    Assert.Equal((1920, -200), PointerMath.ToPixel(Secondary, -3, -3));
    Assert.Equal((1920 + 1279, -200 + 1023), PointerMath.ToPixel(Secondary, 5, 5));
  }
}

public class RemoteInputTests
{
  private static readonly DisplayInfo Display = new("\\\\.\\DISPLAY1", "Main", Left: 0, Top: 0, Width: 1000, Height: 500, IsPrimary: true);

  private readonly RecordingInjector _injector = new();

  [Fact]
  public void AnInteractingViewer_MovesClicksAndTypes()
  {
    var input = For(ViewerPermissions.ViewOnly with { Interact = true });

    input.MovePointer(new MovePointer(0.5, 0.5));
    input.MouseButton(new MouseButton(Button: 0, IsPressed: true, X: 0, Y: 0));
    input.MouseWheel(new MouseWheel(0.5, 0.5, DeltaY: 120, DeltaX: 0));
    input.Key(new KeyEvent("Enter", "Enter", IsPressed: true, KeyModifiers.None));
    input.TypeText(new TypeText("Hi"));

    Assert.Equal("move 500,250", _injector.Log[0]);
    Assert.Equal("button 0,0 b0 down", _injector.Log[1]);
    Assert.Equal("wheel 500,250 y120 x0", _injector.Log[2]);
    Assert.Equal("key Return down ext=False", _injector.Log[3]);
    Assert.Equal(["char H down", "char H up", "char i down", "char i up"], _injector.Log[4..]);
  }

  [Fact]
  public void AViewOnlyViewer_InjectsNothing()
  {
    var input = For(ViewerPermissions.ViewOnly);

    input.MovePointer(new MovePointer(0.5, 0.5));
    input.MouseButton(new MouseButton(0, true, 0.5, 0.5));
    input.Key(new KeyEvent("a", "KeyA", true, KeyModifiers.None));
    input.TypeText(new TypeText("x"));
    input.ResetKeyboard();

    Assert.Empty(_injector.Log);
  }

  [Fact]
  public void APlainCharacter_IsTyped_ButAShortcutIsSentAsItsKey()
  {
    var input = For(ViewerPermissions.ViewOnly with { Interact = true });

    input.Key(new KeyEvent("a", "KeyA", IsPressed: true, KeyModifiers.None));
    input.Key(new KeyEvent("a", "KeyA", IsPressed: true, KeyModifiers.None with { Control = true }));

    Assert.Equal(["char a down", "key A down ext=False"], _injector.Log);
  }

  [Fact]
  public void CtrlAltDel_NeedsItsOwnPermission()
  {
    For(ViewerPermissions.ViewOnly with { Interact = true }).CtrlAltDel();
    Assert.Empty(_injector.Log);

    For(ViewerPermissions.ViewOnly with { SendCtrlAltDel = true }).CtrlAltDel();
    Assert.Equal(["ctrlaltdel"], _injector.Log);
  }

  [Fact]
  public void WithNoDisplayShownYet_MouseInputIsDropped()
  {
    var input = new RemoteInput(_injector, () => null, ViewerPermissions.ViewOnly with { Interact = true }, NullLogger.Instance);

    input.MovePointer(new MovePointer(0.5, 0.5));

    Assert.Empty(_injector.Log);
  }

  private RemoteInput For(ViewerPermissions permissions) =>
    new(_injector, () => Display, permissions, NullLogger.Instance);

  private sealed class RecordingInjector : IInputInjector
  {
    public List<string> Log { get; } = [];

    public void MovePointer(int x, int y) => Log.Add($"move {x},{y}");

    public void MouseButton(int x, int y, int button, bool isPressed) => Log.Add($"button {x},{y} b{button} {(isPressed ? "down" : "up")}");

    public void MouseWheel(int x, int y, int deltaY, int deltaX) => Log.Add($"wheel {x},{y} y{deltaY} x{deltaX}");

    public void Key(VirtualKey key, bool isPressed, bool extended) => Log.Add($"key {key} {(isPressed ? "down" : "up")} ext={extended}");

    public void TypeCharacter(char character, bool isPressed) => Log.Add($"char {character} {(isPressed ? "down" : "up")}");

    public void ResetKeys() => Log.Add("reset");

    public void CtrlAltDel() => Log.Add("ctrlaltdel");
  }
}
