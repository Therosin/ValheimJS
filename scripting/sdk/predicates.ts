/**
 * Composable boolean predicates. A predicate is re-evaluated by the observation layer once per
 * sample, so the temporal combinators below count samples, not wall-clock time: with a 250 ms
 * observer, `delay(fn, 4)` means "true after four consecutive true samples (~1 s)".
 *
 * Layer them: compose inputs, stabilise noisy ones, then observe the result as a named condition
 * and trigger behaviour from its `:Enter`/`:Exit` events.
 */
export type Predicate = () => boolean;

/** True only when every predicate is true. An empty list is true. */
export function all(...predicates: Predicate[]): Predicate {
    return () => predicates.every(predicate => predicate());
}

/** True when at least one predicate is true. An empty list is false. */
export function any(...predicates: Predicate[]): Predicate {
    return () => predicates.some(predicate => predicate());
}

/** Inverts a predicate. */
export function not(predicate: Predicate): Predicate {
    return () => !predicate();
}

/** Alias used by LostMoon's predicate library; same as {@link not}. */
export const never = not;

/** True only on the first sample where the predicate becomes true, then false forever. */
export function once(predicate: Predicate): Predicate {
    let fired = false;
    return () => {
        if (fired) {
            return false;
        }

        fired = predicate();
        return fired;
    };
}

/** Flips only after `rise` consecutive true samples or `fall` consecutive false samples. */
export function stable(predicate: Predicate, rise = 1, fall = rise): Predicate {
    let current = false;
    let rising = 0;
    let falling = 0;
    return () => {
        const next = predicate();
        if (next === current) {
            rising = 0;
            falling = 0;
            return current;
        }

        if (next) {
            rising += 1;
            falling = 0;
            if (rising >= Math.max(1, rise)) {
                current = true;
                rising = 0;
            }
        } else {
            falling += 1;
            rising = 0;
            if (falling >= Math.max(1, fall)) {
                current = false;
                falling = 0;
            }
        }

        return current;
    };
}

/** True after `samples` consecutive true samples; a false sample resets the count. */
export function delay(predicate: Predicate, samples: number): Predicate {
    const required = Math.max(1, Math.floor(samples));
    let count = 0;
    return () => {
        if (!predicate()) {
            count = 0;
            return false;
        }

        count += 1;
        return count >= required;
    };
}

/** Passes at most once every `samples` samples while the predicate stays true. */
export function cooldown(predicate: Predicate, samples: number): Predicate {
    let remaining = 0;
    return () => {
        if (remaining > 0) {
            remaining -= 1;
            return false;
        }

        if (!predicate()) {
            return false;
        }

        remaining = Math.max(1, Math.floor(samples));
        return true;
    };
}

/** Convenience namespace for callers that prefer `predicates.all(...)`. */
export const predicates = { all, any, not, never, once, stable, delay, cooldown };
