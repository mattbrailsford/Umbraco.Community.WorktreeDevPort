# Umbraco.Community.WorktreeDevPort

Gives every git worktree its own stable local dev port — discoverable by any tool with a plain `git config` call. No named pipe, no Unix socket, no discovery endpoint to run and query.

This started as a fix for a real problem: running an Umbraco demo site from several feature-branch worktrees at once, where each one needs its own port, and both a human and an AI agent need an easy way to find out which port belongs to which worktree.

## Packages

| Package | For | What it does |
|---|---|---|
| [`Umbraco.Community.WorktreeDevPort`](src/Umbraco.Community.WorktreeDevPort) | .NET / the Umbraco site | Picks a free port on first run in a worktree, saves it to that worktree's git config, and makes Kestrel listen on it every time after. |
| [`worktree-dev-port`](packages/worktree-dev-port) | Node.js tooling (client generators, scripts, CI) | Reads (or assigns) the same port from the same git config, so it always agrees with the .NET side. |

## Why git config, not a file, a hash, or a pipe

- **A file in the repo or a socket in `/tmp`** — leaves something behind to clean up, and can go stale after a crash.
- **A port number derived from the branch name (a hash)** — needs no storage at all, but two different branch names can land on the same number by chance. Fine for one team's own repos; risky once it's a public tool other people's branch names run through.
- **`git config --worktree`** — no files in your project, no sockets, no collisions (a free port is actually checked before being assigned), and cleanup is automatic: removing the worktree removes the saved port with it.

## Quick start

```bash
dotnet add package Umbraco.Community.WorktreeDevPort
```

Start the site in Development. It'll pick a port once and reuse it from then on. Any other tool can ask for it:

```bash
git config --worktree --get worktreedevport.port
```

Or from Node:

```js
import { getPort } from "worktree-dev-port";
const port = getPort();
```

## Status

🧪 Pre-release (`0.1.0-alpha`). Built and working locally; not yet published to NuGet or npm.

## Releasing

A GitHub Actions workflow (`.github/workflows/release.yml`) publishes both packages whenever a GitHub Release is published. Before publishing, it needs two repo secrets:

- `NUGET_API_KEY` — a nuget.org API key scoped to this package
- `NPM_TOKEN` — an npm automation token with publish rights

Add them under **Settings → Secrets and variables → Actions**, or with `gh secret set NUGET_API_KEY` / `gh secret set NPM_TOKEN`.

To cut a release:

1. Bump `packages/worktree-dev-port/package.json`'s `version` (the .NET package's version comes from `version.json` + Nerdbank.GitVersioning automatically).
2. Commit and push to `main`.
3. Create a GitHub Release with a tag (e.g. `v0.1.0`) and publish it — this triggers the workflow.

## License

MIT — see [LICENSE](LICENSE).
