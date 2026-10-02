# Umbraco.Community.WorktreeDevPort

Gives every git worktree of an Umbraco site its own stable local dev port, discoverable by any tool with a plain `git wdp-port` call — no named pipe, no Unix socket, no `/site-address` endpoint to query.

## Install

```bash
dotnet add package Umbraco.Community.WorktreeDevPort
```

That's it — the composer is picked up automatically by Umbraco and only activates when `ASPNETCORE_ENVIRONMENT=Development`.

## How it works

- The first time the site starts in a given worktree, it picks a free port and saves it in a `wdp-port` file in that worktree's own git folder. The **main checkout** gets `44355` when it's free; every **linked worktree** gets the first free port from `44300` up instead, so `44355` stays reserved.
- That file lives at `.git/wdp-port` (main checkout) or `.git/worktrees/<name>/wdp-port` (linked worktree) — never in a tracked file, never in `/tmp`.
- Every later run in that same worktree reuses the same port.
- `git worktree add` never copies that file, so a new worktree always starts with no port and picks its own. New ports also skip any port another worktree already has saved.
- Removing the worktree (`git worktree remove`) deletes the saved port with it. Nothing to clean up by hand.
- Any other tool — a client generator script, an AI agent, a teammate — gets the same port by running `git wdp-port`, a git alias the package adds to the repo's shared config. No need to ask the running site what port it's on. No output means this worktree hasn't started the site yet.

See [worktree-dev-port](../../packages/worktree-dev-port) for the matching Node.js helper.

## Configuration

Optional, in `appsettings.Development.json`:

```json
{
  "WorktreeDevPort": {
    "BasePort": 44300,
    "RangeSize": 100,
    "MainWorktreePort": 44355
  }
}
```

Set `MainWorktreePort` to `null` to disable the fixed-port reservation and always use the pool.

If `ASPNETCORE_URLS` (or `urls`) is already set to a real fixed port, this package leaves it alone. A `:0` port (the old "ask the OS for a random port" trick) is treated the same as no URL being set at all — this package's whole job is to replace that.

## License

MIT — see [LICENSE](https://github.com/mattbrailsford/Umbraco.Community.WorktreeDevPort/blob/main/LICENSE).
