"""WAV validation/loading, storage access and level helpers (no models)."""
import hashlib

import numpy as np
import pytest

from app.core.errors import PermanentEventError
from app.schemas.audio_event import AudioRef
from app.services.audio_loader import AudioStore, decode_wav
from app.utils import audio_utils
from tests.helpers import INVALID_WAV, SR, corrupted_wav, put_audio, quiet_speech, silence, speech_samples, wav_bytes


def ref_for(key: str, data: bytes, **overrides) -> AudioRef:
    values = dict(storage="file", uri="file:///elsewhere/x.wav", sha256=hashlib.sha256(data).hexdigest(),
                  sizeBytes=len(data), contentType="audio/wav", key=key)
    values.update(overrides)
    return AudioRef(**values)


# ------------------------------------------------------------------ decoding

def test_valid_wav_is_loaded_as_float32_with_its_original_duration():
    samples = speech_samples()
    audio = decode_wav(wav_bytes(samples))
    assert audio.sample_rate == SR
    assert audio.samples.dtype == np.float32
    assert len(audio.samples) == len(samples)  # no resampling, no trimming
    assert audio.duration_seconds == pytest.approx(len(samples) / SR)
    assert np.max(np.abs(audio.samples - samples)) < 1e-4  # 16-bit round trip


@pytest.mark.parametrize("data,code", [
    (wav_bytes(np.zeros((SR, 2), dtype=np.float32)), "INVALID_AUDIO"),           # stereo
    (wav_bytes(silence(), sample_rate=44_100), "INVALID_AUDIO"),                  # wrong rate
    (wav_bytes(silence(), subtype="PCM_24"), "INVALID_AUDIO"),                    # 24-bit
    (wav_bytes(silence(), subtype="FLOAT"), "INVALID_AUDIO"),                     # float
    (INVALID_WAV, "INVALID_AUDIO"),                                               # not a WAV
    (corrupted_wav(), "INVALID_AUDIO"),                                           # damaged header
    (wav_bytes(np.zeros(0, dtype=np.float32)), "EMPTY_AUDIO"),                   # no samples
])
def test_audio_outside_the_upload_format_is_rejected(data, code):
    with pytest.raises(PermanentEventError) as err:
        decode_wav(data)
    assert err.value.code == code


def test_digital_silence_is_valid_audio():
    audio = decode_wav(wav_bytes(silence(1.0)))
    assert audio.duration_seconds == pytest.approx(1.0)


# ------------------------------------------------------------------ storage

def test_store_reads_the_referenced_file_without_changing_it(store_root):
    data = wav_bytes(speech_samples())
    _, key = put_audio(store_root, data)
    before = (store_root / key).stat().st_mtime_ns
    assert AudioStore(store_root).read(ref_for(key, data)) == data
    assert (store_root / key).read_bytes() == data
    assert (store_root / key).stat().st_mtime_ns == before


@pytest.mark.parametrize("key", [
    "../outside.wav",
    "ses_AAAAAAAAAAAAAAAAAAAAAAAA/../../etc/passwd",
    "C:/Windows/win.ini",
    "/etc/passwd",
    "ses_AAAAAAAAAAAAAAAAAAAAAAAA/" + "a" * 32 + ".txt",
    "ses_short/" + "a" * 32 + ".wav",
    "",
])
def test_keys_outside_the_storage_key_format_are_rejected(store_root, key):
    with pytest.raises(PermanentEventError) as err:
        AudioStore(store_root).resolve(key)
    assert err.value.code == "INVALID_AUDIO_REFERENCE"


def test_missing_file_is_a_permanent_error_without_the_path(store_root):
    data = wav_bytes(silence())
    key = "ses_ABCDEFGHJKMNPQRSTVWXYZ0123/" + "b" * 32 + ".wav"
    with pytest.raises(PermanentEventError) as err:
        AudioStore(store_root).read(ref_for(key, data))
    assert err.value.code == "AUDIO_NOT_FOUND"
    assert str(store_root) not in err.value.message


def test_size_and_checksum_must_match_the_message(store_root):
    data = wav_bytes(speech_samples())
    _, key = put_audio(store_root, data)
    store = AudioStore(store_root)
    with pytest.raises(PermanentEventError) as err:
        store.read(ref_for(key, data, sizeBytes=len(data) + 1))
    assert err.value.code == "AUDIO_SIZE_MISMATCH"
    with pytest.raises(PermanentEventError) as err:
        store.read(ref_for(key, data, sha256="0" * 64))
    assert err.value.code == "AUDIO_CHECKSUM_MISMATCH"


def test_store_availability(tmp_path):
    assert AudioStore(tmp_path).is_available()
    assert not AudioStore(tmp_path / "missing").is_available()


# ------------------------------------------------------------------ levels and quiet audio

def test_levels_of_silence_are_finite():
    assert audio_utils.peak_dbfs(silence()) == audio_utils.SILENCE_FLOOR_DBFS
    assert audio_utils.rms_dbfs(silence()) == audio_utils.SILENCE_FLOOR_DBFS


def test_full_scale_sine_levels():
    t = np.arange(SR) / SR
    sine = np.sin(2 * np.pi * 440 * t).astype(np.float32)
    assert audio_utils.peak_dbfs(sine) == pytest.approx(0.0, abs=0.1)
    assert audio_utils.rms_dbfs(sine) == pytest.approx(-3.0, abs=0.1)


def test_normal_audio_is_not_normalized():
    samples = speech_samples()
    out, gain = audio_utils.normalize_quiet(samples)
    assert gain == 0.0 and out is samples


def test_quiet_audio_is_raised_in_memory_only_and_silence_is_left_alone():
    quiet = quiet_speech()
    out, gain = audio_utils.normalize_quiet(quiet)
    assert 0 < gain <= audio_utils.MAX_GAIN_DB
    assert audio_utils.peak_dbfs(out) > audio_utils.peak_dbfs(quiet)
    assert np.array_equal(quiet, quiet_speech())  # input not modified
    _, silent_gain = audio_utils.normalize_quiet(silence())
    assert silent_gain == 0.0


def test_clipping_ratio():
    clipped = np.clip(speech_samples() * 20, -1, 1)
    assert audio_utils.clipped_ratio(clipped) > 0.01
    assert audio_utils.clipped_ratio(speech_samples()) < 0.01
