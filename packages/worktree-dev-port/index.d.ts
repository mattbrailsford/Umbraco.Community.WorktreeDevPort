export declare const CONFIG_KEY: string;
export declare const DEFAULT_BASE_PORT: number;
export declare const DEFAULT_RANGE_SIZE: number;

/**
 * Returns the port already assigned to this worktree. Throws if nothing has
 * assigned one yet.
 */
export declare function getPort(): number;

/**
 * Returns the port already assigned to this worktree, or picks the first free
 * port in range and saves it so future calls (and other tools) reuse the same one.
 */
export declare function getOrAssignPort(basePort?: number, rangeSize?: number): Promise<number>;
