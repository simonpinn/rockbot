# Trello MCP server (docker compose)

Runs `trello-mcp` (`src/McpServer.Trello`), a small MCP server holding the only copy of the
Trello key and token. RockBot gets eight tools:

| Read | Write |
|---|---|
| `list_boards`, `get_board`, `list_cards`, `get_card`, `search` | `create_card`, `update_card` (rename, describe, due date, mark done, move), `add_comment` |

It can't archive or delete anything. With a board allowlist (`--boards`), every call checks
the board first, including the board of the target list when moving a card.

Trello's official MCP server (`mcp.trello.com`) only supports interactive OAuth, which the
RockBot bridge doesn't do for third-party servers yet; hence this server.

## 1. Get an API key

1. Open <https://trello.com/power-ups/admin> and create a Power-Up (any name, e.g. *RockBot*; the
   iframe connector URL can be left blank).
2. Open it, go to **API key**, and generate one.

## 2. Run setup

```bash
deploy/trello/setup.sh --boards "Home,Side projects"
```

It prompts for the API key, then prints an authorize URL. Open it, approve
(read + write, no expiry), and paste the token. It checks both, resolves the board names to
ids, stores `TRELLO_*` in `deploy/docker-compose/.env`, starts `trello-mcp`, and registers
`trello` in the agent's `mcp.json`. Re-running is safe.

| Option | Effect |
|---|---|
| `--boards "A,B"` | Allow only these boards (names or ids) |
| `--all-boards` | Allow every board the token can see |
| `--new-token` | Prompt for a new key and token instead of reusing the stored ones |

Remove it with `deploy/trello/teardown.sh`, then revoke the token in Trello under
**Settings > Applications**.
