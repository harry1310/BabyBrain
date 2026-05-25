# BabyBrain — Deployment Plan (v1)

Single-VPS deploy on Hetzner Cloud. ~€4.59/mo, all-in.

## Decisions (set 2026-05-11)

- **Provider:** Hetzner Cloud, CX22 instance (2 vCPU, 4 GB RAM, 40 GB SSD)
- **Location:** Nuremberg (NBG1) or Falkenstein (FSN1) — Hetzner has no UK DC; these are ~30 ms from London, fine
- **OS:** Ubuntu 24.04 LTS
- **Domain:** none for v1 — site reachable at `http://<server-ip>`. Add a domain + HTTPS later
- **Admin auth:** HTTP basic auth on `/Admin`, credentials in env vars
- **Scraping:** runs daily at 06:30 UTC as a background service inside the app — no cron job, no external trigger

---

## Your manual steps

### 1. Sign up for Hetzner Cloud

Go to **https://www.hetzner.com/cloud** and create an account. Verification can take an hour or so (they sometimes ask for ID for first-time accounts).

Add a payment method.

### 2. Create a project

In the Hetzner Cloud Console, click **+ New project**. Name it `babybrain` (or whatever).

### 3. Generate an SSH key (if you don't have one)

On your local machine, in PowerShell:

```powershell
ssh-keygen -t ed25519 -C "your-email@example.com"
```

Accept the default path (`C:\Users\<you>\.ssh\id_ed25519`). Set a passphrase if you want.

Print the public key — you'll paste it into Hetzner:

```powershell
Get-Content $env:USERPROFILE\.ssh\id_ed25519.pub
```

### 4. Add the SSH key to Hetzner

In the project, **Security → SSH Keys → Add SSH Key**. Paste the public key. Give it a name like `harry-laptop`.

### 5. Create the server

In the project, **+ Add Server**.

- **Location:** Nuremberg or Falkenstein
- **Image:** Ubuntu 24.04
- **Type:** Shared vCPU → **CX22**
- **Networking:** keep IPv4 + IPv6 (default)
- **SSH keys:** select the key you just added
- **Name:** `babybrain-1`

Click **Create & Buy now**. ~30 seconds later you get a public IP. Write it down. Call it `$IP` from here on.

### 6. First SSH

```powershell
ssh root@$IP
```

(Replace `$IP` with the real address. First time you'll be asked to accept the host key — type `yes`.)

You should land in a root shell on the Ubuntu box.

### 7. Install Docker

Still in the SSH session:

```bash
apt-get update
apt-get install -y docker.io docker-compose-v2
systemctl enable --now docker
docker --version
```

Expect Docker 24+ and a clean version line.

### 8. *(Wait here)* — I'll prepare the code

Before you can deploy, I need to add to the repo:

- A `Dockerfile` (multi-stage build, Playwright base image)
- A `docker-compose.yml`
- A `.dockerignore`
- An `IHostedService` that runs the scrape daily at 06:30 UTC
- Basic auth middleware on `/Admin` reading `BABYBRAIN_ADMIN_USER` / `BABYBRAIN_ADMIN_PASSWORD` from env
- A small refactor so the SQLite path is configurable via `BABYBRAIN_DB_PATH` (default `/data/babybrain.db` in container)

I'll tell you when this is committed and pushed.

### 9. Pull and run

Back on the server:

```bash
mkdir -p /opt/babybrain && cd /opt/babybrain
git clone https://github.com/harry1310/BabyBrain.git .

# Create the data directory the volume will mount to
mkdir -p data

# Create .env with your admin credentials (choose a strong password!)
# BABYBRAIN_SCRAPE_ON_STARTUP=true → run a scrape immediately on container
# start, useful right after a fresh deploy. Set to false (or omit) on subsequent
# starts; the daily 06:30 UTC schedule will keep data fresh.
cat > .env <<'EOF'
BABYBRAIN_ADMIN_USER=harry
BABYBRAIN_ADMIN_PASSWORD=replace-with-a-real-strong-password
BABYBRAIN_SCRAPE_ON_STARTUP=true
EOF
chmod 600 .env

docker compose up -d --build
```

First build will take 5-10 minutes (downloads .NET SDK + Playwright base image). Subsequent builds are seconds.

### 10. Verify

```bash
docker compose ps           # service should be "running"
docker compose logs --tail=50 babybrain
curl -I http://localhost   # 200 OK
```

From your laptop:

```powershell
Start-Process "http://$IP"
```

Should load the search page. Try `/Admin` — browser will prompt for the credentials you set in `.env`.

### 11. First scrape

The first scheduled scrape will run at 06:30 UTC. If you want data right away, log into `/Admin` and click "Run all scrapers now". Wait ~6 minutes (same as locally — Cloudflare-challenged sites are slow).

### 12. Optional: a tiny firewall

By default the server's only open port we care about is 80. SSH (22) is also open and that's fine. If you want to be tidier, in Hetzner Console → **Firewalls**, allow inbound 22, 80 (and 443 later when we add HTTPS) — block everything else.

---

## Updating later

When I push new code, on the server:

```bash
cd /opt/babybrain
git pull
docker compose up -d --build
```

That's it. The SQLite DB on `/opt/babybrain/data/` survives rebuilds because it's a mounted volume.

---

## What's not covered yet (deliberately, for v1)

| Thing | Why deferred | When to revisit |
|---|---|---|
| Custom domain | Adds DNS + TLS setup | When you want to share publicly |
| HTTPS | Needs a domain first | Same as above (Caddy or Traefik handles it auto) |
| Automated backups | Snapshot the SQLite file to S3/B2 | When the DB has data you'd cry to lose |
| CI/CD on push | `git pull && up -d` is fine for now | When push-to-deploy gets annoying |
| Monitoring / alerts | Tiny app, you'll just notice | When real users complain |
| Multi-instance / Postgres | SQLite handles tens of users on one box | When you need >1 server |

---

## Cost summary

| Item | Cost |
|---|---|
| Hetzner CX22 | €4.59/mo |
| IPv4 address | included |
| Bandwidth (20 TB/mo) | included |
| Domain (later) | ~£7-12/yr |
| **Total v1** | **€4.59/mo** |
