using System.Reflection;

namespace StealthDesk.Libraries.Branding.Tests;

public class BrandingConstantsTests
{
  // Properties that intentionally don't follow the brand.
  private static readonly HashSet<string> _notDerived =
  [
    nameof(BrandingConstants.AuthenticatorIssuerName),
    nameof(BrandingConstants.DesktopClientDirectoryName),
    nameof(BrandingConstants.WebServerAssemblyName),
  ];

  public static TheoryData<string> ColorConstantNames()
  {
    var names = typeof(BrandingConstants)
      .GetFields(BindingFlags.Public | BindingFlags.Static)
      .Where(x => x.IsLiteral && x.Name.Contains("Color"))
      .Select(x => x.Name);

    return [.. names];
  }

  public static TheoryData<string> DerivedPropertyNames()
  {
    var names = typeof(BrandingConstants)
      .GetProperties(BindingFlags.Public | BindingFlags.Static)
      .Where(x => x.PropertyType == typeof(string))
      .Select(x => x.Name)
      .Where(x => !_notDerived.Contains(x));

    return [.. names];
  }

  [Fact]
  public void AuthenticatorIssuerName_EqualsBrandName()
  {
    Assert.Equal(BrandingConstants.BrandName, BrandingConstants.AuthenticatorIssuerName);
  }

  [Fact]
  public void BrandKey_HasOnlyLettersDigitsAndUnderscores()
  {
    Assert.Matches("^[a-zA-Z0-9_]+$", BrandingConstants.BrandKey);
  }

  [Fact]
  public void BrandName_IsStealthDesk()
  {
    Assert.Equal("StealthDesk", BrandingConstants.BrandName);
  }

  [Theory]
  [MemberData(nameof(ColorConstantNames))]
  public void ColorConstant_IsHexWithoutHash(string constantName)
  {
    var value = (string)typeof(BrandingConstants).GetField(constantName)!.GetValue(null)!;

    Assert.Matches("^[0-9a-fA-F]{6}$", value);
  }

  [Theory]
  [MemberData(nameof(DerivedPropertyNames))]
  public void DerivedProperty_ContainsBrandKey(string propertyName)
  {
    var value = (string)typeof(BrandingConstants).GetProperty(propertyName)!.GetValue(null)!;

    Assert.Contains(BrandingConstants.BrandKey, value, StringComparison.OrdinalIgnoreCase);
  }

  // Flags unintended changes to names that installed agents depend on.
  [Theory]
  [InlineData(nameof(BrandingConstants.AgentBaseName), "StealthDesk.Agent")]
  [InlineData(nameof(BrandingConstants.BundleHashFileName), ".stealthdesk-bundle.sha256")]
  [InlineData(nameof(BrandingConstants.IpcPipeBaseName), "stealthdesk-ipc-server")]
  [InlineData(nameof(BrandingConstants.LinuxAgentServiceName), "stealthdesk.agent.service")]
  [InlineData(nameof(BrandingConstants.MacServicePrefix), "app.stealthdesk")]
  [InlineData(nameof(BrandingConstants.UnixHiddenDirectoryName), ".stealthdesk")]
  [InlineData(nameof(BrandingConstants.UpdaterTempDirectoryName), "StealthDesk_Update")]
  [InlineData(nameof(BrandingConstants.WebServerAssemblyName), "StealthDesk.Web.Server")]
  [InlineData(nameof(BrandingConstants.WindowsInstallDirectoryName), "StealthDesk")]
  [InlineData(nameof(BrandingConstants.WindowsServiceBaseName), "StealthDesk.Agent")]
  public void Property_HasExpectedValue(string propertyName, string expected)
  {
    var value = typeof(BrandingConstants).GetProperty(propertyName)!.GetValue(null);

    Assert.Equal(expected, value);
  }
}
