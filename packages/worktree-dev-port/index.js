import { execSync } from "node:child_process";
import net from "node:net";
import path from "node:path";

export const CONFIG_KEY = "wdp.port";
export const DEFAULT_BASE_PORT = 44300;
export const DEFAULT_RANGE_SIZE = 100;

/**
 * The port reserved for the main checkout (not a linked worktree), used when free.
 * Pass `null` to disable this and always use the auto-assigned pool.
 */
export const DEFAULT_MAIN_WORKTREE_PORT = 44355;

/**
 * Returns the port already assigned to this worktree. Throws if nothing has
 * assigned one yet — call getOrAssignPort() first, or start the app that owns it.
 */
export function getPort() {
    const value = runGit(`config --worktree --get ${CONFIG_KEY}`);
    if (!value) {
        throw new Error(
            `No port found for this worktree (git config ${CONFIG_KEY} is not set). ` +
                "Call getOrAssignPort() to assign one, or start the app that owns this worktree first."
        );
    }
    return parseInt(value, 10);
}

/**
 * Returns the port already assigned to this worktree, or picks one and saves it so future
 * calls (and other tools) reuse the same one. The main checkout gets `mainWorktreePort` when
 * it's free; every worktree gets the first free port from `basePort` up, skipping
 * `mainWorktreePort` so it stays reserved.
 */
export async function getOrAssignPort(basePort = DEFAULT_BASE_PORT, rangeSize = DEFAULT_RANGE_SIZE, mainWorktreePort = DEFAULT_MAIN_WORKTREE_PORT) {
    // Enables per-worktree config files; harmless if already set. This itself lives in
    // the shared .git/config, so it only ever needs to succeed once per clone.
    runGit("config extensions.worktreeConfig true");

    const existing = runGit(`config --worktree --get ${CONFIG_KEY}`);
    if (existing) return parseInt(existing, 10);

    if (mainWorktreePort && !isLinkedWorktree() && (await isPortFree(mainWorktreePort))) {
        runGit(`config --worktree ${CONFIG_KEY} ${mainWorktreePort}`);
        return mainWorktreePort;
    }

    for (let port = basePort; port < basePort + rangeSize; port++) {
        if (port === mainWorktreePort) continue;
        if (await isPortFree(port)) {
            runGit(`config --worktree ${CONFIG_KEY} ${port}`);
            return port;
        }
    }

    throw new Error(`No free port found in range ${basePort}-${basePort + rangeSize - 1}.`);
}

function isLinkedWorktree() {
    // For the main checkout, --git-dir and --git-common-dir are the same path. A linked
    // worktree has its own private --git-dir under the shared repo's .git/worktrees/<name>,
    // so the two differ. This avoids relying on "worktrees" appearing in the path, which the
    // repo's own folder name could also trigger by coincidence.
    const gitDir = runGit("rev-parse --git-dir");
    const commonDir = runGit("rev-parse --git-common-dir");

    // No git repo (or git unavailable) here at all -- treat like the main checkout
    // rather than misreading empty strings, matching runGit's own fail-safe-empty behavior.
    if (!gitDir || !commonDir) return false;

    return path.resolve(gitDir) !== path.resolve(commonDir);
}

function isPortFree(port) {
    return new Promise((resolve) => {
        const server = net.createServer();
        server.once("error", () => resolve(false));
        server.once("listening", () => server.close(() => resolve(true)));
        server.listen(port, "127.0.0.1");
    });
}

function runGit(args) {
    try {
        return execSync(`git ${args}`, { encoding: "utf-8" }).trim();
    } catch {
        return "";
    }
}
