# Deploying Remotler to a public server (DigitalOcean droplet)

Moving from the Raspberry Pi (LAN) to a public host adds two must-haves:

1. **TLS (HTTPS/WSS)** — the server speaks plaintext `ws://`/`http://`, which is fine on a
   trusted LAN but unacceptable on the internet (credentials + session tokens in the clear).
   A Caddy reverse proxy terminates TLS on 443 and proxies to the Node server on localhost.
2. **A TURN relay** — across the internet many networks can't hole-punch a direct P2P path,
   so you need a relay fallback (coturn). Media stays end-to-end encrypted; the relay only
   forwards ciphertext.

Files referenced below live in this `deploy/` folder.

---

## 1. Create the droplet
- Ubuntu 22.04/24.04, 1–2 GB RAM is plenty for signaling (relayed *media* needs more
  bandwidth — size up if many sessions fall back to TURN).
- Add your SSH key. Note the public IP.

## 2. DNS
Point an A record at the droplet, e.g. `remotler.com → <droplet-ip>`.
(TURN can share the same name.)

## 3. Base setup
```bash
ssh root@<droplet-ip>
adduser --system --group remotler
apt update && apt install -y nodejs npm git

# get the code
mkdir -p /opt/remotler && chown remotler:remotler /opt/remotler
sudo -u remotler git clone https://github.com/edkeysender/remote-desktop.git /opt/remotler
cd /opt/remotler/server && sudo -u remotler npm install --omit=dev
```
> Node from Ubuntu's repo may be old. If `node -v` < 18, install NodeSource 20:
> `curl -fsSL https://deb.nodesource.com/setup_20.x | bash - && apt install -y nodejs`

## 4. Run the server as a service
```bash
cp /opt/remotler/deploy/remotler-signal.service /etc/systemd/system/
# EDIT it: set strong ADMIN_PASSWORD and PLATFORM_PASSWORD
systemctl daemon-reload && systemctl enable --now remotler-signal
systemctl status remotler-signal --no-pager        # should be active, listening on 127.0.0.1:8081
```

## 5. TLS reverse proxy (Caddy)
```bash
apt install -y debian-keyring debian-archive-keyring apt-transport-https curl
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg
curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list
apt update && apt install -y caddy

cp /opt/remotler/deploy/Caddyfile /etc/caddy/Caddyfile
# EDIT it: set your domain + email
systemctl reload caddy
```
Caddy now serves `https://remotler.com` and `wss://remotler.com` with an
auto-renewing certificate. Verify: `curl https://remotler.com/health` → `ok`.

## 6. TURN relay (coturn)
```bash
apt install -y coturn
sed -i 's/#TURNSERVER_ENABLED=1/TURNSERVER_ENABLED=1/' /etc/default/coturn
cp /opt/remotler/deploy/turnserver.conf /etc/turnserver.conf
# EDIT it: domain + a strong TURN secret; ensure the cert path exists
systemctl enable --now coturn
```
Then in the org dashboard → **Configurations → Network / relay**:
- STUN: `stun:remotler.com:3478`
- TURN URL: `turn:remotler.com:3478` · user `remotler` · password `<your secret>`

## 7. Firewall
```bash
ufw allow 22/tcp
ufw allow 443/tcp                 # HTTPS + WSS (Caddy)
ufw allow 80/tcp                  # ACME cert challenges
ufw allow 3478                    # TURN (tcp+udp)
ufw allow 5349                    # TURN over TLS
ufw allow 49152:65535/udp         # TURN relay media range
ufw enable
```
Do **not** expose 8081 publicly — it's localhost-only behind Caddy.

## 8. Point the apps at it
In the desktop app: **Settings → Signaling server → Change** to:
```
wss://remotler.com
```
(No port — 443 is implied.) New device enrollments and the web dashboard are then reachable
at `https://remotler.com`.

## 9. First-run security
- Change `ADMIN_PASSWORD` and `PLATFORM_PASSWORD` (done in step 4) — never leave defaults.
- Create your org via the web dashboard, then create users/enrollment tokens.
- Publish the app installers + update package from your build machine (the `remotler` service
  user has no SSH login, so push as root, then hand ownership back):
  ```powershell
  build.ps1 -PushTo root@remotler.com -PiUpdateDir /opt/remotler/server/update
  ```
  ```bash
  ssh root@remotler.com 'chown -R remotler:remotler /opt/remotler/server/update'
  ```
  so the dashboard's **Download** tab and in-app updates work.

## Updating the server later
```bash
cd /opt/remotler && sudo -u remotler git pull && cd server && sudo -u remotler npm install --omit=dev
systemctl restart remotler-signal
```
