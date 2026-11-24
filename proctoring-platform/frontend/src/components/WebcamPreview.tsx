import { useEffect, useRef } from "react";

export function WebcamPreview({ stream, active }: { stream: MediaStream | null; active: boolean }) {
  const video = useRef<HTMLVideoElement>(null);
  useEffect(() => {
    if (video.current) video.current.srcObject = active && stream && stream.getVideoTracks().length > 0 ? stream : null;
  }, [stream, active]);
  const hasVideo = active && !!stream && stream.getVideoTracks().some((t) => t.readyState === "live");
  return (
    <div className="webcam">
      <video ref={video} autoPlay muted playsInline />
      {!hasVideo && <div className="webcam-off">Camera preview off</div>}
    </div>
  );
}
