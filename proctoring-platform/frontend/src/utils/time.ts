/** ISO-8601 UTC with milliseconds (the contract timestamp format). */
export function iso(epochMs: number): string {
  return new Date(Math.round(epochMs)).toISOString();
}

export function formatClock(isoTime: string): string {
  return new Date(isoTime).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit" });
}

/** 32+ character random id for the Idempotency-Key header. */
export function randomKey(): string {
  return crypto.randomUUID().replace(/-/g, "") + crypto.randomUUID().replace(/-/g, "");
}
