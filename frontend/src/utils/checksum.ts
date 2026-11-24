/** SHA-256 hex digest of a Blob (null where Web Crypto is unavailable, i.e. insecure origins). */
export async function sha256Hex(blob: Blob): Promise<string | null> {
  if (!crypto.subtle) return null;
  const digest = await crypto.subtle.digest("SHA-256", await blob.arrayBuffer());
  return Array.from(new Uint8Array(digest), (b) => b.toString(16).padStart(2, "0")).join("");
}
