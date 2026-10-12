import { test, describe, after, beforeEach, afterEach } from "node:test";
import assert from "node:assert/strict";
import { execFile, execFileSync } from "node:child_process";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { getOrAssignPort, getPort, GIT_ALIAS_NAME, LOCK_FILE_NAME, PORT_FILE_NAME } from "../index.js";

// Shared with the .NET package's tests, so both sides agree on what's saved.
const savedPortCases = JSON.parse(
    fs.readFileSync(fileURLToPath(new URL("../../../tests/shared/saved-port-cases.json", import.meta.url)), "utf-8")
);

const testsRoot = fs.mkdtempSync(path.join(os.tmpdir(), "wdp-node-tests-"));

// Keep the developer's own git config (signing, hooks, default branch) out of every git call,
// including the ones the package makes, and stop git finding a repo above the temp folder.
const emptyConfig = path.join(testsRoot, "empty.gitconfig");
fs.writeFileSync(emptyConfig, "");
process.env.GIT_CONFIG_GLOBAL = emptyConfig;
process.env.GIT_CONFIG_NOSYSTEM = "1";
process.env.GIT_CEILING_DIRECTORIES = testsRoot;

// A random block of real ports per run, so a busy machine is unlikely to get in the way.
const BASE_PORT = 46000 + Math.floor(Math.random() * 100) * 20;
const RANGE_SIZE = 10;
const MAIN_PORT = BASE_PORT + 5;

const originalCwd = process.cwd();
let root;
let mainCheckout;
let servers;

function git(cwd, ...args) {
    return execFileSync("git", args, { cwd, encoding: "utf-8", stdio: ["ignore", "pipe", "pipe"] }).trim();
}

function addWorktree(name) {
    const worktree = path.join(root, name);
    git(mainCheckout, "worktree", "add", "-q", "--detach", worktree);
    return worktree;
}

function portFile(checkout) {
    return path.resolve(checkout, git(checkout, "rev-parse", "--git-path", PORT_FILE_NAME));
}

function lockFile() {
    return path.join(path.resolve(mainCheckout, git(mainCheckout, "rev-parse", "--git-common-dir")), LOCK_FILE_NAME);
}

function assignIn(checkout, mainPort = MAIN_PORT, basePort = BASE_PORT, rangeSize = RANGE_SIZE) {
    process.chdir(checkout);
    return getOrAssignPort(basePort, rangeSize, mainPort);
}

async function occupy(...ports) {
    for (const port of ports) {
        const server = net.createServer();
        await new Promise((resolve, reject) => {
            server.once("error", reject);
            server.listen(port, "127.0.0.1", resolve);
        });
        servers.push(server);
    }
}

beforeEach(() => {
    servers = [];
    root = fs.mkdtempSync(path.join(testsRoot, "repo-"));
    mainCheckout = path.join(root, "main");
    fs.mkdirSync(mainCheckout);
    git(mainCheckout, "init", "-q");
    git(mainCheckout, "-c", "user.name=wdp", "-c", "user.email=wdp@example.com", "commit", "-q", "--allow-empty", "-m", "init");
});

afterEach(async () => {
    process.chdir(originalCwd);
    await Promise.all(servers.map((server) => new Promise((resolve) => server.close(resolve))));
    fs.rmSync(root, { recursive: true, force: true });
});

after(() => fs.rmSync(testsRoot, { recursive: true, force: true }));

describe("getOrAssignPort", () => {
    test("main checkout gets the reserved port when free", async () => {
        assert.equal(await assignIn(mainCheckout), MAIN_PORT);
        assert.equal(fs.readFileSync(portFile(mainCheckout), "utf-8"), `${MAIN_PORT}\n`);
    });

    test("main checkout falls back to the pool when the reserved port is busy", async () => {
        await occupy(MAIN_PORT);
        assert.equal(await assignIn(mainCheckout), BASE_PORT);
    });

    for (const mainPort of [null, 0]) {
        test(`main checkout uses the pool when the reserved port is ${mainPort}`, async () => {
            assert.equal(await assignIn(mainCheckout, mainPort), BASE_PORT);
        });
    }

    test("linked worktree never gets the reserved port", async () => {
        assert.equal(await assignIn(addWorktree("wt")), BASE_PORT);
    });

    test("new worktree does not inherit the main checkout's port", async () => {
        assert.equal(await assignIn(mainCheckout), MAIN_PORT);
        const worktree = addWorktree("wt");

        assert.equal(fs.existsSync(portFile(worktree)), false);
        assert.equal(await assignIn(worktree), BASE_PORT);
    });

    test("worktrees skip ports other worktrees have saved, even when free", async () => {
        const first = addWorktree("wt1");
        const second = addWorktree("wt2");

        assert.equal(await assignIn(first), BASE_PORT);
        assert.equal(await assignIn(second), BASE_PORT + 1);
    });

    test("a saved port is reused, even when busy", async () => {
        fs.writeFileSync(portFile(mainCheckout), `${BASE_PORT + 3}\n`);
        await occupy(BASE_PORT + 3);

        assert.equal(await assignIn(mainCheckout), BASE_PORT + 3);
    });

    for (const { text, port } of savedPortCases) {
        test(`saved text ${JSON.stringify(text)} is ${port === null ? "ignored" : `read as ${port}`}`, async () => {
            const file = portFile(mainCheckout);
            fs.writeFileSync(file, text);

            assert.equal(await assignIn(mainCheckout, null), port ?? BASE_PORT);
            if (port === null) assert.equal(fs.readFileSync(file, "utf-8"), `${BASE_PORT}\n`);
        });
    }

    test("a junk port saved by another worktree does not block anything", async () => {
        fs.writeFileSync(portFile(addWorktree("wt1")), "junk");
        assert.equal(await assignIn(addWorktree("wt2")), BASE_PORT);
    });

    test("a full range throws a clear error", async () => {
        await occupy(BASE_PORT, BASE_PORT + 1);
        await assert.rejects(assignIn(mainCheckout, null, BASE_PORT, 2), { message: new RegExp(`${BASE_PORT}-${BASE_PORT + 1}`) });
    });

    for (const [basePort, rangeSize] of [[0, 10], [50000, 0], [65530, 10]]) {
        test(`range ${basePort}+${rangeSize} is rejected`, async () => {
            await assert.rejects(assignIn(mainCheckout, MAIN_PORT, basePort, rangeSize), RangeError);
        });
    }

    test("outside a git repo it still returns a port and saves nothing", async () => {
        const folder = path.join(root, "plain");
        fs.mkdirSync(folder);

        assert.equal(await assignIn(folder), MAIN_PORT);
        assert.deepEqual(fs.readdirSync(folder), []);
    });

    test("worktrees starting at the same moment get different ports", async () => {
        const worktrees = Array.from({ length: 6 }, (_, i) => addWorktree(`wt${i + 1}`));
        const script = `import(${JSON.stringify(new URL("../index.js", import.meta.url).href)})
            .then((m) => m.getOrAssignPort(${BASE_PORT}, ${RANGE_SIZE}, ${MAIN_PORT}))
            .then((port) => process.stdout.write(String(port)));`;

        // Separate processes, like separate sites starting up, so they really do overlap.
        const ports = await Promise.all(
            worktrees.map(
                (cwd) =>
                    new Promise((resolve, reject) =>
                        execFile(process.execPath, ["--input-type=module", "-e", script], { cwd }, (error, stdout) =>
                            error ? reject(error) : resolve(Number(stdout))
                        )
                    )
            )
        );

        assert.equal(new Set(ports).size, worktrees.length);
    });

    test("the lock is released afterwards", async () => {
        await assignIn(mainCheckout);
        assert.equal(fs.existsSync(lockFile()), false);
    });

    test("a lock left by a crashed process is ignored", async () => {
        const file = lockFile();
        fs.writeFileSync(file, "");
        const fiveMinutesAgo = new Date(Date.now() - 5 * 60_000);
        fs.utimesSync(file, fiveMinutesAgo, fiveMinutesAgo);

        assert.equal(await assignIn(mainCheckout), MAIN_PORT);
    });

    test("git alias prints the saved port in every worktree", async () => {
        const worktree = addWorktree("wt");
        const mainPort = await assignIn(mainCheckout);
        const worktreePort = await assignIn(worktree);

        assert.equal(git(mainCheckout, GIT_ALIAS_NAME), `${mainPort}`);
        assert.equal(git(worktree, GIT_ALIAS_NAME), `${worktreePort}`);
    });

    test("git alias is written once", async () => {
        await assignIn(mainCheckout);
        await assignIn(addWorktree("wt"));

        assert.equal(git(mainCheckout, "config", "--get-all", `alias.${GIT_ALIAS_NAME}`).split("\n").length, 1);
    });
});

describe("getPort", () => {
    test("throws when this worktree has no port yet", () => {
        process.chdir(mainCheckout);
        assert.throws(() => getPort(), /No port found/);
    });

    test("returns the port getOrAssignPort saved", async () => {
        const port = await assignIn(mainCheckout);
        assert.equal(getPort(), port);
    });
});
