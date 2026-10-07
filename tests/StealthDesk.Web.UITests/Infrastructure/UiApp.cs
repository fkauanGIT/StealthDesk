using System.Text.RegularExpressions;

namespace StealthDesk.Web.UITests.Infrastructure;

/// <summary>
/// A test's server and browser page. On Windows the installed Edge runs the tests, elsewhere Playwright's own
/// Chromium; STEALTHDESK_UI_BROWSER chooses another channel (e.g. "chrome") and STEALTHDESK_UI_HEADED=1 shows it.
/// </summary>
public sealed partial class UiApp : IAsyncDisposable
{
  private static readonly SemaphoreSlim InstallGate = new(1, 1);
  private static bool _installed;

  private readonly string _name;
  private readonly IPlaywright _playwright;
  private readonly IBrowser _browser;

  private UiApp(string name, UiServer server, IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
  {
    _name = name;
    Server = server;
    _playwright = playwright;
    _browser = browser;
    Context = context;
    Page = page;
  }

  public UiServer Server { get; }

  public IBrowserContext Context { get; }

  public IPage Page { get; }

  public static async Task<UiApp> StartAsync(params (string Key, string Value)[] settings)
  {
    var name = SafeName().Replace(TestContext.Current.Test?.TestDisplayName ?? "test", "_");
    var channel = Environment.GetEnvironmentVariable("STEALTHDESK_UI_BROWSER") is { Length: > 0 } chosen
      ? chosen
      : OperatingSystem.IsWindows() ? "msedge" : null;
    if (channel is null)
    {
      await InstallChromiumAsync();
    }

    var server = await UiServer.StartAsync(name, settings);
    var playwright = await Playwright.CreateAsync();
    var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
    {
      Channel = channel,
      Headless = Environment.GetEnvironmentVariable("STEALTHDESK_UI_HEADED") != "1",
    });
    var context = await browser.NewContextAsync(new BrowserNewContextOptions
    {
      BaseURL = server.BaseAddress.ToString(),
      ViewportSize = new ViewportSize { Width = 1440, Height = 900 },
      Locale = "en-US",
    });
    context.SetDefaultTimeout(30_000);
    SetDefaultExpectTimeout(30_000);
    var page = await context.NewPageAsync();
    return new UiApp(name, server, playwright, browser, context, page);
  }

  /// <summary>Opens a page of the web client, e.g. "account/sign-in".</summary>
  public Task GoAsync(string path) => Page.GotoAsync(path);

  /// <summary>Types into the field with that label, then leaves it, so the page reads the value as it would from a user.</summary>
  public async Task FillAsync(string label, string value)
  {
    var field = Page.GetByLabel(label, new PageGetByLabelOptions { Exact = true });
    await field.FillAsync(value);
    await field.DispatchEventAsync("change");
  }

  public Task ClickAsync(string button) =>
    Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = button, Exact = true }).ClickAsync();

  public Task ClickLinkAsync(string link) =>
    Page.GetByRole(AriaRole.Link, new PageGetByRoleOptions { Name = link, Exact = true }).ClickAsync();

  public ILocator Heading(string text) =>
    Page.GetByRole(AriaRole.Heading, new PageGetByRoleOptions { Name = text, Exact = true });

  public ILocator Alert => Page.Locator(".sd-alert").First;

  /// <summary>The error shown under the field with that label.</summary>
  public ILocator ErrorOf(string label) =>
    Page.Locator(".sd-field")
      .Filter(new LocatorFilterOptions { Has = Page.GetByLabel(label, new PageGetByLabelOptions { Exact = true }) })
      .Locator(".sd-field-error");

  /// <summary>The path and query the page is on, e.g. "/account/sign-in?returnUrl=%2F".</summary>
  public string Location => new Uri(Page.Url).PathAndQuery;

  public Task ScreenshotAsync(string step) =>
    Page.ScreenshotAsync(new PageScreenshotOptions { Path = Path.Combine(UiPaths.Results, $"{_name}.{step}.png"), FullPage = true });

  /// <summary>Registers the first user, who becomes the administrator and lands on the devices.</summary>
  public async Task RegisterAsync(string email, string password)
  {
    await GoAsync("account/register");
    await FillAsync("Email", email);
    await FillAsync("Password", password);
    await FillAsync("Confirm password", password);
    await ClickAsync("Create account");
    await Expect(Heading("Devices")).ToBeVisibleAsync();
  }

  public async Task SignInAsync(string email, string password)
  {
    await FillAsync("Email", email);
    await FillAsync("Password", password);
    await ClickAsync("Sign in");
  }

  public async Task SignOutAsync()
  {
    await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Sign out" }).ClickAsync();
    await Expect(Heading("Sign in to StealthDesk")).ToBeVisibleAsync();
  }

  // The last screen is kept: when a test fails, this is what it saw.
  public async ValueTask DisposeAsync()
  {
    try
    {
      await ScreenshotAsync("end");
    }
    catch (PlaywrightException)
    {
      // The page is already gone.
    }

    await Context.DisposeAsync();
    await _browser.DisposeAsync();
    _playwright.Dispose();
    await Server.DisposeAsync();
  }

  private static async Task InstallChromiumAsync()
  {
    await InstallGate.WaitAsync();
    try
    {
      if (!_installed && Microsoft.Playwright.Program.Main(["install", "chromium"]) != 0)
      {
        throw new InvalidOperationException("Playwright couldn't install Chromium.");
      }

      _installed = true;
    }
    finally
    {
      InstallGate.Release();
    }
  }

  [GeneratedRegex("[^A-Za-z0-9_.-]+")]
  private static partial Regex SafeName();
}
