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

🧪 Pre-release (`0.1.1`). npm published manually to bootstrap trusted publishing; NuGet not yet published.

## Releasing

A GitHub Actions workflow (`.github/workflows/release.yml`) publishes both packages whenever a GitHub Release is published. It uses **Trusted Publishing** (OIDC) for both registries — no long-lived API keys or tokens stored in this repo.

One-time setup before the first release:

- **npm — the very first publish must be manual.** npm's OIDC trusted publishing can't create a brand-new package, only publish new versions of one that already exists. So before the workflow can help:
  1. `cd packages/worktree-dev-port && npm login && npm publish --access public` from your own machine, once.
  2. Then go to the package's Settings → Trusted Publisher on npmjs.com and add:
     - Organization or user: `mattbrailsford`
     - Repository: `Umbraco.Community.WorktreeDevPort`
     - Workflow filename: `release.yml`
  3. Every release after that publishes through the workflow via OIDC — no more manual `npm publish`.
- **NuGet** doesn't have this limitation — a Trusted Publishing policy is tied to your account/org, not to a package that already exists, so it covers the first publish too. On nuget.org, open your username menu → Trusted Publishing → add a policy:
  - Repository Owner: `mattbrailsford`
  - Repository: `Umbraco.Community.WorktreeDevPort`
  - Workflow File: `release.yml`
- Optionally add a `NUGET_USER` repo secret set to your nuget.org profile name (not your email — this isn't a credential, just avoids hardcoding the username in the workflow).

To cut a release:

1. Bump `packages/worktree-dev-port/package.json`'s `version` (the .NET package's version comes from `version.json` + Nerdbank.GitVersioning automatically).
2. Commit and push to `main`.
3. Create a GitHub Release with a tag (e.g. `v0.1.0`) and publish it — this triggers the workflow.

## License

MIT — see [LICENSE](LICENSE).
