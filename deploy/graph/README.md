# Microsoft Graph mail access (docker compose)

Gives RockBot read-only access to **one** Microsoft 365 mailbox through an Entra app
registration (app-only / client credentials). There's no extra licensed user and no
interactive sign-in at runtime. The `graph-mcp` container is the only thing that
holds the client secret; the agent only sees three read-only tools:
`list_messages`, `search_messages`, `get_message`.

Scoping is done with Exchange **RBAC for Applications**: the app has no tenant-wide
Graph permission in Entra, only `Application Mail.Read` on a management scope that
matches your mailbox. Every other mailbox returns 403.

## Prerequisites

- A business / work Microsoft 365 tenant (personal Microsoft accounts don't support app-only access).
- An account that can create app registrations (`az`) and is an **Exchange administrator** (device-code sign-in in step 2).
- `az`, `jq`, `curl` and `docker` on the host. `pwsh` is **not** needed; step 2 runs in `mcr.microsoft.com/powershell`.

## Set up

```bash
deploy/graph/setup.sh --mailbox you@yourdomain.com --other-user colleague@yourdomain.com
```

That runs the four steps below in order. Each one is idempotent, so re-running is safe
and changes only what's missing.

| Script | What it does |
|---|---|
| `01-create-app.sh --mailbox …` | Finds or creates the app registration and service principal (`az ad app/sp`), mints a client secret only if none is stored, and writes `GRAPH_*` into `deploy/docker-compose/.env` (gitignored). |
| `02-scope-mailbox.sh` | In the PowerShell container: Exchange service principal, a management scope matching your mailbox, and an `Application Mail.Read` role assignment on that scope. Prints `Test-ServicePrincipalAuthorization`. |
| `03-verify.sh [--other-user …]` | Gets a token with the app's credentials and checks your mailbox gives 200, the other user's gives 403, and the app has no Entra-level Graph application permissions. |
| `04-enable-compose.sh` | Adds `graph` to `COMPOSE_PROFILES`, starts `graph-mcp`, and registers `graph-mail` in the agent's `mcp.json` if the agent is running. |

**Exchange RBAC takes time to apply.** Right after the first run, `03-verify.sh` usually
gets 403 on your own mailbox. Wait (up to 2 hours), then run
`deploy/graph/03-verify.sh` and `deploy/graph/04-enable-compose.sh` again.

## Use it

```bash
cd deploy/docker-compose && docker compose up -d   # COMPOSE_PROFILES in .env includes graph-mcp
deploy/graph/04-enable-compose.sh                  # once the agent is running, if it wasn't before
```

Then in the Blazor UI (`http://localhost:8080`): *"What are my 5 latest unread emails?"*

## Maintain

- **Rotate the secret:** `deploy/graph/01-create-app.sh --rotate`, then
  `docker compose up -d graph-mcp`. The old `rockbot-compose` secret is deleted.
- **Change mailbox:** `deploy/graph/01-create-app.sh --mailbox new@yourdomain.com && deploy/graph/02-scope-mailbox.sh`.
  The scope is updated in place.
- **Remove everything:** `deploy/graph/teardown.sh`.

## Notes

- Email is untrusted input. The tool descriptions tell the model never to follow instructions
  found in messages, but treat any agent that reads mail as exposed to prompt injection.
- `GRAPH_MAILBOX` is fixed per container. The tools have no mailbox argument, and unknown
  arguments are rejected.
- Secrets created here expire after 1 year by default (`--secret-years N`).
