# How agents are trusted

An agent has no account and no password. The server trusts it through two keys: an installer key to join, and the
device's own key from then on.

## Joining with an installer key

A new device joins a tenant with one of its installer keys. Keys are usage-based (a number of installs within 24
hours), time-based (until they expire) or persistent (until revoked), may place new devices in a device group, and
each key's installs are listed. The agent never saves the key, and the Windows service never receives it: it is only
needed until the device joins.

## The device's own key

On its first run the agent creates an Ed25519 key pair and stores it with its settings, and the server assigns the
device an id with the first accepted report. From then on every report is signed with that key. The server verifies
each signature, and refuses reports whose time is further from its own clock than `Gateway:ClockTolerance` allows, so
an old report can't be replayed. Revoking the installer key doesn't affect a device that joined with it.

## Taking back a device

A reinstalled agent brings a new signing key. The server accepts it for a known device only with an installer key
whose creator has `agent.install` there, and `--device-id <id>` names the device to take back. Without that, a key
change is refused: nobody can take over a device by claiming its id.

## The agent download

The install command **Add device** gives downloads the agent from the server and runs it only if its SHA-256 is the
one the page named. The hash guards the download, not the server: it catches a file changed on its way to the machine,
but a server whose own build was replaced names the replacement's hash. Telling a genuine build from any other takes
code signing, which the agent doesn't have yet.

## Behind a proxy

Agents use nothing but `/hubs/agent`, and prove who they are with signed reports and installer keys. A proxy that puts
its own sign-in in front of the server must leave that path open; see
[Behind a reverse proxy](../guides/reverse-proxy.md).
