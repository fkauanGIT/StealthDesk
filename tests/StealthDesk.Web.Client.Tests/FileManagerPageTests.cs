using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Devices;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Pages;

namespace StealthDesk.Web.Client.Tests;

public class FileManagerPageTests : BunitContext
{
  private readonly FakeApi _api = new();
  private readonly Guid _deviceId = Guid.NewGuid();

  public FileManagerPageTests()
  {
    Services.AddSingleton(_api.CreateClient());
    Services.AddSingleton<DeviceStore>();
    AddAuthorization();
  }

  [Fact]
  public void OpenedDirectly_ListsTheRootsEntries()
  {
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true,
    [
      new FileSystemEntry("Users", IsDirectory: true, SizeBytes: 0, DateTimeOffset.UtcNow),
      new FileSystemEntry("notes.txt", IsDirectory: false, SizeBytes: 2048, DateTimeOffset.UtcNow),
    ]));

    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));

    page.WaitForAssertion(() => Assert.Contains("Users", page.Markup));
    Assert.Contains("notes.txt", page.Markup);
    Assert.Contains("2 KB", page.Markup);
  }

  [Fact]
  public void ClickingAFolder_LoadsItsContents()
  {
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true,
      [new FileSystemEntry("Users", IsDirectory: true, SizeBytes: 0, DateTimeOffset.UtcNow)]));
    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));
    page.WaitForAssertion(() => Assert.Contains("Users", page.Markup));

    _api.RespondWith(new DirectoryListing(@"C:\Users", Exists: true,
      [new FileSystemEntry("fkauan", IsDirectory: true, SizeBytes: 0, DateTimeOffset.UtcNow)]));
    page.FindAll("button").Single(x => x.TextContent.Trim() == "Users").Click();

    page.WaitForAssertion(() => Assert.Contains("fkauan", page.Markup));
    Assert.Contains(_api.Requests, x => x.Contains(Uri.EscapeDataString(@"C:\Users")));
  }

  [Fact]
  public void AFile_HasAPlainDownloadLink_AFolder_HasAZipOne()
  {
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true,
    [
      new FileSystemEntry("Users", IsDirectory: true, SizeBytes: 0, DateTimeOffset.UtcNow),
      new FileSystemEntry("notes.txt", IsDirectory: false, SizeBytes: 2048, DateTimeOffset.UtcNow),
    ]));

    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));

    page.WaitForAssertion(() => Assert.Contains("notes.txt", page.Markup));
    var links = page.FindAll("a").Where(x => x.GetAttribute("href")?.Contains("/files/download") == true).ToList();
    Assert.Equal(2, links.Count);

    var fileLink = links.Single(x => x.GetAttribute("href")!.Contains(Uri.EscapeDataString(@"C:\notes.txt")));
    Assert.Equal("Download", fileLink.TextContent.Trim());

    var folderLink = links.Single(x => x.GetAttribute("href")!.Contains(Uri.EscapeDataString(@"C:\Users")));
    Assert.Equal("Download as zip", folderLink.TextContent.Trim());
  }

  [Fact]
  public void AMissingFolder_SaysNotFound()
  {
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\gone", Exists: false, []));

    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));

    page.WaitForAssertion(() => Assert.Contains("Not found", page.Markup));
  }

  private DeviceSummary Device() => new()
  {
    Id = _deviceId,
    Name = "FRONT-DESK",
    IsOnline = true,
  };
}
