import { useEffect, useState } from "react";
import { EnrolmentPanel } from "../components/EnrolmentPanel";
import { EventList } from "../components/EventList";
import { MicActivity, StatusBar } from "../components/StatusBar";
import { WebcamPreview } from "../components/WebcamPreview";
import { useExam } from "../hooks/useExam";
import { describe } from "../recording/eventStatus";

export function ExamPage() {
  const { state, start, end, newExam, retryUpload, selectMic, startEnrolment, stopEnrolment, skipEnrolment, media } = useExam();
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const t = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(t);
  }, []);

  const running = state.phase === "running";
  const speechEvents = state.events.filter((e) => e.kind !== "enrolment");
  const voiceSample = state.events.filter((e) => e.kind === "enrolment").at(-1) ?? null;
  const lastEvent = speechEvents.length > 0 ? speechEvents[speechEvents.length - 1] : null;

  return (
    <main>
      <header>
        <h1>Online exam</h1>
        <p className="small">
          While the exam runs, your camera and microphone stay on. Only the moments when speech is detected are saved,
          as short audio clips (with a few seconds before the speech); nothing else is recorded or uploaded. Speech,
          silence or noise are not treated as misconduct.
        </p>
      </header>

      <section className="controls">
        {(state.phase === "idle" || state.phase === "ended") && (
          <button className="primary" onClick={state.phase === "ended" ? newExam : start}>{state.phase === "ended" ? "Start a new exam" : "Start exam"}</button>
        )}
        {state.phase === "interrupted" && <><button className="primary" onClick={start}>Resume exam</button><button onClick={end}>End exam</button></>}
        {state.phase === "starting" && <button disabled>Starting…</button>}
        {running && <button className="danger" onClick={end}>End exam</button>}
        {state.phase === "ending" && <button disabled>Ending… (saving the last clip)</button>}
        <label className="small">
          Microphone{" "}
          <select value={state.micId} disabled={running || state.phase === "starting"} onChange={(e) => selectMic(e.target.value)}>
            <option value="">System default</option>
            {state.micDevices.map((d) => <option key={d.id} value={d.id}>{d.label}</option>)}
          </select>
        </label>
        {state.session && <span className="small">Session {state.session.sessionId} · {state.session.status}</span>}
      </section>

      {state.errors.length > 0 && <div className="errors">{state.errors.map((e) => <p key={e}>⚠ {e}</p>)}</div>}
      {state.notices.map((n) => <p key={n} className="small notice">{n}</p>)}
      {state.phase === "interrupted" && <p className="notice">The exam was interrupted (page reload or device problem). Recorded clips are kept and uploaded; resume to continue monitoring.</p>}
      {state.phase === "ended" && <p className="notice">The exam has ended. Results of the last clips may still arrive below.</p>}

      {(running || voiceSample) && (
        <EnrolmentPanel enrolment={state.enrolment} sample={voiceSample} running={running}
          onStart={startEnrolment} onStop={stopEnrolment} onSkip={skipEnrolment} />
      )}

      <StatusBar state={state} />
      <div className="live">
        <WebcamPreview stream={media()} active={running && state.camera === "on"} />
        <div className="live-side">
          <MicActivity state={state} />
          <div className="latest">
            <h3>Last event</h3>
            {lastEvent ? (
              <p className="small">Event {lastEvent.sequenceNumber + 1} finalized at {new Date(lastEvent.endedAt).toLocaleTimeString()} ({lastEvent.durationSeconds.toFixed(1)} s) - {describe(lastEvent, now).label.toLowerCase()}</p>
            ) : <p className="small">No speech event yet.</p>}
          </div>
        </div>
      </div>

      <section>
        <h2>Events</h2>
        <EventList events={speechEvents} now={now} onRetry={retryUpload} />
      </section>
    </main>
  );
}
