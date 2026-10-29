# Behind a reverse proxy

The server speaks plain HTTP. HTTPS comes from a reverse proxy in front of it, such as Caddy or Nginx, which holds the
certificate and forwards each request to the server.

## What the server trusts

Behind a reverse proxy (Nginx, Caddy, Cloudflare) the server sees the proxy's address and plain HTTP unless it trusts
the proxy. From a trusted proxy it takes the client's address, scheme and host from `X-Forwarded-For`,
`X-Forwarded-Proto` and `X-Forwarded-Host` before anything else runs, so cookies are `Secure`, sign-in redirects go
back to `https://` and logs record the client. A chain of proxies (Cloudflare in front of Nginx) is followed back as
long as each one is trusted. Only a proxy on the same machine is trusted until the `ReverseProxy`
[settings](../reference/server-settings.md#reverse-proxy) name others.

With [Docker Compose](docker-compose.md), a proxy installed on the same host reaches the server through the Docker
network's gateway, which the server already trusts: the configurations below need no setting on the server's side. A
proxy on another machine, or in another container, is trusted by its address with `ReverseProxy:TrustedProxies`.

## Caddy

[Caddy](https://caddyserver.com/docs/install) gets and renews a Let's Encrypt certificate on its own, passes
WebSockets and adds the forwarded headers. With the server published on `127.0.0.1:8080`, `/etc/caddy/Caddyfile`:

```
desk.example.com {
	reverse_proxy 127.0.0.1:8080
}
```

Then `sudo systemctl reload caddy`. The name must point to the machine, with ports 80 and 443 reachable from the
internet, for Let's Encrypt to issue the certificate. On a network closed to the internet, `tls internal` in the block
makes Caddy issue the certificate itself; the company's machines then need Caddy's root certificate to trust it.

## Nginx

Nginx needs the certificate from elsewhere, such as [Certbot](https://certbot.eff.org/), and the WebSocket headers
spelled out. In `/etc/nginx/conf.d/stealthdesk.conf`:

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 80;
    server_name desk.example.com;
    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    server_name desk.example.com;

    ssl_certificate     /etc/letsencrypt/live/desk.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/desk.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;

        # WebSockets, for the agents' and the browsers' live connections.
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;

        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
    }
}
```

Then `sudo nginx -t && sudo systemctl reload nginx`. The live connections stay open: the server sends a keep-alive
every 15 seconds, within Nginx's default `proxy_read_timeout` of 60.

## WebSockets and the agents' routes

The proxy must pass WebSocket upgrades (the `Upgrade` and `Connection` headers) to `/hubs/agent`,
`/hubs/dashboard` and `/relay`. Agents use nothing but `/hubs/agent`, and prove who they are with signed reports and
installer keys; a device's end of a remote control session joins `/relay` with the session's one-time token. When the proxy puts its own sign-in in front of the server (Cloudflare Access, for instance), leave these paths
open; browsers need the rest:

| Path | Who uses it |
|---|---|
| `/hubs/agent` | Every agent, all the time. |
| `/relay` | A device's end of a remote control session. |
| `/downloads/agent/windows` | The install command, on a machine being installed: it has no account. |

## Check it

Sign in at `https://desk.example.com`, then look at the session cookie in the browser's developer tools: it is marked
`Secure` only when the server trusted the proxy's `X-Forwarded-Proto`. The device list shows **Live** once its
WebSocket connected through the proxy.
