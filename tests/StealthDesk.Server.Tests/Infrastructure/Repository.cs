namespace StealthDesk.Server.Tests.Infrastructure;

/// <summary>The repository the tests run from, for files that ship next to the server rather than inside it.</summary>
public static class Repository
{
  public static string Root { get; } = Find();

  private static string Find()
  {
    for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
    {
      if (File.Exists(Path.Combine(directory.FullName, "StealthDesk.slnx")))
      {
        return directory.FullName;
      }
    }

    throw new InvalidOperationException("The tests must run inside the repository: StealthDesk.slnx wasn't found.");
  }
}
