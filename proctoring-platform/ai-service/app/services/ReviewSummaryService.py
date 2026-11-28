

CLEARED_CLASSIFICATIONS = frozenset({"likely_benign_other"})


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
