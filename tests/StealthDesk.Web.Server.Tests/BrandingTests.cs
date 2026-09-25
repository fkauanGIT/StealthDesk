using StealthDesk.Libraries.Branding;

namespace StealthDesk.Web.Server.Tests;

public class BrandingTests
{
  [Fact]
  public void WebServerAssemblyName_MatchesTheServerAssembly()
  {
    var assemblyName = typeof(Program).Assembly.GetName().Name;

    Assert.Equal(assemblyName, BrandingConstants.WebServerAssemblyName);
  }
}
