
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
