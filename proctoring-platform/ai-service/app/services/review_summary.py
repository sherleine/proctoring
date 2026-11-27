"""Turns the full AnalysisResult payload into the single valid/invalid + reason verdict someone skimming a
demo (or a future reviewer dashboard) actually wants, without reading observations/aiReview by hand.

This is a convenience derived from the contract, not part of it: the full payload (observations, aiReview,
findings, ...) stays the source of truth and is always returned alongside this - nothing here is persisted
or sent to the backend. "valid" never means "cleared of all suspicion"; it means "nothing here needs a human
to look closer right now". Never silently clears a Tier 1 review-category flag: when Tier 2 couldn't run or
couldn't decide, this stays on the cautious side (invalid) rather than guessing benign.
"""

CLEARED_CLASSIFICATIONS = frozenset({"likely_reading_question", "likely_benign_other"})


def summarize(payload: dict) -> dict:
    """payload: an AnalysisResult.to_payload() dict (or the equivalent JSON). Returns {"valid": bool, "reason": str}."""
    flags = [o["observationType"] for o in payload.get("observations", []) if o.get("category") == "review"]
    ai_review = payload.get("aiReview") or {}

    if not flags:
        return {"valid": True, "reason": "No overlap, extra voices or external-assistance indicators were detected."}

    counts: dict[str, int] = {}
    for flag in flags:
        counts[flag] = counts.get(flag, 0) + 1
    flagged_by = "Flagged by Tier 1: " + ", ".join(f"{f} (x{n})" if n > 1 else f for f, n in counts.items()) + "."

    if ai_review.get("attempted") and ai_review.get("modelValid"):
        rationale = ai_review.get("rationale") or ""
        if ai_review.get("classification") in CLEARED_CLASSIFICATIONS:
            return {"valid": True, "reason": f"{flagged_by} AI review cleared it: {rationale}".strip()}
        return {"valid": False, "reason": f"{flagged_by} AI review: {rationale}".strip()}

    if ai_review.get("attempted"):
        return {"valid": False, "reason": f"{flagged_by} AI review did not complete, so the flag stands "
                                          f"unconfirmed - needs a human look."}

    return {"valid": False, "reason": f"{flagged_by} Tier 2 review was not run ({ai_review.get('reason', 'unknown reason')})."}
