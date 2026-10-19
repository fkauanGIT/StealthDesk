namespace StealthDesk.Web.Server.Configuration;

/// <summary>
/// Where settings come from besides the appsettings files, user secrets and plain environment variables: variables
/// prefixed <c>StealthDesk_</c> (<c>StealthDesk_Accounts__EnablePublicRegistration</c> sets
/// <c>Accounts:EnablePublicRegistration</c>), and, when turned on, Docker secrets: one file per setting, named the same
/// way (<c>Accounts__GitHubClientSecret</c>). Each source added later wins, so a secret beats a variable.
/// </summary>
public static class ConfigurationSources
{
  public const string EnvironmentPrefix = "StealthDesk_";
  public const string EnableDockerSecrets = "EnableDockerSecrets";
  public const string DockerSecretsDirectory = "DockerSecretsDirectory";
  public const string DefaultDockerSecretsDirectory = "/run/secrets";

  public static WebApplicationBuilder AddStealthDeskConfiguration(this WebApplicationBuilder builder)
  {
    builder.Configuration.AddEnvironmentVariables(EnvironmentPrefix);

    // Read after the variables, so a container can turn secrets on with one.
    if (builder.Configuration.GetValue<bool>(EnableDockerSecrets))
    {
      var directory = builder.Configuration[DockerSecretsDirectory] is { Length: > 0 } configured ? configured : DefaultDockerSecretsDirectory;
      if (!Directory.Exists(directory))
      {
        throw new InvalidOperationException(
          $"{EnableDockerSecrets} is on, but the secrets directory '{directory}' doesn't exist. Mount the secrets there, or set {DockerSecretsDirectory}.");
      }

      builder.Configuration.AddKeyPerFile(directory, optional: false);
    }

    return builder;
  }
}
