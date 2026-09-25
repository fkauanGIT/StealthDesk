namespace StealthDesk.Libraries.Shared.Helpers;

public class NoopDisposable : IDisposable
{
  public void Dispose()
  {
    GC.SuppressFinalize(this);
  }
}
