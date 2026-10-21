using System.CommandLine;

namespace StealthDesk.Agent.Tests;

/// <summary>The agent's command line: the server address it takes, and what it says about one it can't use.</summary>
public class AgentCommandLineTests
{
  [Theory]
  [InlineData("--server", "http://localhost:8080")]
  [InlineData("-s", "https://desk.example.com/")]
  public void AServerAddress_IsTaken(string option, string address)
  {
    var parse = Parse("run", option, address);

    Assert.Empty(parse.Errors);
    Assert.Equal(new Uri(address), parse.GetValue<Uri?>("--server"));
  }

  [Fact]
  public void WithoutAServerAddress_TheConfiguredOneIsUsed()
  {
    var parse = Parse("run");

    Assert.Empty(parse.Errors);
    Assert.Null(parse.GetValue<Uri?>("--server"));
  }

  [Theory]
  [InlineData("not-a-url")]
  [InlineData("localhost:8080")]
  [InlineData("/hubs/agent")]
  [InlineData("ftp://desk.example.com")]
  public void AnAddressItCantUse_IsAnErrorNamingTheOption(string address)
  {
    var parse = Parse("run", "--server", address);

    var error = Assert.Single(parse.Errors);
    Assert.Contains("--server", error.Message, StringComparison.Ordinal);
    Assert.Contains(address, error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RunningWithAnAddressItCantUse_ExitsWithTheError_WithoutStartingTheAgent()
  {
    using var output = new StringWriter();
    using var error = new StringWriter();

    var exitCode = await Parse("run", "--server", "not-a-url")
      .InvokeAsync(new InvocationConfiguration { Output = output, Error = error }, TestContext.Current.CancellationToken);

    Assert.NotEqual(0, exitCode);
    Assert.Contains("--server must be an absolute http or https address", error.ToString(), StringComparison.Ordinal);
    Assert.DoesNotContain("Unhandled exception", error.ToString() + output, StringComparison.Ordinal);
  }

  [Fact]
  public void Install_TakesTheServerTheKeyAndAnInstance()
  {
    var parse = Parse("install", "--server", "https://desk.example.com", "--installer-key", "id:secret", "--instance", "office");

    Assert.Empty(parse.Errors);
    Assert.Equal(new Uri("https://desk.example.com"), parse.GetValue<Uri?>("--server"));
    Assert.Equal("id:secret", parse.GetValue<string?>("--installer-key"));
    Assert.Equal("office", parse.GetValue<string?>("--instance"));
  }

  [Theory]
  [InlineData("--server", "install needs --installer-key")]
  [InlineData("--installer-key", "install needs --server")]
  public void Install_NeedsBothTheServerAndTheKey(string given, string error)
  {
    var parse = given == "--server"
      ? Parse("install", "--server", "http://localhost:8080")
      : Parse("install", "--installer-key", "id:secret");

    Assert.Contains(parse.Errors, x => x.Message.StartsWith(error, StringComparison.Ordinal));
  }

  [Fact]
  public void Install_RefusesABadInstanceName()
  {
    var parse = Parse("install", "--server", "http://localhost:8080", "--installer-key", "id:secret", "--instance", "not valid!");

    Assert.NotEmpty(parse.Errors);
  }

  [Fact]
  public void Uninstall_TakesOnlyTheInstance()
  {
    Assert.Empty(Parse("uninstall").Errors);
    Assert.Empty(Parse("uninstall", "--instance", "office").Errors);
    Assert.NotEmpty(Parse("uninstall", "--installer-key", "id:secret").Errors);
  }

  private static ParseResult Parse(params string[] args) => AgentCommandLine.Create(args).Parse(args);
}
