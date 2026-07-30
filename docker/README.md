# TURN relay (used from Phase 2)

WebRTC connects directly peer-to-peer most of the time, but roughly 15–20% of
real-world network pairs (symmetric NAT, restrictive corporate firewalls) can't.
Those need a TURN relay, which forwards the encrypted media — it can't decrypt it,
but it does pay the bandwidth. Budget for that.

Not needed for Phase 0 (the signaling server relays everything). Stand this up when
you do the WebRTC swap.

## Run
```bash
cd docker
cp turnserver.conf turnserver.local.conf   # edit realm + static-auth-secret
docker compose up -d
```

Generate a strong secret:
```bash
openssl rand -hex 32
```

The signaling server then mints short-lived TURN credentials (HMAC of a timestamp
with the shared secret) and hands them to peers in the ICE server list — so the
long-term secret never leaves your server.

## Ports
| Port | Purpose |
|------|---------|
| 3478/udp+tcp | STUN/TURN listener |
| 5349/tcp | TURN over TLS (helps through firewalls that only allow 443/TLS) |
| 49160-49200/udp | relay port range (keep small; widen if you scale) |
