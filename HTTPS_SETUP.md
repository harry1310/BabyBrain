# Adding HTTPS via Cloudflare + Caddy

This document covers swapping the bare-IP HTTP deploy from `DEPLOYMENT.md`
for a proper domain with HTTPS, served by Caddy in front of the BabyBrain
container.

**Why bother:**
- Modern browsers refuse `navigator.geolocation` on plain HTTP origins.
  Step-free routing currently falls back to manual postcode entry; with
  HTTPS it'll auto-detect.
- Cleaner URL (`https://yourdomain.com` vs `http://49.13.x.x`).
- Drops the browser's "Not secure" warning.
- Lets you share the site without "trust me, click through the warning".

**Total time:** ~30 minutes once you've got a card to hand for the domain
registration.

---

## 0. Prerequisites

- BabyBrain already deployed per `DEPLOYMENT.md` and reachable at
  `http://<server-ip>/`.
- SSH access to the Hetzner server.
- A way to pay for a domain (card or PayPal at Cloudflare).

You can keep BabyBrain running throughout — there's only ~10 seconds of
downtime when Caddy first takes over from the container's port 80.

---

## 1. Register the domain on Cloudflare

1. Go to **<https://dash.cloudflare.com/>** and sign up / log in.
2. Top nav → **Domain Registration → Register Domains**.
3. Search the domain you want (e.g. `babybrain.london`,
   `our-baby-events.uk`). Cloudflare Registrar sells at-cost so prices
   are typically £8–12/year for `.com`/`.uk`/`.london`.
4. Add to cart, check out. Two-factor on the account is strongly
   recommended at this stage.

The new domain auto-gets a Cloudflare DNS zone — no separate "add site"
step needed when you register through them.

> If you've **already got** a domain at another registrar, you can either
> transfer it to Cloudflare (saves money long-term) or just point its
> nameservers at Cloudflare via your existing registrar's dashboard. The
> rest of this doc works either way.

---

## 2. Point the domain at the Hetzner server

1. In Cloudflare dashboard, click into the domain → **DNS → Records**.
2. **Add record:**
   - **Type:** `A`
   - **Name:** `@` (this is the apex, e.g. `babybrain.london` itself —
     use a subdomain like `www` if you want `www.babybrain.london`
     instead, or both)
   - **IPv4 address:** your Hetzner server's public IP (the one from
     step 5 of `DEPLOYMENT.md`)
   - **Proxy status:** **DNS only** (grey cloud, not orange) — see §8 for
     when to flip this on
   - **TTL:** Auto
3. Save.
4. (Optional but recommended) Add a second A record for `www` pointing
   at the same IP, so both `yourdomain.com` and `www.yourdomain.com` work.

**Verify propagation** from your laptop (PowerShell):

```powershell
nslookup yourdomain.com
```

The reply should be the Hetzner IP. Cloudflare DNS propagates in
seconds — if it's not resolving in 60 seconds, double-check the record.

---

## 3. Open port 443 on the Hetzner firewall

In the Hetzner Cloud Console → your project → **Firewalls** → the firewall
attached to your server → **Inbound rules** → **Add rule**:

| Field | Value |
|---|---|
| Protocol | TCP |
| Port | 443 |
| Source | Any IPv4 (`0.0.0.0/0`) + Any IPv6 (`::/0`) |
| Description | HTTPS |

You should already have rules for 22 (SSH) and 80 (HTTP) from the initial
deploy. Keep 80 open — Caddy uses it to handle the Let's Encrypt
HTTP-01 challenge and to issue automatic HTTP → HTTPS redirects.

---

## 4. Add Caddy to docker-compose

SSH into the server:

```bash
ssh root@<server-ip>
cd /opt/babybrain
```

**Edit `docker-compose.yml`** to add a Caddy service and remove the
public port mapping from babybrain so it only listens internally:

```yaml
services:
  caddy:
    image: caddy:2-alpine
    container_name: caddy
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy_data:/data
      - caddy_config:/config
    depends_on:
      - babybrain

  babybrain:
    build: .
    container_name: babybrain
    restart: unless-stopped
    env_file: .env
    environment:
      BABYBRAIN_DB_PATH: /data/babybrain.db
      BABYBRAIN_HTML_ARCHIVE_PATH: /data/scrape-html
    # NB: no `ports:` block any more — Caddy is the only public face.
    volumes:
      - ./data:/data

volumes:
  caddy_data:
  caddy_config:
```

**Create the Caddyfile** alongside `docker-compose.yml`:

```bash
cat > Caddyfile <<'EOF'
yourdomain.com, www.yourdomain.com {
    reverse_proxy babybrain:8080
}
EOF
```

Replace `yourdomain.com` with your actual domain. If you didn't add the
`www` A record, drop the `, www.yourdomain.com` part.

That's the whole config. Caddy handles:
- Fetching a Let's Encrypt cert on first request
- Renewing the cert automatically (every ~60 days)
- Redirecting HTTP to HTTPS
- Proxying requests through to the BabyBrain container on port 8080

---

## 5. Bring it up

```bash
docker compose up -d
```

(no `--build` needed since we're just adding a service)

`docker compose ps` should show **both** containers as running:

```
NAME        STATUS
babybrain   Up (healthy)
caddy       Up
```

`docker compose logs caddy --tail=30` will show Caddy fetching the cert.
You'll see lines like:

```
certificate obtained successfully  identifier=yourdomain.com
```

If you see ACME errors:
- **`no such host`** → DNS hasn't propagated yet. Wait 60s, restart Caddy.
- **`connection refused`** → Port 80 isn't open in the Hetzner firewall.
- **`unauthorized`** → DNS A record is pointing at the wrong IP.

---

## 6. Verify HTTPS works

In your browser:

- **`https://yourdomain.com/`** — should load the search page with a
  green padlock.
- **`https://yourdomain.com/Admin`** — should prompt for the basic auth
  credentials, then load Admin.
- **`http://yourdomain.com/`** — should auto-redirect to `https://...`.

In PowerShell:

```powershell
curl -I https://yourdomain.com/   # expect HTTP 200
curl -I http://yourdomain.com/    # expect HTTP 308 → https
```

---

## 7. Confirm geolocation now works

On `https://yourdomain.com/`, scroll to any event with a map dot, click
the dot, click **"Step-free →"**. The browser should prompt for location
permission — click Allow once and it remembers. Step-free routing now
happens automatically without the postcode-fallback dialog.

---

## 8. (Optional) Enable Cloudflare proxy / CDN

Once you've confirmed everything works **DNS-only**, you can flip the
proxy on for CDN, DDoS protection, free analytics, etc.:

1. Cloudflare dashboard → your domain → **DNS → Records**.
2. Edit the A record → flip **Proxy status** to **Proxied** (orange cloud).
3. SSL/TLS → **Overview** → set encryption mode to **Full (strict)**.
   This tells Cloudflare to expect a valid cert at the origin (which
   Caddy provides). The "Flexible" mode is dangerous — it does HTTPS
   visitor↔Cloudflare but plain HTTP Cloudflare↔origin, which would
   re-expose us to the geolocation issue.

Drawbacks of proxying:
- Cloudflare sees all traffic (privacy consideration).
- IP address logging on the server now shows Cloudflare IPs unless you
  configure `trusted_proxies` in Caddy.

Skip this section if you don't need the CDN — the DNS-only setup is
fine for a small site.

---

## 9. Updates after this

Future code deploys are unchanged — `git pull && docker compose up -d
--build` just rebuilds the BabyBrain image. Caddy doesn't need to
restart; its config and certs survive.

If you ever change the domain, edit `Caddyfile` and run
`docker compose restart caddy`. Caddy will fetch a fresh cert for the
new domain.

---

## Rollback

If something's gone wrong and you want to get back to the bare-IP HTTP
setup:

1. Edit `docker-compose.yml`: delete the `caddy` service and the
   `caddy_data` / `caddy_config` volumes; add `ports: ["80:8080"]` back
   onto the `babybrain` service.
2. `docker compose down && docker compose up -d`.

The domain stays registered (Cloudflare doesn't refund), but the A
record is harmless — you can leave it or delete it.
