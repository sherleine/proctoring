// End-to-end test of event-based proctoring in real Chrome.
//
//   npm run e2e -- [--seconds 45] [--wav ../tests/fixtures/audio/speech_with_pauses.wav]
//
// Chrome provides a fake camera and plays the WAV (speech separated by pauses, looped)
// as the microphone. The test monitors for --seconds, then checks that only short event
// clips were saved, downloads every event pair to data/e2e/events/ and plays one back.
// tests/e2e/verify_event_clips.py then checks each pair with ffmpeg.
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { join, resolve } from "node:path";
import puppeteer from "puppeteer-core";

const args = Object.fromEntries(
  process.argv.slice(2).reduce((acc, a, i, arr) => (a.startsWith("--") ? [...acc, [a.slice(2), arr[i + 1]]] : acc), []),
);
const URL_ = args.url ?? "http://localhost:5173";
const WAV = resolve(args.wav ?? "../tests/fixtures/audio/speech_with_pauses.wav");
const OUT = resolve(args.out ?? "../data/e2e/events");
const SECONDS = Number(args.seconds ?? 45);
const CHROME =
  args.chrome ??
  ["C:/Program Files/Google/Chrome/Application/chrome.exe", "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"].find(existsSync);

if (!existsSync(WAV)) {
  console.error(`Fixture ${WAV} missing - run tests\\fixtures\\generate_speech.ps1 first`);
  process.exit(1);
}
rmSync(OUT, { recursive: true, force: true });
mkdirSync(OUT, { recursive: true });
const results = [];
const check = (name, ok, detail) => {
  results.push({ name, ok, detail });
  console.log(`${ok ? "PASS" : "FAIL"}  ${name}${detail ? `  (${detail})` : ""}`);
};
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const clickButton = (page, text) =>
  page.evaluate((text) => [...document.querySelectorAll("button")].find((b) => b.textContent?.includes(text) && !b.disabled)?.click(), text);

const browser = await puppeteer.launch({
  executablePath: CHROME,
  headless: true,
  protocolTimeout: 600000,
  dumpio: !!process.env.E2E_DUMPIO,
  args: [
    "--use-fake-ui-for-media-stream",
    "--use-fake-device-for-media-stream",
    `--use-file-for-fake-audio-capture=${WAV}`,
    "--autoplay-policy=no-user-gesture-required",
  ],
});
let page;
const dumpState = async (label) => {
  try {
    const state = await page.evaluate(() => ({
      notice: [...document.querySelectorAll(".notice")].map((n) => n.textContent),
      devices: [...document.querySelectorAll(".device")].map((d) => `${d.querySelector(".device-name")?.textContent}: ${d.querySelector(".device-state")?.textContent} (${d.getAttribute("title")})`),
      buttons: [...document.querySelectorAll("button")].map((b) => `${b.textContent}${b.disabled ? " [disabled]" : ""}`).filter((b) => !b.startsWith("Delete")),
    }));
    console.log(`state at failure (${label}):`, JSON.stringify(state, null, 1));
  } catch {
    /* page gone */
  }
};
try {
  page = await browser.newPage();
  await page.setViewport({ width: 1280, height: 900 });
  const consoleErrors = [];
  page.on("console", (m) => m.type() === "error" && consoleErrors.push(m.text()));
  page.on("pageerror", (e) => consoleErrors.push(e.message));
  await page.goto(URL_, { waitUntil: "networkidle0" });
  await page.evaluate(() => new Promise((r) => { const q = indexedDB.deleteDatabase("proctoring-recordings"); q.onsuccess = q.onerror = q.onblocked = () => r(); }));
  await page.reload({ waitUntil: "networkidle0" });

  await page.waitForFunction(() => (document.querySelector(".preview-video")?.videoWidth ?? 0) > 0, { timeout: 15000 });
  check("live webcam preview visible", true);

  await page.waitForFunction(() => [...document.querySelectorAll("button")].some((b) => b.textContent?.includes("Start Monitoring") && !b.disabled), { timeout: 15000 });
  await clickButton(page, "Start Monitoring");
  await page.waitForSelector(".mon-badge", { timeout: 20000 });
  check("monitoring starts (VAD loaded, encoders running)", true);
  const t0 = Date.now();

  // Sample the UI while monitoring: REC appears only during events.
  let sawRec = false;
  let sawIdle = false;
  let cardsDuring = 0;
  while (Date.now() - t0 < SECONDS * 1000) {
    const s = await page.evaluate(() => ({ rec: !!document.querySelector(".badge-row .rec-badge"), cards: document.querySelectorAll(".saved-card").length }));
    if (s.rec) sawRec = true;
    else sawIdle = true;
    cardsDuring = s.cards;
    if (sawRec && s.cards === 0 && Date.now() - t0 < 2000) await page.screenshot({ path: join(OUT, "capturing.png") });
    await sleep(250);
  }
  const monitoredSec = (Date.now() - t0) / 1000;
  check("REC indicator shown during speech, not continuously", sawRec && sawIdle);
  check("event cards appear while monitoring continues", cardsDuring >= 1, `${cardsDuring} cards before stop`);
  await page.screenshot({ path: join(OUT, "monitoring.png"), fullPage: false });

  await clickButton(page, "Stop Monitoring");
  await page.waitForFunction(() => [...document.querySelectorAll("button")].some((b) => b.textContent?.includes("Start Monitoring")), { timeout: 20000 });
  await sleep(1000);

  const cards = await page.$$eval(".saved-card", (els) =>
    els.map((el) => ({
      key: el.getAttribute("data-event-key"),
      title: el.querySelector("h3")?.textContent,
      times: el.querySelector(".saved-times")?.textContent,
      status: el.querySelector(".saved-pill")?.textContent,
      video: el.querySelector("a[download$='.webm']")?.getAttribute("download"),
      audio: el.querySelector("a[download$='_audio.wav']")?.getAttribute("download"),
    })),
  );
  const durations = cards.map((c) => parseFloat(c.times.split("·")[2]));
  const savedSec = durations.reduce((a, b) => a + b, 0);
  // The fixture contains 2 utterances per loop; Chrome loops it as the microphone.
  const loopSec = Number(execFileSync("ffprobe", ["-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", WAV]).toString().trim());
  const expected = Math.floor((monitoredSec / loopSec) * 2);
  check("several events saved", cards.length >= Math.max(2, expected - 1), `${cards.length} events in ${monitoredSec.toFixed(0)} s, fixture has ~${expected} utterances in that time`);
  check("every card SAVED with event_<id>.webm + event_<id>_audio.wav", cards.every((c) => c.status === "SAVED" && /^event_[0-9a-f]{12}(_part\d+)?\.webm$/.test(c.video) && c.audio === c.video.replace(".webm", "_audio.wav")));
  check("only short clips, no whole-session video", Math.max(...durations) < 15 && savedSec < monitoredSec, `longest ${Math.max(...durations).toFixed(1)} s, total saved ${savedSec.toFixed(1)} s of ${monitoredSec.toFixed(0)} s`);
  // The earliest event can begin at (or before) the moment monitoring started, when no
  // earlier footage exists yet, so it is exempt.
  const pre = cards.map((c) => parseFloat(c.times.match(/includes (\d+\.\d) s/)?.[1] ?? "0"));
  check("each clip includes pre-event footage (>= 1.5 s)", pre.slice(0, -1).every((x) => x >= 1.5), pre.join(", ") + " s (last = first event of the session)");

  // Download every pair (exact bytes behind the players / download links).
  for (const c of cards) {
    const pair = await page.$eval(`.saved-card[data-event-key="${c.key}"]`, async (card) => {
      const b64 = async (url) => {
        const bytes = new Uint8Array(await (await fetch(url)).arrayBuffer());
        let s = "";
        for (let k = 0; k < bytes.length; k += 0x8000) s += String.fromCharCode(...bytes.subarray(k, k + 0x8000));
        return btoa(s);
      };
      return { v: await b64(card.querySelector("a[download$='.webm']").href), a: await b64(card.querySelector("a[download$='_audio.wav']").href) };
    });
    writeFileSync(join(OUT, c.video), Buffer.from(pair.v, "base64"));
    writeFileSync(join(OUT, c.audio), Buffer.from(pair.a, "base64"));
  }
  check("all event files downloaded", true, `${cards.length * 2} files -> ${OUT}`);
  writeFileSync(join(OUT, "events.json"), JSON.stringify(cards, null, 2));

  // Playback of one event: video with sound, and audio-only.
  const play = await page.$eval(".saved-card", async (card) => {
    const v = card.querySelector("video");
    const a = card.querySelector("audio");
    v.muted = false;
    await v.play();
    await new Promise((r) => setTimeout(r, 1200));
    const r = { vt: v.currentTime, vw: v.videoWidth, vDur: v.duration, decoded: v.webkitAudioDecodedByteCount };
    v.pause();
    await a.play();
    await new Promise((r) => setTimeout(r, 1000));
    r.at = a.currentTime;
    r.aDur = a.duration;
    a.pause();
    return r;
  });
  check("event video plays with picture and sound", play.vt > 0.8 && play.vw > 0 && play.decoded > 0, `t=${play.vt.toFixed(2)} s, ${play.vw}px, ${play.decoded} audio bytes`);
  check("event audio-only file plays", play.at > 0.6, `t=${play.at.toFixed(2)} s`);
  check("video and audio durations match", Number.isFinite(play.vDur) && Math.abs(play.vDur - play.aDur) < 0.15, `video ${play.vDur.toFixed(2)} s, audio ${play.aDur.toFixed(2)} s`);

  // Persistence and a second monitoring run without refresh.
  await page.reload({ waitUntil: "domcontentloaded", timeout: 60000 });
  await page.waitForSelector(".saved-card", { timeout: 30000 });
  const after = await page.$$eval(".saved-card", (e) => e.length);
  check("events persist after reload (IndexedDB)", after === cards.length, `${after} cards`);
  try {
    await page.waitForFunction(() => [...document.querySelectorAll("button")].some((b) => b.textContent?.includes("Start Monitoring") && !b.disabled), { timeout: 15000 });
  } catch (err) {
    const state = await page.evaluate(() => ({
      devices: [...document.querySelectorAll(".device")].map((d) => `${d.querySelector(".device-name")?.textContent}: ${d.querySelector(".device-state")?.textContent} (${d.getAttribute("title")})`),
      notice: document.querySelector(".notice")?.textContent,
      buttons: [...document.querySelectorAll("button")].map((b) => `${b.textContent}${b.disabled ? " [disabled]" : ""}`),
    }));
    console.log("state after reload:", JSON.stringify(state, null, 1));
    throw err;
  }
  await clickButton(page, "Start Monitoring");
  await page.waitForSelector(".mon-badge", { timeout: 20000 });
  await page.waitForFunction((n) => document.querySelectorAll(".saved-card").length > n, { timeout: 30000 }, after);
  await clickButton(page, "Stop Monitoring");
  check("monitoring can be restarted and saves new events", true);

  check("no console errors", consoleErrors.length === 0, consoleErrors.slice(0, 3).join(" | "));
  writeFileSync(join(OUT, "events.json"), JSON.stringify(cards, null, 2));
} catch (err) {
  await dumpState(String(err.message).split(/\r?\n/)[0]);
  throw err;
} finally {
  await browser.close();
}

writeFileSync(join(OUT, "e2e_results.json"), JSON.stringify(results, null, 2));
const failed = results.filter((r) => !r.ok).length;
console.log(`\n${results.length - failed}/${results.length} checks passed`);
process.exit(failed ? 1 : 0);
