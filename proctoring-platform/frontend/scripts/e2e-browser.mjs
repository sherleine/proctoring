
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import puppeteer from "puppeteer-core";

const here = dirname(fileURLToPath(import.meta.url));
const URL_ = process.env.E2E_URL ?? "http://localhost:5173";
const CHROME = process.env.CHROME_PATH ?? "C:/Program Files/Google/Chrome/Application/chrome.exe";
const SPEECH = join(here, "..", "..", "ai-service", "tests", "fixtures", "speech_16k_mono.wav");
const outDir = join(here, "..", "test-results");
mkdirSync(outDir, { recursive: true });


function makeAudio(name, gaps) {
  const src = readFileSync(SPEECH);
  const speech = src.subarray(src.indexOf("data") + 8);
  const parts = [];
  gaps.forEach((g, i) => { parts.push(Buffer.alloc(g * 32000)); if (i < gaps.length - 1) parts.push(speech); });
  const pcm = Buffer.concat(parts);
  const header = Buffer.alloc(44);
  header.write("RIFF", 0); header.writeUInt32LE(36 + pcm.length, 4); header.write("WAVE", 8); header.write("fmt ", 12);
  header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20); header.writeUInt16LE(1, 22); header.writeUInt32LE(16000, 24);
  header.writeUInt32LE(32000, 28); header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34); header.write("data", 36); header.writeUInt32LE(pcm.length, 40);
  const path = join(outDir, name);
  writeFileSync(path, Buffer.concat([header, pcm]));
  return path;
}

async function runScenario(name, audioFile, { expectEvents, waitMs, failUploads = 0 }) {
  const browser = await puppeteer.launch({
    executablePath: CHROME,
    headless: true,
    args: [
      "--use-fake-ui-for-media-stream", // auto-grant camera + microphone
      "--use-fake-device-for-media-stream", // synthetic camera
      `--use-file-for-fake-audio-capture=${audioFile}%noloop`,
      "--autoplay-policy=no-user-gesture-required",
    ],
  });
  const log = [];
  try {
    const page = await browser.newPage();
    page.on("console", (m) => log.push(`[console.${m.type()}] ${m.text()}`));
    page.on("pageerror", (e) => log.push(`[pageerror] ${e.message}`));
    let failedUploads = 0;
    const retryStates = [];
    if (failUploads > 0) {
      // Simulates the backend being unreachable for the first uploads.
      await page.setRequestInterception(true);
      page.on("request", (req) => {
        if (req.method() === "POST" && /\/events$/.test(req.url()) && failedUploads < failUploads) {
          failedUploads++;
          void req.abort("connectionrefused");
        } else void req.continue();
      });
      page.on("console", () => {});
      const poll = setInterval(async () => {
        try {
          const t = await page.evaluate(() => document.body.innerText);
          if (t.includes("Upload failed - retrying automatically")) retryStates.push(Date.now());
        } catch { /* page closing */ }
      }, 300);
      page.once("close", () => clearInterval(poll));
    }
    await page.goto(URL_, { waitUntil: "networkidle0" });
    await page.evaluate(() => sessionStorage.clear());
    await page.reload({ waitUntil: "networkidle0" });
    await new Promise((r) => setTimeout(r, 5000)); // like a real user: the page is open a while before starting
    const clickedAt = Date.now();
    await page.locator("button::-p-text(Start exam)").click();
    await page.waitForFunction(() => document.body.innerText.includes("Speech detection: active"), { timeout: 30_000 });
    const t0 = Date.now();

   
    if (expectEvents > 0) {
      await page.waitForFunction(
        (n) => [...document.querySelectorAll(".event")].filter((e) => /Audio saved \(analysed/.test(e.innerText.replace(/\s+/g, " "))).length >= n,
        { timeout: waitMs }, expectEvents,
      );
    } else {
      await new Promise((r) => setTimeout(r, waitMs));
    }
    const summary = await page.evaluate(() => [...document.querySelectorAll(".event")].map((e) => e.innerText.replace(/\s+/g, " ").trim()));
    const diag = await page.evaluate(() => window.__proctoringDiag);
    const status = await page.evaluate(() => document.querySelector(".badges")?.innerText.replace(/\s+/g, " "));
    await page.screenshot({ path: join(outDir, `${name}.png`), fullPage: true });

    
    const stored = await page.evaluate(() => new Promise((resolve) => {
      const req = indexedDB.open("proctoring-poc");
      req.onsuccess = () => {
        const all = req.result.transaction("events").objectStore("events").getAll();
        all.onsuccess = async () => resolve(await Promise.all(all.result.map(async (e) => ({ upload: e.upload, status: e.status,
          video: e.video ? e.video.size : 0, meta: JSON.parse(e.metadataJson), result: e.result,
          wav: e.wav ? Array.from(new Uint8Array(await e.wav.arrayBuffer())) : null }))));
      };
    }));
    await page.locator("button::-p-text(End exam)").click();
    await page.waitForFunction(() => document.body.innerText.includes("The exam has ended"), { timeout: 20_000 });
    return { name, clickedAt, diag, seconds: (Date.now() - t0) / 1000, summary, status, stored, log, failedUploads, retryShown: retryStates.length > 0 };
  } finally {
    await browser.close();
  }
}

const results = [];
let failed = false;
const check = (ok, message) => { console.log(`${ok ? "PASS" : "FAIL"}  ${message}`); if (!ok) failed = true; };

/** Parses a WAV byte array like the backend does. */
function wavInfo(bytes) {
  const b = Buffer.from(bytes);
  const ok = b.toString("ascii", 0, 4) === "RIFF" && b.toString("ascii", 8, 12) === "WAVE";
  const pcm = new Int16Array(b.buffer.slice(b.byteOffset + 44, b.byteOffset + b.length));
  let loud = 0;
  for (const v of pcm) if (Math.abs(v) > 300) loud++; // samples above about -40 dBFS
  return { ok, format: b.readUInt16LE(20), channels: b.readUInt16LE(22), rate: b.readUInt32LE(24), bits: b.readUInt16LE(34),
    seconds: (b.length - 44) / 32000, voicedSeconds: loud / 16000 };
}


const speech = await runScenario("speech", makeAudio("e2e-speech.wav", [4, 5, 6, 5, 6]), { expectEvents: 4, waitMs: 120_000 });
results.push(speech);
check(speech.summary.length === 4, `four utterances produced four separate events (got ${speech.summary.length})`);
check(speech.diag?.detected === 4 && speech.diag?.saved === 4 && speech.diag?.uploaded === 4,
  `detector/IndexedDB/backend counts: detected ${speech.diag?.detected}, saved ${speech.diag?.saved}, uploaded ${speech.diag?.uploaded}`);
check(speech.stored.every((e) => e.upload === "uploaded" && e.status === "COMPLETED"), "all four: uploaded, stored by the backend and processed");
for (const [i, e] of speech.stored.sort((a, b) => a.meta.sequenceNumber - b.meta.sequenceNumber).entries()) {
  const w = e.wav ? wavInfo(e.wav) : null;
  check(!!w && w.ok && w.format === 1 && w.channels === 1 && w.rate === 16000 && w.bits === 16,
    `event ${i + 1}: valid 16 kHz mono PCM 16-bit WAV kept locally (${w?.seconds.toFixed(2)} s)`);
  check(!!w && w.voicedSeconds > 1.5 && e.meta.preEventSeconds >= 2 && w.seconds <= 2.5 + 3.5 + 0.6,
    `event ${i + 1}: contains the speech (${w?.voicedSeconds.toFixed(2)} s voiced), pre-roll ${e.meta.preEventSeconds} s, no long silence (${w?.seconds.toFixed(2)} s total)`);
  check(e.result?.speech?.verified === true && e.result?.transcription?.available === false,
    `event ${i + 1}: server confirmed speech; no transcription required (${e.result?.transcription?.reason})`);
  
  const d = e.result?.diarization;
  const kinds = (e.result?.observations ?? []).map((o) => `${o.observationType}/${o.category}`);
  check(d?.available === true && d?.speakerCount === 1 && kinds.includes("SPEECH_DETECTED/informational")
      && !(e.result?.observations ?? []).some((o) => o.category === "review"),
    `event ${i + 1}: diarized (${d?.speakerCount} speaker), observations ${JSON.stringify(kinds)} - none for review`);
}
check(speech.summary.every((t) => /Speech detected \(\d+\.\d s\)/.test(t)),
  "every event shows the informational 'Speech detected' line on the page");
const meta = speech.stored[0]?.meta;
const startedAt = meta ? Date.parse(meta.startedAt) : 0;
check(startedAt >= speech.clickedAt - 1000 && startedAt <= speech.clickedAt + 30_000,
  `event timestamps follow "Start exam" (${meta?.startedAt}), not page load`);
check((speech.stored[0]?.video ?? 0) > 0, `local video clip stored in IndexedDB (${speech.stored[0]?.video} bytes, not uploaded)`);


const silence = await runScenario("silence", makeAudio("e2e-silence.wav", [20]), { expectEvents: 0, waitMs: 20_000 });
results.push(silence);
check(silence.summary.length === 0, `20 s of silence produced no events (got ${silence.summary.length})`);


const retry = await runScenario("upload-retry", makeAudio("e2e-retry.wav", [4, 6]), { expectEvents: 1, waitMs: 120_000, failUploads: 2 });
results.push(retry);
check(retry.failedUploads === 2 && retry.retryShown, `upload failed twice (network), page showed the automatic retry`);
check(retry.summary.length === 1 && /Audio saved/.test(retry.summary[0]), "event uploaded once on retry and saved");


{
  const browser = await puppeteer.launch({ executablePath: CHROME, headless: true, args: ["--deny-permission-prompts"] });
  try {
    const page = await browser.newPage();
    await page.goto(URL_, { waitUntil: "networkidle0" });
    await page.evaluate(() => sessionStorage.clear());
    await page.reload({ waitUntil: "networkidle0" });
    await page.locator("button::-p-text(Start exam)").click();
    await page.waitForFunction(() => document.body.innerText.includes("denied"), { timeout: 20_000 });
    const text = await page.evaluate(() => document.body.innerText);
    await page.screenshot({ path: join(outDir, "permission-denied.png"), fullPage: true });
    check(/access was denied/i.test(text) && /permission denied/i.test(text), "permission denial is explained to the candidate");
    check(!/Session ses_/.test(text), "no exam session is created without camera/microphone");
    results.push({ name: "permission-denied", text: text.slice(0, 800) });
  } finally {
    await browser.close();
  }
}

writeFileSync(join(outDir, "e2e-results.json"), JSON.stringify(results, null, 2));
for (const r of results) {
  const errors = (r.log ?? []).filter((l) => /error/i.test(l));
  if (errors.length) console.log(`${r.name}: browser errors:\n  ${errors.join("\n  ")}`);
}
console.log(failed ? "E2E FAILED" : "E2E PASSED", `- details in ${join(outDir, "e2e-results.json")}`);
process.exit(failed ? 1 : 0);
