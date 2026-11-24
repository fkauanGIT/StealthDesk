using System.Net;
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using StealthDesk.Contracts.Devices;
using StealthDesk.Contracts.Messaging;
using StealthDesk.Web.Client.Devices;
using StealthDesk.Web.Client.Pages;

namespace StealthDesk.Web.Client.Tests;

public class FileManagerPageTests : BunitContext
{
  private readonly FakeApi _api = new();
  private readonly FakeDeviceHub _hub = new();
  private readonly FakeLiveUpdates _live = new();
  private readonly Guid _deviceId = Guid.NewGuid();

  public FileManagerPageTests()
  {
    Services.AddSingleton(_api.CreateClient());
    Services.AddSingleton<DeviceStore>();
    Services.AddSingleton<ILiveUpdates>(_live);
    Services.AddSingleton<IDeviceHub>(_hub);
    AddAuthorization();
    _live.Become(LiveState.Live);
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
  public void TheUploadControl_ShowsOnlyWithThePermission()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, BrowseFiles: true, UploadFiles: false));
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true, []));

    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));

    page.WaitForAssertion(() => Assert.Contains("Empty", page.Markup));
    Assert.Empty(page.FindAll("input[type=file]"));
  }

  [Fact]
  public void SelectingAFile_UploadsItToTheCurrentFolder_AndReloadsTheListing()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, BrowseFiles: true, UploadFiles: true));
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true, []));

    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));
    page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("input[type=file]")));

    _api.Respond(HttpStatusCode.OK);
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true,
      [new FileSystemEntry("note.txt", IsDirectory: false, SizeBytes: 5, DateTimeOffset.UtcNow)]));

    page.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("hello", "note.txt"));

    page.WaitForAssertion(() => Assert.Contains("note.txt", page.Markup));
    Assert.Contains(_api.Requests, x => x.StartsWith("POST", StringComparison.Ordinal) && x.Contains("/files/upload"));
  }

  [Fact]
  public void TheNewFolderButton_ShowsOnlyWithTheUploadPermission()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, BrowseFiles: true, UploadFiles: false));
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true, []));

    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));

    page.WaitForAssertion(() => Assert.Contains("Empty", page.Markup));
    Assert.DoesNotContain("New folder", page.Markup);
  }

  [Fact]
  public void CreatingANewFolder_PostsItsName_AndReloadsTheListing()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, BrowseFiles: true, UploadFiles: true));
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true, []));

    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));
    page.WaitForAssertion(() => Assert.Contains("New folder", page.Markup));
    page.FindAll("button").Single(x => x.TextContent.Trim() == "New folder").Click();

    page.Find("#new-folder-name").Input("Reports");
    _api.Respond(HttpStatusCode.OK);
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true,
      [new FileSystemEntry("Reports", IsDirectory: true, SizeBytes: 0, DateTimeOffset.UtcNow)]));
    page.FindAll("button").Single(x => x.TextContent.Trim() == "Create").Click();

    page.WaitForAssertion(() => Assert.Contains("Reports", page.Markup));
    Assert.Contains(_api.Requests, x =>
      x.StartsWith("POST", StringComparison.Ordinal) && x.Contains("/files/directories") && x.Contains(Uri.EscapeDataString("Reports")));
  }

  [Fact]
  public void TheDeleteButton_ShowsOnlyWithTheUploadPermission()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, BrowseFiles: true, UploadFiles: false));
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true,
      [new FileSystemEntry("notes.txt", IsDirectory: false, SizeBytes: 5, DateTimeOffset.UtcNow)]));

    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));

    page.WaitForAssertion(() => Assert.Contains("notes.txt", page.Markup));
    Assert.DoesNotContain("Delete", page.Markup);
  }

  [Fact]
  public void DeletingAnEntry_AsksForConfirmation_AndOnlyDeletesWhenConfirmed()
  {
    _hub.Actions = GatewayReply.Accept(new DeviceActions(UseTerminal: false, BrowseFiles: true, UploadFiles: true));
    _api.RespondWith(Device());
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true,
      [new FileSystemEntry("notes.txt", IsDirectory: false, SizeBytes: 5, DateTimeOffset.UtcNow)]));

    var page = Render<FileManagerPage>(x => x.Add(p => p.Id, _deviceId));
    page.WaitForAssertion(() => Assert.Contains("notes.txt", page.Markup));
    page.FindAll("button").Single(x => x.TextContent.Trim() == "Delete").Click();

    page.WaitForAssertion(() => Assert.Contains("Delete this?", page.Markup));
    page.Find(".sd-dialog").QuerySelectorAll("button").Single(x => x.TextContent.Trim() == "Cancel").Click();
    page.WaitForAssertion(() => Assert.DoesNotContain("Delete this?", page.Markup));
    Assert.DoesNotContain(_api.Requests, x => x.StartsWith("DELETE", StringComparison.Ordinal));

    page.FindAll("button").Single(x => x.TextContent.Trim() == "Delete").Click();
    page.WaitForAssertion(() => Assert.Contains("Delete this?", page.Markup));
    _api.Respond(HttpStatusCode.OK);
    _api.RespondWith(new DirectoryListing(@"C:\", Exists: true, []));
    page.Find(".sd-dialog").QuerySelectorAll("button").Single(x => x.TextContent.Trim() == "Delete").Click();

    page.WaitForAssertion(() => Assert.Contains("Empty", page.Markup));
    Assert.Contains(_api.Requests, x =>
      x.StartsWith("DELETE", StringComparison.Ordinal) && x.Contains(Uri.EscapeDataString(@"C:\notes.txt")));
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
