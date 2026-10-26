# Endpoints

Every route the server answers, by path. A user marked to change their password gets `403` from the rest of the API
and the hubs until they change it.

## Hubs

| Path | Transport | Purpose |
|------|-----------|---------|
| `/hubs/agent` | WebSockets | SignalR gateway agents stay connected to. Carries their signed device reports, the server's calls to the agent, and terminal output. |
| `/hubs/dashboard` | WebSockets | SignalR hub signed-in browsers connect to for live updates of their tenant's devices, to ask which actions they may use on one, and to open, type in and close a terminal on a device. Each terminal call checks `device.terminal.use` on that device; a terminal's output goes only to the connection that opened it, and closes with it. |

## Devices, tenants and the server

| Path | Transport | Purpose |
|------|-----------|---------|
| `/api/v1/devices` | HTTP | Lists the devices of the signed-in user's tenant; 401 when signed out. |
| `/api/v1/devices/{id}` | HTTP | Returns one of the tenant's devices, or 404 for an unknown device or one of another tenant. |
| `/api/v1/device-groups` | HTTP | The tenant's device groups: list, one with its devices, create, change, delete, and add, remove or move devices (`POST`/`DELETE /{id}/members`, `fromGroupId` to move). Device permissions can be granted on a group, reaching every device in it. |
| `/api/v1/tenants` | HTTP | The server's tenants, for its administrators: list (`GET`), one (`GET /{id}`), create (`POST`), rename (`PUT /{id}`) and delete with everything in them (`DELETE /{id}`, never the caller's own). |
| `/api/v1/server/alert` | HTTP | The message every signed-in user sees at the top of the app (`GET`), set by the server's administrators (`PUT`). |
| `/api/v1/server/test-email` | HTTP | Sends a test message to the administrator's own address and says whether it went out. |
| `/api/v1/tenant-settings` | HTTP | The tenant's settings: list (`GET`), one (`GET /{name}`), change one (`PUT /{name}`) or several (`PUT`), back to the default (`DELETE /{name}`). |

## Installing agents

| Path | Transport | Purpose |
|------|-----------|---------|
| `/api/v1/deployment-options` | HTTP | What install commands use, for those who install agents: the tenant's agent instance, and the Windows agent the server hands out with its size and SHA-256. |
| `/downloads/agent/windows` | HTTP | The Windows agent build, for anyone; 404 when the server has none. |
| `/api/v1/installer-keys` | HTTP | The tenant's installer keys: list (`GET`), create (`POST`), rename (`PUT /{id}`), revoke (`DELETE /{id}`) and installs (`GET /{id}/usages`). Without `installer-key.manage-all`, only your own. |

## Signing in

| Path | Transport | Purpose |
|------|-----------|---------|
| `/api/auth/login` | HTTP | Signs in. `?useCookies=true` sets the browser cookie; without it, returns bearer tokens when `Accounts:EnableBearerLogin` is on. |
| `/api/auth/register` | HTTP | Registers following the [account rules](../guides/accounts.md#who-can-register); 404 while registration is closed. |
| `/api/auth/settings` | HTTP | Whether registration is open, for the sign-up page. |
| `/api/auth/me` | HTTP | The signed-in user and tenant, or 401. |
| `/api/auth/sign-out` | HTTP | Ends the cookie session. |
| `/api/auth/*` | HTTP | The other ASP.NET Core Identity endpoints: refresh, confirm email, forgot and reset password, manage info. |
| `/api/auth/two-factor` | HTTP | The second sign-in step with the app's code, after `/login` answered `RequiresTwoFactor`. `/api/auth/recovery-code` does the same with a recovery code. |
| `/api/auth/passkey` | HTTP | Signs in with a passkey, after `POST /api/auth/passkey/request-options`. Needs no second factor. |
| `/api/auth/external/{provider}` | HTTP | Leaves for Microsoft or GitHub to sign in; the provider comes back to `/signin-microsoft` or `/signin-github`, then to `/api/auth/external/callback`. `/pending` and `/register` create the account on a first visit. |

## The signed-in account

| Path | Transport | Purpose |
|------|-----------|---------|
| `/api/account/profile` | HTTP | The signed-in user's own account (`GET`), and saving the phone number (`PUT`). |
| `/api/account/password` | HTTP | Changes the password; other sessions are signed out. `/password/set` adds one to an account without it. |
| `/api/account/personal-data` | HTTP | Downloads the user's personal data as JSON. |
| `/api/account/delete` | HTTP | Deletes the signed-in account; needs the password when the account has one. |
| `/api/account/two-factor/*` | HTTP | Two-factor with an authenticator app: status, the key and QR code, turning it on and off, resetting the key, new recovery codes, forgetting this browser. |
| `/api/account/passkeys` | HTTP | The user's passkeys: list (`GET`), add (`POST`, after `POST /creation-options`), rename (`PUT /{id}`) and remove (`DELETE /{id}`). |
| `/api/account/logins` | HTTP | The providers linked to the account: list (`GET`), link (`GET /link/{provider}`), unlink (`DELETE /{provider}/{key}`). |

## Health and version

| Path | Transport | Purpose |
|------|-----------|---------|
| `/api/internal/version/server` | HTTP | Returns the server version. |
| `/health/ready` | HTTP | Readiness: every check must pass, including that the database answers. `/health` does the same. |
| `/health/live` | HTTP | Liveness: only that the process responds, so a server waiting for its database isn't restarted. `/alive` does the same. |
