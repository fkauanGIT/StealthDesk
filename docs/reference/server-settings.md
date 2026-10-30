# Server settings

Every setting the server reads, by key.

## How to set them

A key with a section is written `Section:Key` on the command line (`--Accounts:EnablePublicRegistration=true`) and
nested in `appsettings.json`. In a container, set it as an environment variable with the `StealthDesk_` prefix and
`__` between section and key (`StealthDesk_Accounts__EnablePublicRegistration=true`), or as a Docker secret: with
`EnableDockerSecrets` on, each file in `/run/secrets` is a setting, named the same way without the prefix
(`Accounts__GitHubClientSecret`). A secret wins over a variable, which wins over the files.

The server checks every setting when it starts and stops with a message naming the key of any invalid one. Outside
the Development environment it also refuses the in-memory database and the development PostgreSQL password.

## Database

| Key | Default | Meaning |
|---|---|---|
| `UseInMemoryDatabase` | `false` | An in-memory database instead of PostgreSQL, lost when the server stops. Development and tests only. |
| `InMemoryDatabaseName` | random | Servers in one process with the same name share the in-memory data. |
| `POSTGRES_HOST` | `localhost` in development | PostgreSQL's address. Required. |
| `POSTGRES_PORT` | `5432` | |
| `POSTGRES_DB` | `stealthdesk` | Created if missing; migrations run on every start. |
| `POSTGRES_USER`, `POSTGRES_PASSWORD` | `postgres`, `password` in development | Required. |
| `ApplyMigrationsOnStart` | `true` | Brings the database schema up to date on every start. Turned off, the server only checks it is up to date and refuses to start otherwise. |
| `EnableDockerSecrets` | `false` | Also reads settings from files in `DockerSecretsDirectory`, which must exist. |
| `DockerSecretsDirectory` | `/run/secrets` | Where Docker mounts the secrets. |

## Accounts

See [Accounts and sign-in](../guides/accounts.md).

| Key | Default | Meaning |
|---|---|---|
| `Accounts:EnableBearerLogin` | `false` | Lets scripts sign in for bearer tokens. Browsers always use a cookie. |
| `Accounts:BearerTokenLifetime` | `01:00:00` | How long a bearer token lasts. |
| `Accounts:RefreshTokenLifetime` | `30.00:00:00` | How long a refresh token lasts. |
| `Accounts:RequireUniqueEmail` | `true` | When false, several accounts may share an email. |
| `Accounts:EnablePublicRegistration` | `false` | Lets anyone register at any time; each registration gets its own tenant. |
| `Accounts:DisableFirstUserSelfRegistration` | `false` | Closes the registration a new server allows for its first user. |
| `Accounts:RequireConfirmedEmail` | `false` | Users must confirm their email before signing in. Needs email sending. |
| `Accounts:PersistPasskeySignIn` | `false` | A passkey sign-in keeps the session after the browser closes, like "Remember me". |
| `Accounts:DefaultTheme` | `Auto` | How the sign-in pages look: `Auto`, `Light` or `Dark`. Each user then picks their own. |
| `Accounts:MicrosoftClientId`, `Accounts:MicrosoftClientSecret` | empty | Sign in with a Microsoft account. On only when both are set. |
| `Accounts:GitHubClientId`, `Accounts:GitHubClientSecret` | empty | Sign in with GitHub. On only when both are set. |

## First administrator

Created on the first start only, while the server has no users.

| Key | Default | Meaning |
|---|---|---|
| `Bootstrap:AdminEmail`, `Bootstrap:AdminPassword` | empty | The first administrator, created on a start while the server has no users, like the first registration. Both or neither. |
| `Bootstrap:AdminTokenId`, `Bootstrap:AdminTokenSecret` | empty | A personal access token for that administrator, `{id}:{secret}`, acting as them and never expiring. The secret needs 32 characters or more. |
| `Bootstrap:ServerServiceAccountName`, `Bootstrap:ServerServiceAccountCredentialId`, `Bootstrap:ServerServiceAccountCredentialSecret` | empty | An unrestricted server service account and its key, `{credential id}:{secret}`, created while none has that name. All three or none; the secret needs 32 characters or more. |
| `Bootstrap:ServerServiceAccountId`, `Bootstrap:ServerServiceAccountDescription` | new id, empty | Its fixed id and description. |

## Email

See [Email](../guides/email.md).

| Key | Default | Meaning |
|---|---|---|
| `Email:DisableSending` | `false` (`true` in development) | Sends nothing. In development the messages, links included, are written to the server log. |
| `Email:SmtpHost`, `Email:SmtpPort` | empty, `587` | The SMTP server. Port 465 uses TLS from the start; others use STARTTLS when offered. |
| `Email:SmtpUserName`, `Email:SmtpPassword` | empty | Credentials, when the server needs them. |
| `Email:SmtpLocalDomain` | empty | The name the server introduces itself with; some relays require a real domain. |
| `Email:SmtpCheckCertificateRevocation` | `true` | Checks whether the SMTP server's certificate was revoked. |
| `Email:SenderName`, `Email:SenderAddress` | `StealthDesk`, empty | Who the messages come from. |

## Agents and tenants

See [Tenant settings](../guides/tenant-settings.md) and [Installing agents](../guides/installing-agents.md).

| Key | Default | Meaning |
|---|---|---|
| `TenantDefaults:AppendInstanceName` | `false` | Install commands name an agent instance (`--instance`), so agents of several servers can share a machine. |
| `TenantDefaults:InstanceName` | empty | The instance they name; empty makes one from the server's address (`desk.example.com` becomes `desk-example-com`). |
| `Gateway:ClockTolerance` | `00:01:00` | How far an agent's clock may be from the server's before its reports are refused as replays. Empty turns the check off. |
| `Gateway:AgentCallTimeout` | `00:00:30` | How long the server waits for an agent to answer before telling the user the device didn't respond. |
| `Relay:TicketLifetime` | `00:01:00` | How long a remote control session's tokens can be used before either end connects to the relay. |
| `Relay:PartnerTimeout` | `00:00:20` | How long the first end at the relay waits for the other before the session is given up. |
| `Relay:MaxMessageBytes` | `16777216` | The largest message an end may send through the relay; a larger one ends the session. |
| `InstallerKeys:UsageHistoryDays` | `90` | How long each key's installs are kept. Zero keeps them all. |
| `AgentDownloads:Folder` | `downloads` | Where the agent builds the server hands out are, relative to the server's folder unless absolute; the Windows agent is `windows/StealthDesk.Agent.zip` in it, with its desktop client. The container image ships one. |

## Logs and service accounts

| Key | Default | Meaning |
|---|---|---|
| `AuthorizationLogs:RetentionDays` | `365` | How long authorization log entries are kept. Zero keeps them all. |
| `ServiceAccounts:AuthFailureLimit`, `ServiceAccounts:AuthFailureWindowMinutes` | `5`, `5` | Failed service account sign-ins allowed per address and per credential before more are refused for that many minutes. |
| `ServiceAccounts:CredentialCleanupAfterDays` | `30` | Revoked and expired credentials are deleted this long after they stopped working. Zero keeps them. |

## Reverse proxy

See [Behind a reverse proxy](../guides/reverse-proxy.md).

| Key | Default | Meaning |
|---|---|---|
| `ReverseProxy:TrustedProxies` | empty | Addresses of proxies whose forwarded headers are trusted, e.g. `10.0.0.2`. A list: `--ReverseProxy:TrustedProxies:0=10.0.0.2`, or `StealthDesk_ReverseProxy__TrustedProxies__0`. |
| `ReverseProxy:TrustedNetworks` | empty | Networks of trusted proxies, e.g. `10.0.0.0/8`. A list, like the one above. |
| `ReverseProxy:DockerGatewayAddress` | empty | The gateway of the Docker network the server runs in, which a proxy in front of Docker comes through. |
| `ReverseProxy:TrustCloudflare` | `false` | Trusts Cloudflare's address ranges, downloaded from Cloudflare when the server starts; it doesn't start if they can't be. |
| `ReverseProxy:TrustAllProxies` | `false` | Trusts forwarded headers from any address. Only for a server nothing but the proxy can reach: otherwise any client can claim any address. |

## Data protection keys

See [Encrypting the data protection keys](../guides/encrypting-keys.md).

| Key | Default | Meaning |
|---|---|---|
| `KeyProtection:EncryptKeys` | `false` | Encrypts the data protection keys with `KeyProtection:Certificate`. |
| `KeyProtection:Certificate:Path`, `KeyProtection:Certificate:Base64`, `KeyProtection:Certificate:Password` | empty | The `.pfx` certificate, with an RSA private key, from a file or its base64 contents (which wins), and its password. |
| `KeyProtection:PreviousCertificates` | empty | Certificates that only read keys encrypted before a rotation. A list, each with `Path` or `Base64` and `Password`: `KeyProtection:PreviousCertificates:0:Path`. |

## Telemetry

| Key | Default | Meaning |
|---|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | empty | Sends logs, traces and metrics to this OpenTelemetry collector. |
