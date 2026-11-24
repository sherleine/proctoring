import { useEffect } from "react";
import { RecordingControls } from "../components/proctoring/RecordingControls";
import { SavedEvents } from "../components/proctoring/SavedEvents";
import { SessionTimeline } from "../components/proctoring/SessionTimeline";
import { WebcamPreview } from "../components/proctoring/WebcamPreview";
import { useEventMonitoring } from "../hooks/useEventMonitoring";

export function ProctoringPage() {
  const p = useEventMonitoring();
  const monitoring = p.phase === "monitoring";
  const capturing = monitoring && p.capturingSince !== null;

  useEffect(() => {
    if (!p.notice) return;
    const id = setTimeout(p.dismissNotice, 6000);
    return () => clearTimeout(id);
  }, [p.notice, p.dismissNotice]);

  return (
    <div className="layout">
      <header className="header">
        <div className="header-brand">
          <span className="logo" aria-hidden>
            <svg viewBox="0 0 24 24" width="18" height="18"><path fill="currentColor" d="M12 2 3 6v6c0 5 3.8 9.3 9 10 5.2-.7 9-5 9-10V6l-9-4Z" /></svg>
          </span>
          <div>
            <h1>Online Examination</h1>
            <span className="header-sub">Proctored session</span>
          </div>
        </div>
        <div className="header-right">
          <span className="session-id">
            Session <code>{p.sessionId}</code>
          </span>
          <span className={`status ${capturing ? "status-rec" : monitoring ? "status-mon" : ""}`}>
            {capturing ? (
              <>
                <span className="rec-dot" aria-hidden /> Recording event
              </>
            ) : monitoring ? (
              <>
                <span className="mon-dot" aria-hidden /> Monitoring
              </>
            ) : (
              "Not monitoring"
            )}
          </span>
        </div>
      </header>

      <main className="main">
        {p.error && (
          <div className="notice notice-error" role="alert">
            <span>{p.error}</span>
            <button className="btn-link" onClick={p.dismissError}>Dismiss</button>
          </div>
        )}
        {p.notice && (
          <div className="notice notice-ok" role="status">
            <span>{p.notice}</span>
            <button className="btn-link" onClick={p.dismissNotice}>Dismiss</button>
          </div>
        )}

        <section className="stage" aria-label="Camera and monitoring controls">
          <WebcamPreview
            camera={p.camera}
            phase={p.phase}
            monitoringSince={p.monitoringSince}
            capturingSince={p.capturingSince}
            onEnableCamera={() => void p.camera.request()}
          />
          <RecordingControls
            phase={p.phase}
            capturing={capturing}
            camera={p.camera}
            mic={p.mic}
            onEnableCamera={() => void p.camera.request()}
            onEnableMic={() => void p.mic.request()}
            onStart={() => void p.start()}
            onStop={() => void p.stop()}
          />
          {monitoring && p.monitoringSince && <SessionTimeline since={p.monitoringSince} spans={p.sessionSpans} capturingSince={p.capturingSince} />}
          <p className="privacy">
            {monitoring
              ? "Your camera and microphone are monitored. Short clips are saved only when speech is detected."
              : "Your camera and microphone are on for the preview. Nothing is saved until monitoring starts."}
          </p>
        </section>

        <SavedEvents items={p.saved.items} loading={p.saved.loading} error={p.saved.error} onDelete={(k) => void p.saved.remove(k)} />
      </main>
    </div>
  );
}
