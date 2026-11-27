"""Question-context lookup: what the candidate was actually shown on screen during a clip.

Nothing upstream provides this yet - audio-event-metadata.v1.json has no question field, and the backend's
Session/AudioEvent domain has no notion of exam content at all (confirmed by reading both). QuestionContextProvider
is the integration seam for whenever that exists (likely an HTTP call to the exam platform, keyed by examId/
sessionId and the clip's time range); NullQuestionContextProvider is the safe default until then.

"Unavailable" is a normal, expected answer here, never an error: llm_review_service treats it as "nothing to
compare the speech against" and falls back to judging on content alone - it is never a reason to suppress a flag.
"""
from dataclasses import dataclass
from typing import Protocol


@dataclass(frozen=True)
class QuestionContext:
    text: str
    options: list[str] | None = None


class QuestionContextProvider(Protocol):
    def get(self, *, session_id: str, exam_id: str, start_at: str, end_at: str) -> QuestionContext | None:
        """start_at/end_at: ISO-8601 timestamps, matching audio-event-metadata's startedAt/endedAt.

        Returns None when no question is known for this session/time range (nothing displayed yet, a break
        screen, or - today - simply because this integration does not exist yet)."""
        ...


class NullQuestionContextProvider:
    """Always "unavailable". Use until the exam backend exposes the active question for a session + time range."""

    def get(self, *, session_id: str, exam_id: str, start_at: str, end_at: str) -> QuestionContext | None:
        return None
