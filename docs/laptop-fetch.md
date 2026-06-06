# Laptop fetch service

Optional. Lets BabyBrain fetch the Cloudflare-fronted sources (British Museum,
Southbank Centre) through a **real browser on your home laptop** instead of
ScraperAPI, saving credits whenever the laptop is on. When the laptop is off,
the scrape falls back to ScraperAPI automatically — nothing breaks.

Tempo Tots does **not** use this (it's a plain-HttpClient source); only the two
Cloudflare sites route through the laptop.

## Why it has to be a visible browser

Cloudflare's managed challenge fingerprints and blocks **every headless variant**
we tried (bundled Chromium, real Chrome headless, Chrome `--headless=new` with
stealth flags). Only a **genuine, visible Chrome window** passes. So the service:

- drives your installed Google Chrome **non-headless** (windows appear per fetch);
- needs the laptop **unlocked / logged in** to an interactive desktop;
- mainly helps **manual admin "Re-run"s** and any time the laptop happens to be
  on — the scheduled 03:00 UTC scrape runs while the laptop is usually asleep, so
  that one still uses ScraperAPI (which is fine; caching keeps it cheap).

## Prerequisites (laptop)

- Google Chrome installed (the service drives it via channel `chrome`).
- .NET 10 SDK (to run the service).
- An SSH client (Windows 10/11 ships OpenSSH).

## 1. Run the service on the laptop

```powershell
# From the repo root. Use the SAME token you set as a VPS secret (below).
$env:BABYBRAIN_LAPTOP_FETCH_TOKEN = "<the-token>"
dotnet run -c Release --project src/BabyBrain.LaptopFetch
```

It binds to `http://127.0.0.1:8099` (loopback only) and exposes:

- `GET /healthz` → `ok`
- `GET /fetch?url=<absolute-url>` with header `X-Fetch-Token: <token>` → rendered HTML

Quick check:

```powershell
curl.exe -H "X-Fetch-Token: $env:BABYBRAIN_LAPTOP_FETCH_TOKEN" `
  "http://127.0.0.1:8099/fetch?url=https://www.southbankcentre.co.uk/visit-us/families/"
```

## 2. Open the reverse SSH tunnel (laptop → VPS)

The laptop dials out and holds open a tunnel so the VPS can reach the service.
The BabyBrain app runs in Docker, so the tunnel must terminate where the
container can reach it (the host's docker gateway), not just the host's loopback.

**On the VPS**, allow client-specified bind addresses for `-R` (one-time):

```
# /etc/ssh/sshd_config
GatewayPorts clientspecified
# then: sudo systemctl reload ssh
```

**On the laptop**, bind the remote end to the docker bridge gateway
(`172.17.0.1` is the default; confirm with `ip addr show docker0` on the VPS):

```powershell
ssh -N -R 172.17.0.1:8099:localhost:8099 `
    -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes `
    <deploy-user>@<vps-host>
```

Wrap that in a restart loop (or Windows Task Scheduler "at log on") so it
reconnects after sleep/network changes — the OpenSSH equivalent of `autossh`:

```powershell
while ($true) { ssh -N -R 172.17.0.1:8099:localhost:8099 `
  -o ServerAliveInterval=30 -o ServerAliveCountMax=3 -o ExitOnForwardFailure=yes `
  <deploy-user>@<vps-host>; Start-Sleep -Seconds 10 }
```

## 3. Point BabyBrain at it (VPS secrets)

Add two **GitHub repo secrets** (Settings → Secrets and variables → Actions).
The deploy workflow already passes them through to the box:

| Secret | Value |
|---|---|
| `BABYBRAIN_LAPTOP_FETCH_URL`   | `http://host.docker.internal:8099/fetch` |
| `BABYBRAIN_LAPTOP_FETCH_TOKEN` | the shared token (same as on the laptop) |

`host.docker.internal` resolves to the host gateway via the `extra_hosts` mapping
in `docker-compose.yml`. Trigger a deploy (push to main) to apply them. When both
are set, the app registers the laptop as **first-choice** backend; when either is
missing, it's simply not registered and everything uses ScraperAPI.

## 4. Verify

With the service + tunnel up, open **/Admin** and **Re-run** `southbank_centre_families`
or `british_museum_family` (Re-run bypasses the cache). It should succeed, and the
laptop's console will log the fetch. If the laptop is off, the same Re-run still
succeeds via ScraperAPI.

## Security

- The service binds to loopback and is reachable only through the SSH tunnel.
- Every request must present the shared token (constant-time checked).
- Keep `GatewayPorts clientspecified` + the `172.17.0.1` bind so the port is on
  the docker gateway, not a public interface. Don't bind the tunnel to `0.0.0.0`.

## Turning it off

Stop the laptop service / tunnel, or clear the two VPS secrets and redeploy.
Either way the scrape falls back to ScraperAPI.
