# Microsoft Graph mail, calendar and contacts (docker compose)

Gives RockBot access to **one** Microsoft 365 mailbox through an Entra app registration
(app-only / client credentials): mail read-only, calendar and contacts read/write. There's
no extra licensed user and no interactive sign-in at runtime. The `graph-mcp` container is
the only thing that holds the client secret. The agent sees these tools:

| Area | Tools |
|---|---|
| Mail (read-only) | `list_messages`, `search_messages`, `get_message` |
| Calendar | `list_events`, `get_event`, `find_free_time` (read); `create_event`, `update_event`, `respond_to_event`, `delete_event` (write) |
| Contacts | `list_contacts`, `search_contacts`, `get_contact` (read); `create_contact`, `update_contact` (write; no delete) |

`get_message` lists a message's attachments, and `get_attachment` fetches one (up to 25 MB,
`Graph__MaxAttachmentBytes`):
- text files come back as text;
- attached emails come back as a readable message;
- other files are returned as base64. A capture rule that `04-enable-compose.sh` adds to the
  `graph-mail` entry in `mcp.json` makes the agent's bridge save them under
  `/rockbot/shared/attachments/` and hand the model the path, so they never land in its context.
  `Mail.Read` covers attachments; no extra permission is needed.

Scoping is done with Exchange **RBAC for Applications**: the app has no tenant-wide
Graph permission in Entra, only `Application Mail.Read`, `Application Calendars.ReadWrite`
and `Application Contacts.ReadWrite` on a management scope that matches your mailbox.
Every other mailbox returns 403.

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
| `02-scope-mailbox.sh` | In the PowerShell container: Exchange service principal, a management scope matching your mailbox, and `Application Mail.Read`, `Application Calendars.ReadWrite` and `Application Contacts.ReadWrite` role assignments on that scope. Prints `Test-ServicePrincipalAuthorization`. |
| `03-verify.sh [--other-user …]` | Gets a token with the app's credentials and checks your mail, calendar and contacts give 200, the other user's give 403, and the app has no Entra-level Graph application permissions. |
| `04-enable-compose.sh` | Adds `graph` to `COMPOSE_PROFILES`, starts `graph-mcp`, and registers `graph-mail` in the agent's `mcp.json` if the agent is running. |

**Exchange RBAC takes time to apply.** Right after the first run (or after adding a role
to an existing setup), `03-verify.sh` usually gets 403 on your own mailbox. Wait (up to 2 hours), then run
`deploy/graph/03-verify.sh` and `deploy/graph/04-enable-compose.sh` again.

## Use it

```bash
cd deploy/docker-compose && docker compose up -d   # COMPOSE_PROFILES in .env includes graph-mcp
deploy/graph/04-enable-compose.sh                  # once the agent is running, if it wasn't before
```

Then in the Blazor UI: *"What are my 5 latest unread emails?"*, *"What's on my calendar this
week?"*, *"Find me a free hour tomorrow"*, *"What's Alice's mobile number?"*

## Maintain

- **Rotate the secret:** `deploy/graph/01-create-app.sh --rotate`, then
  `docker compose up -d graph-mcp`. The old `rockbot-compose` secret is deleted.
- **Change mailbox:** `deploy/graph/01-create-app.sh --mailbox new@yourdomain.com && deploy/graph/02-scope-mailbox.sh`.
  The scope is updated in place.
- **Remove everything:** `deploy/graph/teardown.sh`.

## Notes

- **Time zone.** The calendar tools read and write in `AGENT_TIMEZONE` (e.g. `Australia/Sydney`),
  the same zone the agent uses for "today". A tool call can name a different zone per event.
- **Email, event and contact content is untrusted input.** The tool descriptions tell the model
  never to follow instructions found in it, but treat any agent that reads mail as exposed to
  prompt injection. Now that it can write, the risk is that an email talks it into sending
  meeting invitations from your address, cancelling meetings, or editing contacts. Invites
  and cancellations go out from your mailbox like any you send yourself.
- **Deleting.** `delete_event` cancels meetings you organise (attendees get a cancellation) and
  silently removes other events; to decline an invitation it uses `respond_to_event`. Contacts
  can't be deleted through RockBot.
- `GRAPH_MAILBOX` is fixed per container. The tools have no mailbox argument, and unknown
  arguments are rejected.
- Secrets created here expire after 1 year by default (`--secret-years N`).
