import { videoConfig } from "../config/appConfig";
import { useMediaDevice } from "./useMediaDevice";

const CAMERA_CONSTRAINTS: MediaTrackConstraints = {
  width: { ideal: videoConfig.width },
  height: { ideal: videoConfig.height },
  frameRate: { ideal: videoConfig.frameRate },
  facingMode: "user",
};

export function useCamera(options: { onDisconnected?: (reason: string) => void } = {}) {
  return useMediaDevice({ kind: "camera", constraints: CAMERA_CONSTRAINTS, onDisconnected: options.onDisconnected });
}
