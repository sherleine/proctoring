import { audioConfig } from "../config/appConfig";
import { useMediaDevice } from "./useMediaDevice";

const MIC_CONSTRAINTS: MediaTrackConstraints = { channelCount: 1, ...audioConfig.constraints };

export function useMicrophone(options: { onDisconnected?: (reason: string) => void } = {}) {
  return useMediaDevice({ kind: "microphone", constraints: MIC_CONSTRAINTS, onDisconnected: options.onDisconnected });
}
