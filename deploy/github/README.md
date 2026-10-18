# GitHub MCP server (docker compose)

Connects RockBot to GitHub's hosted MCP server (`https://api.githubcopilot.com/mcp/`) in
**read-only** mode, with the `repos`, `issues`, `pull_requests` and `actions` toolsets.
There's no container to run: the agent's MCP bridge connects directly and sends the token
as a bearer header. `mcp.json` holds only `${GITHUB_MCP_TOKEN}`; the value comes from the
agent's environment.

Read-only is enforced twice: by the `X-MCP-Readonly: true` header, so write tools aren't
offered at all, and by the token's own permissions.

## 1. Create a fine-grained token

<https://github.com/settings/personal-access-tokens/new>

- **Resource owner:** your account (or an org you belong to).
- **Repository access:** *Only select repositories* is safest; *All repositories* also works.
- **Repository permissions** (all **Read-only**): Contents, Issues, Pull requests, Actions,
  and Metadata (added automatically).
- **Expiration:** your choice. Re-run setup with `--new-token` when it expires.

This is separate from `GITHUB_TOKEN`, which the Copilot LLM provider uses.

## 2. Run setup

```bash
deploy/github/setup.sh            # prompts for the token
```

It checks the token against the GitHub API and the MCP endpoint, stores it as
`GITHUB_MCP_TOKEN` in `deploy/docker-compose/.env`, recreates the agent if needed, and
registers `github` in the agent's `mcp.json`. Re-running is safe.

| Option | Effect |
|---|---|
| `--new-token` | Prompt for a new token instead of reusing the stored one |
| `--toolsets a,b` | Change the toolsets (default `repos,issues,pull_requests,actions`) |

Remove it with `deploy/github/teardown.sh`, then revoke the token on GitHub.
