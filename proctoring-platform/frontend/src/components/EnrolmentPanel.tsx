import { ENROLMENT_MAX_SECONDS, ENROLMENT_MIN_SPEECH_SECONDS, type EnrolmentState } from "../hooks/useExam";
import type { LocalEvent } from "../storage/eventStore";

const PASSAGE =
  "Please read this text aloud in your normal voice. Today I am taking an online examination. I will read each " +
  "question carefully, think about my answer, and write it down in my own words. When I am not sure, I will move " +
  "on and come back to the question later. I understand that the exam is monitored for my own protection and fairness.";

interface Props {
  enrolment: EnrolmentState;
  sample: LocalEvent | null; 
  running: boolean;
  onStart: () => void;
  onStop: () => void;
  onSkip: () => void;
}


function outcome(sample: LocalEvent | null): { label: string; tone: string; detail?: string } | null {
  if (!sample) return null;
  if (sample.upload !== "uploaded") return { label: "Uploading the voice sample…", tone: "busy" };
  if (sample.status === "FAILED" || sample.status === "DEAD_LETTERED")
    return { label: "The voice sample could not be processed", tone: "warn", detail: sample.backendError?.message };
  const result = sample.result?.enrolment;
  if (sample.status !== "COMPLETED" || !result) return { label: "Checking the voice sample…", tone: "busy" };
  if (result.status === "accepted")
    return { label: "Voice sample accepted", tone: "ok", detail: result.reason ?? `${result.speechSeconds.toFixed(1)} s of speech` };
  if (result.status === "uncertain")
    return { label: "Voice sample unclear - voices will not be compared until a clearer sample is recorded", tone: "warn", detail: result.reason ?? undefined };
  return { label: "Voice sample not usable", tone: "warn", detail: result.reason ?? undefined };
}

export function EnrolmentPanel({ enrolment, sample, running, onStart, onStop, onSkip }: Props) {
  const result = outcome(sample);
  const accepted = sample?.result?.enrolment?.status === "accepted";
  if (enrolment.phase === "skipped" && !sample) {
    return <section className="enrolment small">Voice sample skipped: voices are not compared with yours in this exam.</section>;
  }
  if (accepted && enrolment.phase !== "recording") {
    return <section className="enrolment small"><span className="badge ok">{result!.label}</span> {result!.detail}</section>;
  }

  return (
    <section className="enrolment">
      <h3>Voice sample</h3>
      <p className="small">
        A short sample of your voice lets the system tell your voice apart from other voices. It is used only for this
        exam session and deleted afterwards. A different voice is shown to a human reviewer as an observation - it is
        never treated as proof of anything.
      </p>
      {enrolment.phase === "recording" ? (
        <>
          <blockquote className="passage">{PASSAGE}</blockquote>
          <p className="small">
            Recording… {enrolment.seconds.toFixed(0)} s (speech {enrolment.speechSeconds.toFixed(0)} s of at least{" "}
            {ENROLMENT_MIN_SPEECH_SECONDS} s; stops automatically after {ENROLMENT_MAX_SECONDS} s)
          </p>
          <button className="primary" onClick={onStop}>Finish voice sample</button>
        </>
      ) : (
        <>
          {result && <p><span className={`badge ${result.tone}`}>{result.label}</span> {result.detail && <span className="small">{result.detail}</span>}</p>}
          {enrolment.phase === "saving" && <p className="small">Saving the voice sample…</p>}
          <button className="primary" disabled={!running || enrolment.phase === "saving"} onClick={onStart}>
            {sample ? "Record the voice sample again" : "Record voice sample (about 15 s)"}
          </button>{" "}
          {!sample && <button disabled={!running} onClick={onSkip}>Skip</button>}
          {!running && <p className="small">Start the exam first.</p>}
        </>
      )}
    </section>
  );
}
