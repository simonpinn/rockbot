# HTTPS + Microsoft sign-in for LAN access (docker compose)

Lets other devices on your network use the Blazor UI at `https://rockbot.<your-zone>` with a
real Let's Encrypt certificate and Microsoft (Entra ID) sign-in. Nothing is exposed to the
internet: the name resolves to a LAN IP, and the certificate comes from a DNS-01 challenge
answered through Azure DNS.

```
phone/laptop ──https──▶ caddy :443 (Let's Encrypt cert) ──http──▶ blazor :8080 (sign-in + allowlist)
```

`blazor` binds to `127.0.0.1:8080` by default, so the LAN can only come in through Caddy.

## Prerequisites

- A domain you control at any registrar (e.g. `spinn.net.au` at Melbourne IT), and the ability
  to add NS records there. That one step is manual.
- An Azure subscription for the DNS zone (about US$0.50 a month), and rights to create app
  registrations and role assignments. `az login` first.
- `az`, `jq`, `dig`, `curl`, `docker`.

## Set up

```bash
deploy/https/setup.sh --zone home.spinn.net.au --allow-email simon@spinn.net.au
```

| Script | What it does |
|---|---|
| `01-dns.sh --zone … [--host rockbot] [--ip auto]` | Resource group `rockbot-dns`, the DNS zone, an A record pointing at this machine's LAN IP, and a `RockBot Caddy DNS` app with **DNS Zone Contributor** on that resource group only. Checks delegation; if it's missing, prints the NS records to add at your registrar and stops. |
| `02-signin-app.sh [--allow-email …]` | A `RockBot Sign-in` app (single tenant, redirect `https://<host>/signin-microsoft`, `email` claim, consented openid/profile/email) and the `BLAZOR_AUTH_*` settings. |
| `03-enable.sh` | Adds `https` to `COMPOSE_PROFILES`, starts `caddy` and `blazor`, waits for the certificate and checks that `/` asks for sign-in. |

On the first run the script stops after step 1. Add the four `NS` records it prints (host
`home` under `spinn.net.au`), wait a few minutes, and run it again. Every step is idempotent.

## Things to know

- **Use the HTTPS name on this machine too.** Signing in at `http://localhost:8080` won't
  complete, because the Microsoft redirect always goes back to the public name.
- **DNS-rebinding protection.** Some routers refuse public DNS answers that point at private IPs
  (192.168.x.x). If devices can't resolve the name but `dig rockbot.home.spinn.net.au @1.1.1.1`
  works, allow-list the zone in your router or add a local DNS override for it.
- **Only your tenant can sign in.** The provider requires a tenant GUID (no
  `common`/`organizations`), and the email allowlist is still checked after sign-in.
- **chezmoi/Bitwarden.** The scripts write `RB_*`, `CADDY_AZURE_*` and `BLAZOR_AUTH_*` into
  `deploy/docker-compose/.env`; copy them into your template before the next `chezmoi apply`.
- **Secrets** last a year. Re-run with `--rotate` to replace both.
- **Helm** doesn't wire the Microsoft provider yet; this is compose-only for now.

## Remove

`deploy/https/teardown.sh` stops Caddy, turns sign-in off and deletes both apps. Add
`--delete-zone` to remove the DNS zone as well, then delete the NS records at your registrar.
