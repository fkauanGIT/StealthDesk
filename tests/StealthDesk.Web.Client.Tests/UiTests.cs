using Microsoft.AspNetCore.Components;
using StealthDesk.Web.Client.Ui;

namespace StealthDesk.Web.Client.Tests;

/// <summary>The design system components, one state at a time.</summary>
public class UiTests : BunitContext
{
  [Theory]
  [InlineData(ButtonVariant.Primary, "sd-btn--primary")]
  [InlineData(ButtonVariant.Secondary, "sd-btn--secondary")]
  [InlineData(ButtonVariant.Ghost, "sd-btn--ghost")]
  [InlineData(ButtonVariant.Danger, "sd-btn--danger")]
  public void Button_HasTheClassOfItsVariant(ButtonVariant variant, string expected)
  {
    var button = Render<SdButton>(x => x.Add(p => p.Variant, variant).AddChildContent("Save"));

    Assert.Contains(expected, button.Find("button").ClassList);
    Assert.Equal("button", button.Find("button").GetAttribute("type"));
  }

  [Fact]
  public void Button_RaisesClicksUnlessDisabled()
  {
    var clicks = 0;
    var button = Render<SdButton>(x => x.Add(p => p.OnClick, () => clicks++).AddChildContent("Save"));

    button.Find("button").Click();
    button.Render(x => x.Add(p => p.Disabled, true));

    Assert.Equal(1, clicks);
    Assert.True(button.Find("button").HasAttribute("disabled"));
  }

  [Fact]
  public void IconOnlyButton_IsLabelledAndSquare()
  {
    var button = Render<SdButton>(x => x.Add(p => p.Icon, IconName.Refresh).Add(p => p.AriaLabel, "Refresh"));

    Assert.Equal("Refresh", button.Find("button").GetAttribute("aria-label"));
    Assert.Contains("sd-btn--icon", button.Find("button").ClassList);
    Assert.Equal("true", button.Find("svg").GetAttribute("aria-hidden"));
  }

  [Fact]
  public void TextField_LabelPointsAtItsInput()
  {
    var field = Render<SdTextField>(x => x.Add(p => p.Label, "Email").Add(p => p.Type, "email"));

    var input = field.Find("input");
    Assert.Equal(input.Id, field.Find("label").GetAttribute("for"));
    Assert.Equal("email", input.GetAttribute("type"));
    Assert.False(input.HasAttribute("aria-invalid"));
  }

  [Fact]
  public void TextField_WithAnError_IsInvalidAndDescribedByIt()
  {
    var field = Render<SdTextField>(x => x.Add(p => p.Label, "Password").Add(p => p.Error, "Use at least 8 characters."));

    var input = field.Find("input");
    Assert.Equal("true", input.GetAttribute("aria-invalid"));
    Assert.Equal("Use at least 8 characters.", field.Find($"#{input.GetAttribute("aria-describedby")}").TextContent);
  }

  [Fact]
  public void TextField_ReportsChanges()
  {
    string? value = null;
    var field = Render<SdTextField>(x => x.Add(p => p.Label, "Name").Add(p => p.ValueChanged, (string? v) => value = v));

    field.Find("input").Change("FRONT-DESK");

    Assert.Equal("FRONT-DESK", value);
  }

  [Fact]
  public void Checkbox_ReportsChanges()
  {
    var value = false;
    var box = Render<SdCheckbox>(x => x.Add(p => p.Label, "Remember me").Add(p => p.CheckedChanged, (bool v) => value = v));

    box.Find("input").Change(true);

    Assert.True(value);
    Assert.Contains("Remember me", box.Find("label").TextContent);
  }

  [Fact]
  public void Select_ShowsItsOptionsAndReportsChanges()
  {
    string? value = null;
    var select = Render<SdSelect>(x => x
      .Add(p => p.Label, "Theme")
      .Add(p => p.Options, [new SelectOption("dark", "Dark"), new SelectOption("light", "Light")])
      .Add(p => p.ValueChanged, (string? v) => value = v));

    select.Find("select").Change("light");

    Assert.Equal(["Dark", "Light"], select.FindAll("option").Select(x => x.TextContent));
    Assert.Equal("light", value);
  }

  [Theory]
  [InlineData(Tone.Online, "Online")]
  [InlineData(Tone.Offline, "Offline")]
  [InlineData(Tone.Warning, "Reconnecting...")]
  public void Status_AlwaysCarriesText(Tone tone, string text)
  {
    var status = Render<SdStatus>(x => x.Add(p => p.Tone, tone).Add(p => p.Text, text));

    Assert.Equal(text, status.Find("span").TextContent);
    Assert.Contains($"sd-status--{tone.ToString().ToLowerInvariant()}", status.Find("span").ClassList);
  }

  [Theory]
  [InlineData(Tone.Danger, "alert")]
  [InlineData(Tone.Warning, "alert")]
  [InlineData(Tone.Info, "status")]
  public void Alert_IsAnnouncedByTone(Tone tone, string role)
  {
    var alert = Render<SdAlert>(x => x.Add(p => p.Tone, tone).AddChildContent("Something happened."));

    Assert.Equal(role, alert.Find("div").GetAttribute("role"));
    Assert.Contains("Something happened.", alert.Markup);
  }

  [Theory]
  [InlineData(0.5, 50, null)]
  [InlineData(0.8, 80, "sd-meter--warning")]
  [InlineData(0.95, 95, "sd-meter--danger")]
  [InlineData(1.7, 100, "sd-meter--danger")]
  [InlineData(double.NaN, 0, null)]
  public void Meter_ShowsTheShareAndWarnsWhenNearlyFull(double value, int percent, string? level)
  {
    var meter = Render<SdMeter>(x => x.Add(p => p.Label, "Memory").Add(p => p.Value, value));

    Assert.Equal(percent.ToString(), meter.Find("[role=meter]").GetAttribute("aria-valuenow"));
    var classes = meter.Find(".sd-meter").ClassList;
    Assert.Equal(level is not null, classes.Any(x => x is "sd-meter--warning" or "sd-meter--danger"));
    if (level is not null)
    {
      Assert.Contains(level, classes);
    }
  }

  [Fact]
  public void EmptyState_ShowsTitleAndBody()
  {
    var empty = Render<SdEmptyState>(x => x.Add(p => p.Title, "No devices yet").AddChildContent("Run the agent."));

    Assert.Equal("No devices yet", empty.Find(".sd-empty-title").TextContent);
    Assert.Equal("Run the agent.", empty.Find(".sd-empty-body").TextContent);
  }

  [Fact]
  public void Loading_IsAStatusWithText()
  {
    var loading = Render<SdLoading>(x => x.Add(p => p.Text, "Loading devices..."));

    Assert.Equal("status", loading.Find("div").GetAttribute("role"));
    Assert.Contains("Loading devices...", loading.Markup);
  }

  [Fact]
  public void Card_ShowsItsTitle()
  {
    var card = Render<SdCard>(x => x.Add(p => p.Title, "Network").AddChildContent("<p>body</p>"));

    Assert.Equal("Network", card.Find("h2").TextContent);
    Assert.Equal("body", card.Find("p").TextContent);
  }

  [Fact]
  public void Dialog_RendersOnlyWhenOpen()
  {
    var dialog = Render<SdDialog>(x => x.Add(p => p.Title, "Delete device?").Add(p => p.Open, false));
    Assert.Empty(dialog.FindAll("[role=dialog]"));

    dialog.Render(x => x.Add(p => p.Open, true));

    var box = dialog.Find("[role=dialog]");
    Assert.Equal("true", box.GetAttribute("aria-modal"));
    Assert.Equal("Delete device?", dialog.Find($"#{box.GetAttribute("aria-labelledby")}").TextContent);
  }

  [Fact]
  public void Dialog_AsksToCloseOnEscapeAndBackdrop()
  {
    var closes = 0;
    var dialog = Render<SdDialog>(x => x
      .Add(p => p.Title, "Delete device?")
      .Add(p => p.Open, true)
      .Add(p => p.OnClose, () => closes++));

    dialog.Find("[role=dialog]").KeyDown("Escape");
    dialog.Find("[role=dialog]").KeyDown("Enter");
    dialog.Find(".sd-dialog-backdrop").Click();

    Assert.Equal(2, closes);
  }

  [Fact]
  public void Segmented_MarksTheChosenOptionAndReportsPicks()
  {
    var picked = 0;
    var segmented = Render<SdSegmented<int>>(x => x
      .Add(p => p.Label, "Show")
      .Add(p => p.Options, [(1, "All"), (2, "Online")])
      .Add(p => p.Value, 1)
      .Add(p => p.ValueChanged, (int v) => picked = v));

    var buttons = segmented.FindAll("button");
    Assert.Equal("true", buttons[0].GetAttribute("aria-pressed"));
    Assert.Equal("false", buttons[1].GetAttribute("aria-pressed"));

    buttons[1].Click();

    Assert.Equal(2, picked);
  }

  [Fact]
  public void Logo_IsDecorative()
  {
    var logo = Render<SdLogo>();

    Assert.Equal("true", logo.Find("svg").GetAttribute("aria-hidden"));
  }

  [Fact]
  public void Icons_DrawEveryName()
  {
    foreach (var name in Enum.GetValues<IconName>())
    {
      var icon = Render<SdIcon>(x => x.Add(p => p.Name, name));
      Assert.NotEmpty(icon.Find("svg").Children);
    }
  }
}
