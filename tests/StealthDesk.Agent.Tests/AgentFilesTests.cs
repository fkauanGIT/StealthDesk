using StealthDesk.Agent.Windows.Service;

namespace StealthDesk.Agent.Tests;

/// <summary>What install copies: a regular build's folder, or a single-file agent and nothing that sits next to it.</summary>
public sealed class AgentFilesTests : IDisposable
{
  private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"sd-files-{Guid.NewGuid():N}")).FullName;

  public void Dispose() => Directory.Delete(_root, recursive: true);

  [Fact]
  public void ARegularBuild_IsItsWholeFolder()
  {
    var folder = Folder("build", "StealthDesk.Agent.exe", "StealthDesk.Agent.dll", "StealthDesk.Core.dll", Path.Combine("runtimes", "win", "native.dll"));

    var files = AgentFiles.Of(Path.Combine(folder, "StealthDesk.Agent.exe"));

    Assert.Equal(
      new[] { "StealthDesk.Agent.dll", "StealthDesk.Agent.exe", "StealthDesk.Core.dll", Path.Combine("runtimes", "win", "native.dll") }.Order(),
      files.Order());
  }

  [Fact]
  public void ASingleFileAgent_IsOnlyItself_WhateverIsAroundIt()
  {
    var downloads = Folder("temp", "StealthDesk.Agent.exe", "someone-elses.dll", "report.docx", Path.Combine("other", "app.exe"));

    var files = AgentFiles.Of(Path.Combine(downloads, "StealthDesk.Agent.exe"));

    Assert.Equal(["StealthDesk.Agent.exe"], files);
  }

  [Fact]
  public void Copying_ASingleFileAgent_LeavesItsNeighboursBehind()
  {
    var downloads = Folder("temp", "StealthDesk.Agent.exe", "someone-elses.dll");
    var destination = Path.Combine(_root, "installed");

    AgentFiles.Copy(Path.Combine(downloads, "StealthDesk.Agent.exe"), destination);

    Assert.Equal(["StealthDesk.Agent.exe"], Directory.GetFiles(destination).Select(Path.GetFileName));
    Assert.Equal("StealthDesk.Agent.exe contents", File.ReadAllText(Path.Combine(destination, "StealthDesk.Agent.exe")));
  }

  private string Folder(string name, params string[] files)
  {
    var folder = Path.Combine(_root, name);
    foreach (var file in files)
    {
      var path = Path.Combine(folder, file);
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, $"{Path.GetFileName(file)} contents");
    }

    return folder;
  }
}
