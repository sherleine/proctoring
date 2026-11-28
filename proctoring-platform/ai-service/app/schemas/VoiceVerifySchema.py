"""Response model for POST /voice/verify."""
from pydantic import BaseModel, Field


class VoiceVerifyResponse(BaseModel):
    valid: bool
    reason: str
    result: dict | None = Field(None, description="The full AnalysisResult; only present when verbose=true")
