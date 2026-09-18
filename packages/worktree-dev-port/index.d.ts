export declare const CONFIG_KEY: string;
export declare const DEFAULT_BASE_PORT: number;
export declare const DEFAULT_RANGE_SIZE: number;
export declare const DEFAULT_MAIN_WORKTREE_PORT: number;

/**
 * Returns the port already assigned to this worktree. Throws if nothing has
 * assigned one yet.
 */
export declare function getPort(): number;

/**
 * Returns the port already assigned to this worktree, or picks one and saves it so future
 * calls (and other tools) reuse the same one. The main checkout gets `mainWorktreePort` when
 * it's free; every worktree gets the first free port from `basePort` up, skipping
 * `mainWorktreePort` so it stays reserved. Pass `null` for `mainWorktreePort` to disable this.
 */
export declare function getOrAssignPort(basePort?: number, rangeSize?: number, mainWorktreePort?: number | null): Promise<number>;
