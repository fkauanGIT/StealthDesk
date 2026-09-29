namespace StealthDesk.Server.Tests.Infrastructure;

internal static class Eventually
{
  /// <summary>Polls until <paramref name="condition"/> holds, for things the server does in the background.</summary>
  public static async Task<bool> TrueAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
  {
    var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
    while (DateTime.UtcNow < deadline)
    {
      if (await condition())
      {
        return true;
      }

      await Task.Delay(50, TestContext.Current.CancellationToken);
    }

    return false;
  }
}
