import type {
    UtilityAdjustments,
    UtilityFeedbackContext,
    UtilityFeedbackRule
} from "./types";

export function collectFeedback(
    rules: readonly UtilityFeedbackRule[],
    context: UtilityFeedbackContext,
    log?: (message: string) => void
): UtilityAdjustments {
    const adjustments: Record<string, number> = {};
    for (const rule of rules) {
        let result: UtilityAdjustments;
        try {
            result = rule.adjust(context);
        } catch (error) {
            log?.(`utility feedback '${rule.id}' failed: ${String(error)}`);
            continue;
        }

        for (const [key, value] of Object.entries(result)) {
            if (typeof value === "number" && Number.isFinite(value)) {
                adjustments[key] = (adjustments[key] ?? 0) + value;
            }
        }
    }

    return adjustments;
}
