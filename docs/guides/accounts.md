# Accounts and sign-in

Account rules come from the `Accounts` [settings](../reference/server-settings.md#accounts).

## Who can register

A new server has no users and no tenants. The first person to register (`POST /api/auth/register`) gets a new
tenant and becomes the server administrator; after that, registration is closed unless public registration is on.
A server set up by automation can instead get its first administrator from the `Bootstrap` settings, with a
personal access token and a server service account whose keys the automation chooses in advance; they are created
on the first start only, while the server has no users.
Agents join a tenant with one of its installer keys (see [your first agent](../getting-started/first-agent.md)).

Passwords need at least 8 characters with an upper-case letter, a lower-case letter and a digit. Five wrong
passwords in a row lock the account for five minutes.

## Signing in with Microsoft or GitHub

Each provider needs an app registered with it, whose callback address points back to this server:

- **GitHub**: Settings → Developer settings → OAuth Apps → New OAuth App. Homepage `http://localhost:5099`,
  callback `http://localhost:5099/signin-github`. Copy the client ID and generate a client secret.
- **Microsoft**: Azure portal → App registrations → New registration, for personal and work accounts. Add a Web
  redirect URI `http://localhost:5099/signin-microsoft`, then create a client secret under Certificates & secrets.

Keep the secrets out of `appsettings.json`, e.g. with user secrets:

```powershell
dotnet user-secrets set "Accounts:GitHubClientId" "<client id>" --project StealthDesk.Web.Server
dotnet user-secrets set "Accounts:GitHubClientSecret" "<client secret>" --project StealthDesk.Web.Server
```

A first sign-in with a provider follows the registration rules above, creating an account without a password.
Providers can be linked and unlinked in the account settings, as long as the account keeps a way to sign in.
