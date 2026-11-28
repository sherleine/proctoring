"""The LLM client: an OpenAI-compatible server (vLLM / LiteLLM). One instance per process - get_llm_client()."""
import json
import logging
from functools import lru_cache
from typing import Protocol

import httpx

from app.config import get_settings

log = logging.getLogger(__name__)


class LlmClient(Protocol):
    def complete(self, prompt: str, schema: dict) -> dict:
        """Returns the model's parsed JSON. Raises on any failure (network, timeout, bad JSON) - review_clip
        turns that into a safe fallback rather than letting it propagate."""
        ...


class TruncatedResponseError(RuntimeError):
    """The LLM stopped because it reached max_tokens - the JSON is incomplete and must not be trusted."""


class VllmClient:
    """Talks to an LLM through an OpenAI-compatible server (vLLM / LiteLLM). The server is asked for a JSON object
    and given the schema in the prompt; review_clip then validates the answer against that schema locally."""

    def __init__(self, base_url: str, model: str, timeout_seconds: float = 20.0, api_key: str = "",
                 max_tokens: int = 800, retries: int = 1):
        self.base_url = base_url.rstrip("/")
        self.api_key = api_key
        self.max_tokens = max_tokens
        self.retries = retries
        self.model = model
        self.timeout_seconds = timeout_seconds

    @property
    def model_id(self) -> str:
        return f"vllm:{self.model}"

    def complete(self, prompt: str, schema: dict) -> dict:
        """One retry, only for a runaway answer: with JSON-schema decoding some models occasionally keep
        emitting whitespace inside the object until they hit the token cap (seen: ~19,600 tokens / 174 s for an
        answer that normally takes ~150 tokens / 1.3 s). max_tokens bounds that to seconds; a cut-off answer is
        never parsed as if it were complete. The retry uses a small temperature to step off the stuck path."""
        last_error: Exception | None = None
        for attempt in range(1 + self.retries):
            try:
                return self._complete_once(prompt, schema, temperature=0.0 if attempt == 0 else 0.3)
            except TruncatedResponseError as exc:
                last_error = exc
                log.warning("LLM answer was cut off at max_tokens; retrying" if attempt < self.retries else
                            "LLM answer was cut off at max_tokens", extra={"attempt": attempt + 1})
        raise last_error  # type: ignore[misc]

    def _complete_once(self, prompt: str, schema: dict, temperature: float) -> dict:
        body = {
            "model": self.model,
            "messages": [{"role": "user", "content": f"{prompt}\nJSON schema of the answer: "
                                                     f"{json.dumps(schema, separators=(',', ':'))}"}],
            "temperature": temperature,
            "max_tokens": self.max_tokens,
            # json_object, not strict json_schema: on the deployed model, schema-constrained decoding regularly
            # looped on whitespace until max_tokens (4/5 first attempts), json_object answered 5/5 in ~1 s.
            # The answer is still checked against the schema locally (validate_review), so nothing is trusted blind.
            "response_format": {"type": "json_object"},
        }
        headers = {"Authorization": f"Bearer {self.api_key}"} if self.api_key else {}
        response = httpx.post(f"{self.base_url}/chat/completions", json=body, headers=headers,
                              timeout=self.timeout_seconds)
        response.raise_for_status()
        choice = response.json()["choices"][0]
        if choice.get("finish_reason") == "length":
            raise TruncatedResponseError(f"answer hit max_tokens={self.max_tokens}")
        return json.loads(choice["message"]["content"])


@lru_cache
def get_llm_client() -> VllmClient | None:
    """The one shared LLM client, or None when Tier 2 escalation is off (ESCALATION_ENABLED=false)."""
    settings = get_settings()
    if not settings.escalation_enabled:
        return None
    return VllmClient(settings.llm_base_url, settings.llm_model, settings.llm_timeout_seconds,
                      api_key=settings.llm_api_key.get_secret_value() if settings.llm_api_key else "")
