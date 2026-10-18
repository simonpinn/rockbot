# Obsidian MCP server (docker compose)

Gives RockBot an Obsidian vault that is synced with **Obsidian Sync**. Two services, profile `obsidian`:

| Service | What it does |
|---|---|
| `obsidian-sync` | Obsidian's official [headless Sync client](https://obsidian.md/help/sync/headless) (`obsidian-headless`, CLI `ob`). Keeps the `obsidian-vault` volume in two-way sync with one remote vault. Only this container holds the Obsidian login (`obsidian-config` volume). |
| `obsidian-mcp` | `src/McpServer.Obsidian`, an MCP server over the same volume. |

RockBot gets six tools:

| Read (whole vault) | Write (only under `OBSIDIAN_WRITE_FOLDER`, default `RockBot/`) |
|---|---|
| `search_notes`, `read_note`, `list_notes`, `list_folders` | `create_note`, `append_to_note` |

Nothing is ever deleted. The tools only handle `.md` notes. They don't go into hidden folders
(`.obsidian`, `.trash`) and don't follow symlinks. Notes RockBot writes get frontmatter with
`created`, `source` and `tags` (always including `rockbot`). They reach your other devices
through Sync, and appear in Sync's version history under the device name **RockBot**.

The desktop app doesn't need to be running. The headless client syncs its own copy, so keep
using Obsidian on your desktop as normal.

## Requirements

- An Obsidian Sync subscription. The headless client is in open beta.
- If the vault is end-to-end encrypted, its encryption password. It's entered once, at setup.

## Set up

Run it in a terminal, because it prompts:

```bash
deploy/obsidian/setup.sh --vault "My Vault"
```

- It signs in to your Obsidian account. You'll be asked for your email, password and MFA code.
  The token is kept in the `obsidian-config` volume.
- It binds the vault volume to the remote vault, starts both services, waits for the first sync,
  and registers `obsidian` in the agent's `mcp.json`.
- Without `--vault` it lists your remote vaults and asks which one to use.
- Re-running is safe.

| Option | Effect |
|---|---|
| `--vault NAME` | Remote vault to sync (name or id) |
| `--vault-name NAME` | Vault name for `obsidian://` links in replies (defaults to `--vault`) |
| `--write-folder FOLDER` | The only folder RockBot may write to (default `RockBot`) |

It writes `OBSIDIAN_WRITE_FOLDER`, `OBSIDIAN_VAULT_NAME` and `obsidian` in `COMPOSE_PROFILES`
to `deploy/docker-compose/.env`. If you manage `.env` another way, copy them there.

Remove it with `deploy/obsidian/teardown.sh`. Add `--purge` to also sign out and delete this
machine's copy of the vault; the remote vault is untouched.

## Saving to Obsidian

RockBot's directives (`directives.md`, "Saving to Obsidian") tell it to:

- offer to save an Advisor Council or ResearchAgent result as a note, and save it in full if you say yes;
- save anything else when you ask ("save this to Obsidian");
- search and read your notes when you refer to them.

## Security

Note content is untrusted input to the agent. A note, or an email or web page RockBot is asked
to save, could contain instructions trying to steer it. The tool descriptions tell the model to
ignore them, and the server enforces the hard limit: it can only write inside the write folder,
and never deletes. Everything in the vault is readable by the agent, so keep anything it should
never see out of this vault.
