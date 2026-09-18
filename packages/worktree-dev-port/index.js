import { execSync } from "node:child_process";
import net from "node:net";

export const CONFIG_KEY = "wdp.port";
export const DEFAULT_BASE_PORT = 44300;
export const DEFAULT_RANGE_SIZE = 100;

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
 * Returns the port already assigned to this worktree, or picks the first free
 * port in range and saves it so future calls (and other tools) reuse the same one.
 */
export async function getOrAssignPort(basePort = DEFAULT_BASE_PORT, rangeSize = DEFAULT_RANGE_SIZE) {
    // Enables per-worktree config files; harmless if already set. This itself lives in
    // the shared .git/config, so it only ever needs to succeed once per clone.
    runGit("config extensions.worktreeConfig true");

    const existing = runGit(`config --worktree --get ${CONFIG_KEY}`);
    if (existing) return parseInt(existing, 10);

    for (let port = basePort; port < basePort + rangeSize; port++) {
        if (await isPortFree(port)) {
            runGit(`config --worktree ${CONFIG_KEY} ${port}`);
            return port;
        }
    }

    throw new Error(`No free port found in range ${basePort}-${basePort + rangeSize - 1}.`);
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
