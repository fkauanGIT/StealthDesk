using StealthDesk.Branding;

namespace StealthDesk.Shared.Tests;

public class BrandTests
{
  [Fact]
  public void Key_IsSafeForPathsAndServiceNames()
  {
    Assert.Matches("^[A-Za-z0-9_]+$", Brand.Key);
  }

  [Fact]
  public void DerivedNames_FollowTheBrand()
  {
    Assert.StartsWith(Brand.Key, Brand.DataFolderName);
    Assert.StartsWith(Brand.Key, Brand.AgentName);
  }
}
