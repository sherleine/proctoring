# Model comparison for suspicious-audio detection

Status: research only (24 Nov 2026). No model in this document has been added to the code; the POC still runs only
Silero VAD and (optionally) faster-whisper.

Scope: the **standalone** audio-proctoring application in this repository (no dependency on any other proctoring
system). Related: [detection-strategy.md](detection-strategy.md) · [integration-design.md](integration-design.md) ·
[evaluation-plan.md](evaluation-plan.md)

## How to read the evidence labels

| Label | Meaning |
|---|---|
| **[V]** | Verified: stated on the official repository, model card or paper linked in the row (read on 24 Nov 2026). |
| **[M]** | Measured by us on the development machine (see [Measured on this machine](#measured-on-this-machine)). |
| **[E]** | Estimate: derived from [V] facts (e.g. parameter count → file size) or from third-party numbers; must be measured. |
| **[A]** | Assumption: plausible but unchecked; the evaluation plan has to confirm or reject it. |
| **not stated** | The official source does not give this number; we did not invent one. |

No accuracy figure below is an accuracy **for proctoring**. Published numbers are on research benchmarks
(AudioSet, VoxCeleb, AMI, DIHARD …) whose recordings differ from a laptop microphone in a candidate's room. Our own
accuracy will only be known after [evaluation-plan.md](evaluation-plan.md) has been run.

## Measured on this machine

Development machine: Intel Core i5-13450HX (10 cores / 16 threads), 15.7 GB RAM, of which ~2 GB was free during the
measurement (the machine is under constant memory pressure). Windows 11, Python 3.12, CPU only.
Input: the POC fixture `ai-service/tests/fixtures/speech_16k_mono.wav` (3.46 s, one sentence) placed inside a
10 s clip (= the POC's maximum event length). Each model ran in its own process; 10 (VAD) or 5 (Whisper) timed
runs after one warm-up. Script: `bench_cpu.py` (scratchpad, not part of the repo).

| Model | Threads | Median time per 10 s clip | Real-time factor | Process peak working set |
|---|---|---|---|---|
| Silero VAD 6.2.3 (PyTorch JIT, `get_speech_timestamps`) | 1 | **158 ms** (135–340) [M] | 0.016 | **220 MB** (incl. PyTorch) [M] |
| faster-whisper tiny, int8, beam 5 | 4 | **635 ms** (601–706) [M] | 0.063 | **236 MB** [M] |
| faster-whisper base, int8, beam 5 | 4 | **1 310 ms** (741–1 369) [M] | 0.131 | **332 MB** [M] |
| faster-whisper small, int8, beam 5 | 4 | **2 136 ms** (2 127–2 292) [M] | 0.214 | **709 MB** [M] |

All three Whisper sizes transcribed the sentence correctly ("Can you tell me the answer to question number 3?");
`small` added a spurious "3." at the start, a small example of the hallucinations Whisper is known for.
These are single-machine, single-clip numbers under memory pressure, not a benchmark.

---

## A. Multiple speakers

Two different questions hide behind "multiple speakers", and they need different tools:

1. **"Is this voice the candidate?"** — speaker *verification*. Needs one speaker **embedding** (voice fingerprint)
   model and a reference recording of the candidate (enrolment at exam start). Works per clip, even when the clip
   contains only one voice (e.g. a helper speaking while the candidate is silent).
2. **"Are there two or more voices inside this clip, and when / overlapping?"** — speaker *diarization* /
   *segmentation*. Needs a segmentation model (who speaks when, overlap) plus embeddings to link segments.

Diarization alone cannot answer question 1 for a clip with a single (non-candidate) voice; verification alone cannot
see two voices overlapping. The recommended design combines a cheap embedding check on every speech clip with
segmentation only when triggered ([detection-strategy.md](detection-strategy.md)).

| Model | Official source | Size | CPU feasibility | Latency | Memory | Licence | Strengths | Limitations | For this POC? |
|---|---|---|---|---|---|---|---|---|---|
| **pyannote `speaker-diarization-community-1`** (full pipeline) | [HF model card](https://huggingface.co/pyannote/speaker-diarization-community-1), [pyannote-audio](https://github.com/pyannote/pyannote-audio) | not stated | "pipelines run on CPU by default" [V]; CPU speed **not stated** | Only GPU speed published: 31–37 s per hour of audio on an H100 [V]; CPU not stated | not stated | Code MIT [V]; model **CC-BY-4.0** [V]; **gated** (accept conditions + HF token) [V]; needs **ffmpeg** (torchcodec) [V] | State of the art among open pipelines: DER 17.0 % AMI-IHM, 20.2 % DIHARD 3 [V]; handles overlap in scoring; `min/max_speakers` options [V] | Built for long recordings; heavy dependency tree (pyannote.audio 4.x, torch, torchcodec, ffmpeg); CPU latency unknown; gated download complicates deployment | **Not on every clip.** Candidate for offline/batch review or a later phase after a CPU benchmark |
| **pyannote `segmentation-3.0`** (the segmentation model inside the pipeline) | [HF model card](https://huggingface.co/pyannote/segmentation-3.0) | not stated on the card; ONNX export is **5.7 MB** (fp32) / **1.5 MB** (int8) [V, sherpa-onnx](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/models.html) | Small model; runs on CPU via ONNX in sherpa-onnx [V] | not stated alone (see sherpa-onnx row) | not stated | **MIT** [V]; gated on HF [V] (the sherpa-onnx ONNX copy is not gated [A]) | Input: 10 s, 16 kHz mono [V] — matches our max clip; up to **3 speakers per chunk, 2 overlapping** (powerset) [V]; overlapped-speech detection supported [V] | "cannot be used to perform speaker diarization of full recordings on its own" [V] — fine for ≤10 s clips; no speaker identity across clips | **Yes, triggered** (overlap / speaker-change detection inside a clip) |
| **SpeechBrain ECAPA-TDNN** `spkrec-ecapa-voxceleb` | [HF model card](https://huggingface.co/speechbrain/spkrec-ecapa-voxceleb) | parameters not stated; checkpoint ~83 MB [E, search result] | PyTorch, CPU [A] | not stated | not stated | **Apache-2.0** [V]; trained on VoxCeleb 1+2 [V] | EER **0.80 %** VoxCeleb1-test [V]; widely used; 16 kHz mono [V] | Adds the SpeechBrain dependency; VoxCeleb is celebrity interview speech, not laptop microphones | Possible; second choice |
| **3D-Speaker CAM++** | [3D-Speaker](https://github.com/modelscope/3D-Speaker) | **7.2 M params** [V] | ONNX Runtime export provided [V] | not stated | not stated | Code **Apache-2.0** [V]; licence of the VoxCeleb weights not stated separately [A: dataset terms, see WeSpeaker row] | EER **0.65 %** VoxCeleb1-O [V] with a small model | Chinese-centred project docs; weight licence to confirm | **Yes — recommended embedding model** (small, ONNX, good EER) |
| **WeSpeaker** (ResNet34, ECAPA, CAM++ …) | [wespeaker](https://github.com/wenet-e2e/wespeaker), [pretrained.md](https://github.com/wenet-e2e/wespeaker/blob/master/docs/pretrained.md) | not stated | `.onnx` runtime models provided [V] | not stated | not stated | Code Apache-2.0 [V]; VoxCeleb weights follow the dataset licence: **CC BY 4.0** [V] | Many models, ONNX; `pyannote/wespeaker-voxceleb-resnet34-LM` is the embedding used by pyannote [V] | "LM" models tuned for >3 s audio [V]; our clips can be shorter | Alternative to CAM++ |
| **NeMo TitaNet-small** (via sherpa-onnx) | [sherpa-onnx models](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/models.html) | not stated | ONNX on CPU [V] | see sherpa-onnx row | not stated | to verify (NVIDIA model licence) [A] | Fastest embedding in the sherpa-onnx diarization table [V] | Licence and English-domain accuracy unverified | Alternative after licence check |
| **sherpa-onnx** (runtime, not a model) | [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), [diarization docs](https://k2-fsa.github.io/sherpa/onnx/speaker-diarization/index.html) | pip wheels `sherpa-onnx` [V] | CPU ONNX; no PyTorch needed | Diarization RTF on a 56.9 s file: **0.11–0.30** (pyannote-seg-3.0 + NeMo / 3D-Speaker embeddings, machine not specified) [V, third-party machine] | not stated | **Apache-2.0** [V] | One small dependency for segmentation + embeddings (+ audio tagging); int8 models | Speed numbers are from the authors' unspecified machine; we must benchmark | **Yes — recommended runtime** for segmentation and embeddings |
| **Resemblyzer** (GE2E d-vector) | [Resemblyzer](https://github.com/resemble-ai/Resemblyzer) | not stated | "can run both on CPU or GPU" [V] | ~1000× real time on a GTX 1080 [V]; CPU not stated | not stated | Apache-2.0 [V] | Very simple API | Older (GE2E) model; "works best on English language only" [V]; no published EER on the page | No (superseded by CAM++/ECAPA) |
| **Brouhaha** (VAD + SNR + C50) | [brouhaha-vad](https://github.com/marianne-m/brouhaha-vad) | not stated | PyTorch (pyannote-based) [V] | not stated | not stated | MIT [V] | Predicts speech, **speech-to-noise ratio and C50 (reverberation)** per frame [V] — direct cues for "distant" speech | Research code, documented for Python 3.8 [V]; no published size; untested with our stack | Later phase / research spike for background-speech scoring |

**Why not "count speakers with diarization on every clip"**: on a 2–10 s clip there are few frames per speaker,
and clustering-based diarization becomes unstable on short audio [A — to be measured in the evaluation]. Comparing
each clip with a candidate enrolment embedding is cheaper and answers the question the proctor actually has
("is someone other than the candidate speaking?").

## B. Unusual sound detection (audio event classification)

All candidates below are trained on **AudioSet** (Google; YouTube clips; ontology CC-BY-4.0). They output
*tags* (probabilities for a fixed class list), mostly at clip level; frame-level timing is coarse.

### Which target sounds exist as AudioSet classes

Checked against YAMNet's official class map
([yamnet_class_map.csv](https://raw.githubusercontent.com/tensorflow/models/master/research/audioset/yamnet/yamnet_class_map.csv)) [V]:

| Target | AudioSet / YAMNet class (index) | Notes |
|---|---|---|
| Phone ringing | `Telephone` (383), `Telephone bell ringing` (384), `Ringtone` (385), `Telephone dialing, DTMF` (386) | Modern ringtones are often music → may score as `Music` (132) [A] |
| Keyboard typing | `Typing` (378), `Computer keyboard` (380), `Typewriter` (379) | Typing is **normal** in many exams — report as context, not a concern by default |
| Whispering | `Whispering` (12) | Exists as a class; quality on laptop-mic whispers unknown [A] |
| Conversation / speech | `Speech` (0), `Conversation` (2), `Narration, monologue` (3), `Babbling` (4), `Speech synthesizer` (5) | `Conversation` is a useful hint for background talk |
| Media playback | `Television` (518), `Radio` (519), `Music` (132) | Distinguishes TV/radio speech from people in the room — imperfectly [A] |
| Other room sounds | `Cough` (42), `Door` (348), `Knock` (353), `Clicking` (485), `Tick` (401), `Silence` (494), `Inside, small room` (500) | `Mouse` (119) is the **animal**, not a computer mouse |

The gendered classes "Male speech, man speaking" / "Female speech, woman speaking" are **not** in YAMNet's 521
classes (removed by Google's fairness review) [V]; we will not use gender as a signal anyway.

PANNs, AST, EfficientAT and CED use the 527-class AudioSet label set, which contains the same targets [A — same
ontology; per-model label files to be checked when integrating].

| Model | Official source | Size | CPU feasibility | Latency | Memory | Licence | Strengths | Limitations | For this POC? |
|---|---|---|---|---|---|---|---|---|---|
| **YAMNet** | [README](https://github.com/tensorflow/models/blob/master/research/audioset/yamnet/README.md) | **3.7 M weights, 69.2 M multiplies per 0.96 s frame** [V] | MobileNet-v1, designed to be light [V] | not stated; ~10 frames per 10 s clip → ~0.7 G multiplies per clip [E] | not stated; TensorFlow runtime adds several hundred MB [A] | **Apache-2.0** (tensorflow/models LICENSE) [V] | Official class map, 16 kHz mono input [V] = our format; one score per 0.96 s frame (hop 0.48 s) gives rough timing [V] | Balanced mAP **0.306** [V] (lowest here); needs **TensorFlow + Keras 2 / tf-keras, not Keras 3** [V] — a second ML framework next to PyTorch | Baseline / comparator; not the default because of the TensorFlow dependency |
| **PANNs MobileNetV1** | [audioset_tagging_cnn](https://github.com/qiuqiangkong/audioset_tagging_cnn), [paper](https://arxiv.org/abs/1912.10211) | **4.8 M params**, 3.6 G multi-adds [V, paper table] | PyTorch (already in our stack) | not stated | not stated | Code **MIT** [V]; checkpoint licence on Zenodo not stated [A] | mAP **0.389** [V]; `panns_inference` pip package [V] | Trained at **32 kHz** [V] → we must upsample our 16 kHz WAV (no information above 8 kHz) [E: some accuracy loss] | Candidate |
| **PANNs Cnn14 / Cnn14_16k** | same | Cnn14 **80.8 M params**, 42.2 G multi-adds [V] | Heavy for every clip [E] | not stated | ~320 MB weights in fp32 [E from params] | MIT code [V] | mAP 0.431 (Cnn14) [V]; **Cnn14_16k** trained at 16 kHz, mAP 0.438 README / 0.427 paper [V]; `Cnn14_DecisionLevelMax` gives frame-level events (SED) [V] | Too heavy for per-clip CPU use at scale | Offline reference only |
| **AST** (`audioset_10_10_0.4593`) | [ast](https://github.com/YuanGongND/ast), [HF card](https://huggingface.co/MIT/ast-finetuned-audioset-10-10-0.4593) | **86.6 M params** [V] | Transformer on 10.24 s × 128 mel input [V]; heavy on CPU [E] | not stated | ~350 MB weights fp32 [E] | **BSD-3-Clause** [V] | mAP **0.459** single model [V]; 16 kHz [V] | Expensive; fixed 10.24 s input; overkill for short clips | No (offline reference at most) |
| **EfficientAT `mn10_as`** | [EfficientAT](https://github.com/fschmid56/EfficientAT) | **4.88 M params, 0.54 G MACs** [V] | Efficient MobileNetV3 design; PyTorch [V] | not stated | not stated | **MIT** [V] (weights downloaded from the repo's GitHub releases) | mAP **47.1** [V] — near AST accuracy at ~1/18 of the parameters | Sample rate not stated on the README (to verify; the project's training recipe uses 32 kHz [A]) | **Recommended default**, subject to the evaluation |
| **EfficientAT `mn04_as`** | same | **0.98 M params, 0.11 G MACs** [V] | Very light | not stated | not stated | MIT [V] | mAP 43.2 [V] | Lower accuracy than mn10 | Fallback if CPU budget is tight |
| **CED-Tiny / Mini** | [CED](https://github.com/RicherMans/CED) | Tiny **5.5 M**, Mini **9.6 M** params [V] | ONNX export scripts provided [V]; 16 kHz input [V] | not stated | not stated | **GPL-3.0** [V] | mAP 48.1 (Tiny) / 49.0 (Mini) on AS-2M [V] — best accuracy per parameter here | **GPL-3.0 is copyleft**: shipping it inside the AI service puts the service under GPL terms; needs licence approval | Not without legal approval |
| **sherpa-onnx Zipformer audio tagging** | [sherpa-onnx audio tagging](https://k2-fsa.github.io/sherpa/onnx/audio-tagging/index.html) | not stated | ONNX CPU [V] | not stated | not stated | sherpa-onnx Apache-2.0 [V]; model licence not stated | Same runtime as the recommended speaker stack | No accuracy or size published on the page | Watch; evaluate only if EfficientAT is unsuitable |

### Per-target suitability (all [A] until evaluated)

| Target | Expected difficulty | Why |
|---|---|---|
| Phone ringing | Medium | Clear AudioSet classes, but ringtones vary (music, vibration). Vibration on a desk is not a reliable AudioSet class. |
| Keyboard typing | Easy to detect, **hard to interpret** | Distinctive transients; but typing is expected in exams with typed answers. Browser noise suppression may remove it before it is recorded. |
| Whispering | Hard | Low level, unvoiced; browser VAD and Silero may not trigger; noise suppression may remove it. The tagger's `Whispering` class is a weak signal on its own. |
| TV / radio speech | Medium | `Television`/`Radio` classes exist, but compressed speech from a loudspeaker and a person in the room overlap acoustically. |
| Generic environmental noise | Easy to tag, not suspicious | Fans, traffic, doors are normal; they should *suppress* rather than raise events. |

## C. Background conversation

Signals that can indicate "someone else is talking in the background", from strongest to weakest:

1. **Non-candidate voice** — speaker embedding of the speech segments does not match the candidate enrolment
   (section A). Strongest single signal, but fails when the background voice is too quiet or noisy for a stable
   embedding [A].
2. **Overlapping speech / speaker change** inside a clip — pyannote `segmentation-3.0` (section A).
3. **Distance cues** — lower speech-to-noise ratio and more reverberation (lower C50) than the candidate's own
   enrolment recording. Brouhaha predicts exactly SNR and C50 [V]; a simpler proxy is the ratio of speech level to
   the noise floor the browser already measures (`trigger.noiseFloorDbfs` in the POC metadata).
4. **Tagger context** — `Conversation`, `Television`, `Radio` scores (section B).
5. **Relative level** — speech level compared with the candidate's enrolment level. Weak on its own (see below).

### Why a single microphone's volume cannot separate the candidate from a background speaker

- **Distance and effort trade off.** Sound pressure falls with distance (about −6 dB per doubling in free field), but
  a person speaking loudly 2 m away can reach the microphone at the same level as a candidate murmuring 40 cm away.
  One number (level) cannot separate two unknowns (distance and vocal effort).
- **The microphone and the browser change the level.** Laptop microphones, headsets and Bluetooth headsets differ by
  tens of dB in sensitivity; browser **automatic gain control** (`autoGainControl`, enabled by default in the POC)
  continually rescales the signal, and **noise suppression** attenuates sounds it classifies as noise — which can
  include distant speech. Levels are not comparable across devices, sessions, or even minutes.
- **The candidate is not a constant source.** Turning the head, leaning back, whispering or speaking up changes
  the candidate's own level by more than the difference between "candidate" and "someone across the room".
- **Rooms reflect sound.** In a small room, reverberation keeps a distant voice louder than free-field attenuation
  predicts; in a large room the opposite. A fixed loudness threshold has no physical meaning.
- **Overlap.** When both speak at once, a single channel carries the sum; level says nothing about how many sources
  contributed.

Level is therefore used only as **one feature** in combination with speaker identity, overlap and distance cues, and
always relative to the same session's own enrolment, never as an absolute threshold.

## D. Potential verbal assistance

| Approach | What it can detect | Needs | Strengths | Limitations | For this POC? |
|---|---|---|---|---|---|
| **No transcription** (acoustic only) | "Non-candidate speech while the candidate is being examined"; turn-taking (candidate ↔ other voice) | Speaker embeddings + segmentation | Works when Whisper is disabled or wrong; no content stored | Cannot tell assistance from a family member asking an unrelated question | **Yes — baseline** |
| **Transcription** (faster-whisper) | The words that were said | faster-whisper (MIT [V]); tiny/base/small measured above [M] | Enables keyword/content rules; evidence a human can read | Errors and hallucinations (seen even on our fixture [M]); weaker on whispered/distant/overlapping speech [A]; privacy weight of storing text | **Optional, off by default** (as in the POC) |
| **Rule-based text analysis** | Phrases such as "the answer is", option letters ("B", "option C"), question numbers, imperative instructions | Transcript | Transparent, cheap, explainable to a proctor | Language-dependent keyword lists; easy to evade; false positives from a candidate reading a question aloud | Yes, only on top of transcription and only combined with a non-candidate-voice signal |
| **LLM classification** | Intent ("is someone giving instructions?") | An LLM, the transcript; possibly external service | Handles paraphrases | Cost, latency, data-protection review if external; non-deterministic; can hallucinate intent | **No** for this POC (out of scope, as agreed) |
| **Contextual comparison with exam content** | Spoken text that matches the current question, options or answer key | Exam questions/answers per session (this standalone app has no question bank) | Strongest content evidence ("someone read out the correct option") | Needs an exam-content store that this app does not have; answer keys are sensitive data | Later phase, design only |

**Conclusion:** transcription is *not* needed to raise MULTIPLE_SPEAKERS or BACKGROUND_SPEECH observations, and the
audio path must work without it. It is needed only to raise POSSIBLE_VERBAL_ASSISTANCE with content evidence; without
a transcript the system can at most say "another voice spoke while the candidate was present", which is reported as
MULTIPLE_SPEAKERS, not as assistance.

## Recommendations (summary)

| Need | Recommended for the POC | Why | Fallback |
|---|---|---|---|
| Speech verification | **Silero VAD** (already in POC) | Measured 158 ms per 10 s clip [M], MIT [V] | – |
| "Is it the candidate?" | **CAM++** embeddings via **sherpa-onnx** (or 3D-Speaker ONNX) + enrolment | 7.2 M params, EER 0.65 % [V], Apache-2.0 code [V], ONNX | ECAPA (SpeechBrain) |
| Overlap / speaker change in a clip | **pyannote segmentation-3.0** ONNX via sherpa-onnx, triggered only | MIT [V], 1.5–5.7 MB [V], 10 s chunks = our clip size [V] | Full community-1 pipeline offline |
| Unusual sounds | **EfficientAT mn10_as** | MIT [V], 4.9 M params, mAP 47.1 [V], PyTorch already present | YAMNet (Apache-2.0, TensorFlow) / mn04_as |
| Distant speech cue | Speech-to-noise ratio from the POC's VAD + noise floor; Brouhaha as a research spike | No new dependency for the first version | – |
| Assistance content | faster-whisper **base** int8, optional, off by default | 1.3 s per 10 s clip, 332 MB peak [M] | tiny (0.6 s, 236 MB [M]) |
| LLM | none | Out of scope | – |

Licence items needing sign-off before any use: pyannote model gating (accept-conditions flow and HF token in
deployment), CC-BY-4.0 attribution for community-1 and VoxCeleb-trained weights, PANNs/EfficientAT checkpoint terms,
CED's GPL-3.0 (excluded), AudioSet-derived models in general (training data from YouTube).
