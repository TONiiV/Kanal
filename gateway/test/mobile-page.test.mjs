import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import vm from "node:vm";

const html = readFileSync(new URL("../../web/index.html", import.meta.url), "utf8");

function sourceOf(name) {
  const marker = `function ${name}(`;
  const start = html.indexOf(marker);
  assert.notEqual(start, -1, `${name} is missing from the shipped phone page`);
  const body = html.indexOf("{", start);
  let depth = 0;
  for (let i = body; i < html.length; i++) {
    if (html[i] === "{") depth++;
    if (html[i] === "}" && --depth === 0) return html.slice(start, i + 1);
  }
  throw new Error(`${name} has no closing brace`);
}

function harness(cached = null) {
  const notice = { text: "", shown: false };
  const state = {
    recording: false,
    transcribing: false,
    paused: false,
    closed: false,
    speakers: new Map(),
    aliases: new Map(),
    utterances: new Map(),
  };
  const context = vm.createContext({
    state,
    CACHE_KEY: "kanal.cache::test",
    $: () => ({
      querySelector: () => ({
        get textContent() { return notice.text; },
        set textContent(value) { notice.text = value; },
      }),
      classList: { toggle: (_, value) => { notice.shown = value; } },
    }),
    t: (key) => key,
    lifecycleStatus: () => state.closed ? "ended" : state.paused ? "paused" : "",
    setStatus: () => {},
    saveCache: () => {},
    applyConfig: () => {},
    applySpeaker: () => {},
    renderAll: () => {},
    localStorage: { getItem: () => cached === null ? null : JSON.stringify(cached) },
  });
  vm.runInContext([
    "setRecordingNotice", "applyRecording", "applyTranscribing", "applyPaused",
    "applyClosed", "applySnapshot", "loadCache",
  ].map(sourceOf).join("\n"), context);
  return { context, notice, state, run: (code) => vm.runInContext(code, context) };
}

test("recording replaces transcription, pause holds it, and close clears it", () => {
  const h = harness();

  h.run("applyTranscribing(true)");
  assert.deepEqual(h.notice, { text: "live", shown: true });

  h.run("applyRecording(true)");
  assert.deepEqual(h.notice, { text: "rec", shown: true });

  h.run("applyPaused(true)");
  assert.deepEqual(h.notice, { text: "recHeld", shown: true });

  h.run("applyRecording(false)");
  assert.deepEqual(h.notice, { text: "liveHeld", shown: true });

  h.run("applyClosed()");
  assert.equal(h.state.transcribing, false);
  assert.deepEqual(h.notice, { text: "", shown: false });
});

test("snapshot and cache restore authoritative transcription state", () => {
  const snapshot = harness();
  snapshot.context.snap = {
    config: { languages: ["zh"] }, speakers: [], utterances: [],
    paused: false, recording: false, transcribing: true,
  };
  snapshot.run("applySnapshot(snap)");
  assert.deepEqual(snapshot.notice, { text: "live", shown: true });

  const cached = harness({
    languages: ["zh"], speakers: [], utterances: [], closed: false,
    paused: true, recording: false, transcribing: true,
  });
  cached.run("loadCache()");
  assert.deepEqual(cached.notice, { text: "liveHeld", shown: true });
});
