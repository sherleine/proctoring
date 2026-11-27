
from app.schemas.analysis_result import Finding
from app.services.transcription_service import TranscriptionData
from app.services.vad_service import VadResult

CLIPPING_RATIO = 0.01  # >= 1 % of samples at full scale
VERY_QUIET_RMS_DBFS = -50.0


def analyse(vad: VadResult, transcription: TranscriptionData | None, rms_dbfs: float, peak_dbfs: float,
            clipped_ratio: float, gain_db: float) -> list[Finding]:
    findings: list[Finding] = []
    if not vad.verified:
        findings.append(Finding(
            type="NO_SPEECH_VERIFIED", severity="info",
            description="Server-side voice detection did not confirm speech in this clip. "
                        "This is not a concern by itself (e.g. a cough, noise or a short sound triggered recording).",
            start=None, end=None,
            evidence={"speechSeconds": vad.speech_seconds, "maxSpeechProbability": vad.max_probability,
                      "threshold": vad.threshold}))
    else:
        words = len(transcription.text.split()) if transcription else 0
        findings.append(Finding(
            type="CANDIDATE_SPEECH", severity="info",
            description=f"Speech detected for {vad.speech_seconds:.1f} s. The speaker is not identified, and speech "
                        "alone is not evidence of misconduct.",
            start=vad.segments[0][0], end=vad.segments[-1][1],
            evidence={"speechSeconds": vad.speech_seconds, "speechRatio": vad.speech_ratio,
                      "segmentCount": len(vad.segments), "meanSpeechProbability": vad.mean_speech_probability,
                      "transcribed": transcription is not None, "wordCount": words}))

    if clipped_ratio >= CLIPPING_RATIO:
        findings.append(Finding(
            type="UNUSUAL_AUDIO", severity="info",
            description="The recording is clipped (too loud); transcription quality may be reduced.",
            start=None, end=None, evidence={"clippedRatio": round(clipped_ratio, 4), "peakDbfs": peak_dbfs}))
    elif peak_dbfs > -120.0 and rms_dbfs < VERY_QUIET_RMS_DBFS:
        findings.append(Finding(
            type="UNUSUAL_AUDIO", severity="info",
            description="The recording is very quiet; speech detection and transcription may be less reliable.",
            start=None, end=None, evidence={"rmsDbfs": rms_dbfs, "peakDbfs": peak_dbfs, "appliedGainDb": gain_db}))
    return findings
