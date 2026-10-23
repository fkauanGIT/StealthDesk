# Behind a reverse proxy

Behind a reverse proxy (Nginx, Caddy, Cloudflare) the server sees the proxy's address and plain HTTP unless it trusts
the proxy. From a trusted proxy it takes the client's address, scheme and host from `X-Forwarded-For`,
`X-Forwarded-Proto` and `X-Forwarded-Host` before anything else runs, so cookies are `Secure`, sign-in redirects go
back to `https://` and logs record the client. A chain of proxies (Cloudflare in front of Nginx) is followed back as
long as each one is trusted. Only a proxy on the same machine is trusted until the `ReverseProxy`
[settings](../reference/server-settings.md#reverse-proxy) name others.

## WebSockets and the agents' route

The proxy must pass WebSocket upgrades (the `Upgrade` and `Connection` headers) to `/hubs/agent` and
`/hubs/dashboard`. Agents use nothing but `/hubs/agent`, and prove who they are with signed reports and installer
keys. When the proxy puts its own sign-in in front of the server (Cloudflare Access, for instance), leave that path
open; browsers need the rest.

With [Docker Compose](docker-compose.md), the server already trusts the Docker network's gateway, which a proxy on the
same host comes through.
