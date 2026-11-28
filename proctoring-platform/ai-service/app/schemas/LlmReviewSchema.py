"""JSON schema the Tier 2 LLM's answer must match (checked locally in LlmReviewService.validate_review)."""

REVIEW_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "required": ["classification", "attributedTo", "confidence", "rationale", "recommendedReviewPriority"],
    "properties": {
        "classification": {
            "enum": ["likely_benign_other", "possible_external_assistance", "inconclusive"],
        },
        "attributedTo": {"enum": ["main", "other", "mixed", "unknown"]},
        "confidence": {"type": "number", "minimum": 0, "maximum": 1},
        "rationale": {"type": "string", "maxLength": 500},
        "recommendedReviewPriority": {"enum": ["low", "normal", "high"]},
    },
}
