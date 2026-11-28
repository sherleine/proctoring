"""Prompt template for the Tier 2 LLM review of one clip (sent by LlmReviewService.review_clip)."""
import json


def build_prompt(tagged_segments: list[dict], observation_types: list[str], diarization_summary: dict) -> str:
    lines = [
        "You are assisting exam proctoring review. You decide nothing automatically - a human reviewer makes "
        "the final call. Judge only from the evidence below; do not assume anything it does not say.",
        "Rule-based flags already raised for this clip: "
        + (", ".join(observation_types) if observation_types
           else "none - this clip was reviewed as one voice, but a conversation can span separate clips") + ".",
        f"Diarization summary: {json.dumps(diarization_summary, separators=(',', ':'))}",
        "Speaker-tagged transcript (role describes the audio, never an identity - main is the clip's "
                 "primary voice, other is a clearly different voice, background is a much quieter voice, "
        "uncertain is unresolved):",
    ]
    for seg in tagged_segments:
        lines.append(f"  [{seg['start']:.1f}-{seg['end']:.1f}] ({seg['role']}): {seg['text']}")
    lines.append(
        "Write the rationale for a human reviewer in plain language, 1-3 sentences: say what was heard (who "
        "spoke - the candidate, a second voice, or a quieter background voice - and what kind of speech it was, "
        "e.g. a background conversation, someone giving instructions, the candidate thinking aloud), then why "
        "that is or is not a sign of assistance with the exam. Quote short phrases from the transcript as evidence. Do not use the internal role names (main/other/background/uncertain) or "
        "explain what the speech is not - describe what it is.")
    lines.append("Respond with JSON only, matching the schema you were given.")
    return "\n".join(lines)
