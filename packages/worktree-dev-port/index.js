import { execFileSync } from "node:child_process";
import fs from "node:fs";
import net from "node:net";
import path from "node:path";

/**
 * The file, inside each worktree's own git dir, that holds its port. `git worktree add` never
 * copies it into a new worktree, so a worktree can only ever see a port it assigned itself.
 */
export const PORT_FILE_NAME = "wdp-port";

/**
 * The git alias added to the repo's shared config, so any tool can read the current
 * worktree's port with `git wdp-port`.
 */
export const GIT_ALIAS_NAME = "wdp-port";

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
    const dirs = getGitDirs();
    const port = dirs ? readPortFile(path.join(dirs.gitDir, PORT_FILE_NAME)) : null;
    if (port === null) {
        throw new Error(
            "No port found for this worktree (`git wdp-port` is empty). " +
                "Call getOrAssignPort() to assign one, or start the app that owns this worktree first."
        );
    }
    return port;
}

/**
 * Returns the port already assigned to this worktree, or picks one and saves it so future
 * calls (and other tools) reuse the same one. The main checkout gets `mainWorktreePort` when
 * it's free; every worktree gets the first free port from `basePort` up, skipping
 * `mainWorktreePort` so it stays reserved, and skipping any port another worktree already has saved.
 */
export async function getOrAssignPort(basePort = DEFAULT_BASE_PORT, rangeSize = DEFAULT_RANGE_SIZE, mainWorktreePort = DEFAULT_MAIN_WORKTREE_PORT) {
    ensureGitAlias();

    const dirs = getGitDirs();
    const portFile = dirs ? path.join(dirs.gitDir, PORT_FILE_NAME) : null;

    const existing = portFile ? readPortFile(portFile) : null;
    if (existing !== null) return existing;

    const isLinked = dirs ? dirs.gitDir !== dirs.commonDir : false;
    const takenByOthers = dirs ? getPortsSavedByOtherWorktrees(dirs.gitDir, dirs.commonDir) : new Set();

    if (mainWorktreePort && !isLinked && (await isPortFree(mainWorktreePort))) {
        return savePort(portFile, mainWorktreePort);
    }

    for (let port = basePort; port < basePort + rangeSize; port++) {
        if (port === mainWorktreePort || takenByOthers.has(port)) continue;
        if (await isPortFree(port)) {
            return savePort(portFile, port);
        }
    }

    throw new Error(`No free port found in range ${basePort}-${basePort + rangeSize - 1}.`);
}

function savePort(portFile, port) {
    if (portFile) fs.writeFileSync(portFile, `${port}\n`);
    return port;
}

function readPortFile(file) {
    if (!fs.existsSync(file)) return null;
    const port = parseInt(fs.readFileSync(file, "utf-8").trim(), 10);
    return Number.isNaN(port) ? null : port;
}

function ensureGitAlias() {
    // Lives in the shared .git/config, so every worktree gets it, and it only needs
    // writing once per clone.
    const command = `!cat "$(git rev-parse --git-path ${PORT_FILE_NAME})" 2>/dev/null`;
    if (runGit("config", "--get", `alias.${GIT_ALIAS_NAME}`) !== command) {
        runGit("config", `alias.${GIT_ALIAS_NAME}`, command);
    }
}

function getGitDirs() {
    // For the main checkout, --git-dir and --git-common-dir are the same path. A linked
    // worktree has its own private --git-dir under the shared repo's .git/worktrees/<name>,
    // so the two differ. This avoids relying on "worktrees" appearing in the path, which the
    // repo's own folder name could also trigger by coincidence.
    const gitDir = runGit("rev-parse", "--path-format=absolute", "--git-dir");
    const commonDir = runGit("rev-parse", "--path-format=absolute", "--git-common-dir");

    // No git repo (or git unavailable) here at all -- treat like the main checkout
    // rather than misreading empty strings, matching runGit's own fail-safe-empty behavior.
    if (!gitDir || !commonDir) return null;

    return { gitDir: path.resolve(gitDir), commonDir: path.resolve(commonDir) };
}

/** The port saved by every other worktree of this repo (main checkout included). */
function getPortsSavedByOtherWorktrees(gitDir, commonDir) {
    const portFiles = [path.join(commonDir, PORT_FILE_NAME)];

    const worktreesDir = path.join(commonDir, "worktrees");
    if (fs.existsSync(worktreesDir)) {
        for (const entry of fs.readdirSync(worktreesDir, { withFileTypes: true })) {
            if (entry.isDirectory()) portFiles.push(path.join(worktreesDir, entry.name, PORT_FILE_NAME));
        }
    }

    const ownPortFile = path.join(gitDir, PORT_FILE_NAME);
    const ports = new Set();

    for (const file of portFiles) {
        if (file === ownPortFile) continue;
        const port = readPortFile(file);
        if (port !== null) ports.add(port);
    }

    return ports;
}

function isPortFree(port) {
    return new Promise((resolve) => {
        const server = net.createServer();
        server.once("error", () => resolve(false));
        server.once("listening", () => server.close(() => resolve(true)));
        server.listen(port, "127.0.0.1");
    });
}

function runGit(...args) {
    try {
        return execFileSync("git", args, { encoding: "utf-8", stdio: ["ignore", "pipe", "ignore"] }).trim();
    } catch {
        return "";
    }
}
