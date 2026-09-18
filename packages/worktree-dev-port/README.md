# worktree-dev-port

Node-side helper for [Umbraco.Community.WorktreeDevPort](https://github.com/mattbrailsford/Umbraco.Community.WorktreeDevPort). Reads or assigns the local dev port stored in the current git worktree's own config — no pipe, socket, or running discovery endpoint required.

## Install

```bash
npm install worktree-dev-port
```

## Usage

```js
import { getPort, getOrAssignPort } from "worktree-dev-port";

// If something else (e.g. the paired NuGet package) already assigned a port:
const port = getPort();

// Or, to assign one yourself if nothing has yet:
const port = await getOrAssignPort();
```

See the [root README](../../README.md) for how this fits with the .NET side.
