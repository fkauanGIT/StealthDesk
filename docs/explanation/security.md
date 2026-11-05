# Security model

What protects a tenant's devices and data today, in one place, and what doesn't yet. The pieces are spread across
other pages; this one ties them together and says plainly where the gaps are.

## Transport

The server itself speaks plain HTTP: HTTPS comes from a reverse proxy in front of it, such as Caddy or Nginx, which
holds the certificate. See [Behind a reverse proxy](../guides/reverse-proxy.md). The server trusts a proxy's
`X-Forwarded-Proto` only from addresses listed in `ReverseProxy:TrustedProxies`, and a session cookie is marked
`Secure` only once that trust is in place — so a server exposed without a correctly configured proxy may hand out
cookies that aren't marked `Secure`.

## Signing in

A person signs in with a cookie session, or a bearer token if `Accounts:EnableBearerLogin` is on. Passwords are
hashed with ASP.NET Core Identity's own hasher, never StealthDesk's own code. Two-factor authentication is a 6-digit
TOTP code from an authenticator app, with 10 recovery codes; passkeys (WebAuthn) skip it, since a passkey already
proves possession and, with a PIN or biometric, who is using it. Microsoft and GitHub sign-in are also available.
Changing a password, resetting 2FA or unlinking a login changes the account's security stamp and signs out other
sessions. See [Accounts and sign-in](../guides/accounts.md).

The session cookie's lifetime, `HttpOnly` and `SameSite` follow ASP.NET Core's own defaults, unchanged by
StealthDesk: a 14-day sliding expiration, `HttpOnly`, `SameSite=Lax`.

## Personal access tokens and service account keys

Scripts use the API with a personal access token or a service account key instead of a person's password, each
`{id}:{64 random characters}` from a cryptographically secure generator. The secret is hashed the same way a
password is, and is returned once, at creation: it is never stored or logged anywhere in the clear. A service
account key is additionally cached as a SHA-256 digest in memory for 30 seconds, to spare rehashing it on every
call, but that cache is memory-only and never written to disk. Five failed attempts within five minutes, by IP
address or by the token or credential's id, are enough to start refusing more without checking them.

## Authorization

Every API route and dashboard hub method checks a permission, granted or denied at server, tenant, device group or
device scope; wherever it comes from, a deny always wins. A query for a tenant's own data is filtered to it
automatically by Entity Framework Core, from the signed-in user's own tenant claim — a few administrative queries
turn that filter off on purpose and rely on their own, explicit condition instead, such as a personal access
token's owner. See [Permissions](permissions.md).

## Trusting a device

An agent has no account and no password: it joins with an installer key, then signs every report with its own key
from then on, which the server verifies. See [How agents are trusted](agent-trust.md). The agent isn't code signed
yet, so antivirus software may flag it, and the SHA-256 hash the install command checks only catches a file changed
on its way to the machine — it says nothing about a server whose own build was replaced.

## The terminal and remote control

Commands and their output, typed on a device's terminal, and the screen and clipboard content of a remote control
session, are relayed directly between the browser and the device and never written to a log or a database: what is
recorded is only that a session or a command happened, who asked, which device, and whether it was allowed.

## Data at rest

The data protection keys that back every session cookie, by default, are **not encrypted** in the database: anyone
who reads it, or a backup of it, can forge sign-in cookies. Turning on `KeyProtection:EncryptKeys` with a
certificate protects them; the server itself warns about this in production when it's off. See
[Encrypting the data protection keys](../guides/encrypting-keys.md).

## What isn't true yet

StealthDesk hasn't had an independent security review. Treat the points above as what the code does today, not as
a guarantee.
