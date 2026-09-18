# Umbraco.Community.WorktreeDevPort

Gives every git worktree of an Umbraco site its own stable local dev port, discoverable by any tool with a plain `git config` call — no named pipe, no Unix socket, no `/site-address` endpoint to query.

## Install

```bash
dotnet add package Umbraco.Community.WorktreeDevPort
```

That's it — the composer is picked up automatically by Umbraco and only activates when `ASPNETCORE_ENVIRONMENT=Development`.

## How it works

- The first time the site starts in a given worktree, it picks a free port in `44300`–`44399` and saves it with `git config --worktree wdp.port <port>`.
- That value lives in the worktree's own git config (`.git/config.worktree` or `.git/worktrees/<name>/config.worktree`) — never in a tracked file, never in `/tmp`.
- Every later run in that same worktree reuses the same port.
- Removing the worktree (`git worktree remove`) deletes the saved port with it. Nothing to clean up by hand.
- Any other tool — a client generator script, an AI agent, a teammate — gets the same port by running `git config --worktree --get wdp.port`. No need to ask the running site what port it's on.

See [worktree-dev-port](../../packages/worktree-dev-port) for the matching Node.js helper.

## Configuration

Optional, in `appsettings.Development.json`:

```json
{
  "WorktreeDevPort": {
    "BasePort": 44300,
    "RangeSize": 100
  }
}
```

If `ASPNETCORE_URLS` (or `urls`) is already set, this package leaves it alone.

## License

MIT — see [LICENSE](https://github.com/mattbrailsford/Umbraco.Community.WorktreeDevPort/blob/main/LICENSE).
