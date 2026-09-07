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

/* Taken from the page rather than restated here: a test that carries its own copy of the
   close code stays green while the two drift apart. */
function constantOf(name) {
  const match = html.match(new RegExp(`^const ${name} = [^;]+;`, "m"));
  assert.ok(match, `${name} is missing from the shipped phone page`);
  return match[0];
}

function harness(cached = null) {
  const notice = { text: "", shown: false };
  const saved = [];
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
    saveCache: () => { saved.push(true); },
    applyConfig: () => {},
    applySpeaker: () => {},
    renderAll: () => {},
    localStorage: { getItem: () => cached === null ? null : JSON.stringify(cached) },
    atob: (value) => Buffer.from(value, "base64").toString("binary"),
    TextDecoder,
    Uint8Array,
  });
  vm.runInContext([
    constantOf("ROOM_EXPIRED_CLOSE"),
    ...[
      "setRecordingNotice", "applyRecording", "applyTranscribing", "applyPaused",
      "showRoomEnded", "applyClosed", "applySnapshot", "loadCache", "stopFollowing",
      "decodeBase64Url", "ticketExpiry", "ticketLapsed", "roomExpired",
    ].map(sourceOf),
  ].join("\n"), context);
  return { context, notice, state, saved, run: (code) => vm.runInContext(code, context) };
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

test("the room's own 4001 is terminal, and every other close still reconnects", () => {
  const h = harness();
  const now = 1_800_000_000;
  const connected = { everConnected: true, expiresAt: now + 3600 };

  assert.equal(h.run(`roomExpired(${JSON.stringify(connected)}, 4001, ${now})`), true);
  assert.equal(h.run(`roomExpired(${JSON.stringify(connected)}, 1006, ${now})`), false);
  assert.equal(h.run(`roomExpired(${JSON.stringify(connected)}, 1000, ${now})`), false);

  // A phone with a wrong clock must not be able to declare a live meeting over.
  const lapsedButAnswering = { everConnected: true, expiresAt: now - 60 };
  assert.equal(h.run(`roomExpired(${JSON.stringify(lapsedButAnswering)}, 1006, ${now})`), false);
});

test("a page reloaded after the room ended stops before it asks", () => {
  const h = harness();
  const now = 1_800_000_000;

  // No 4001 can arrive here: the upgrade is refused before a socket exists.
  assert.equal(h.run(`ticketLapsed({everConnected: false, expiresAt: ${now - 60}}, ${now})`), true);
  assert.equal(h.run(`ticketLapsed({everConnected: false, expiresAt: ${now + 60}}, ${now})`), false);

  // A ticket the page cannot read is not a ticket the page may condemn.
  assert.equal(h.run(`ticketLapsed({everConnected: false, expiresAt: 0}, ${now})`), false);
});

test("a reader ticket's expiry is read from the ticket itself", () => {
  const h = harness();
  const claims = { v: 1, room: "kanal-x", vk: "k", iat: 1_800_000_000, exp: 1_800_043_200, role: "reader" };
  const payload = Buffer.from(JSON.stringify(claims)).toString("base64url");

  assert.equal(h.run(`ticketExpiry(${JSON.stringify(payload + ".c2ln")})`), 1_800_043_200);

  // Unreadable means "do not know", never "expired".
  assert.equal(h.run(`ticketExpiry("not-a-ticket")`), 0);
  assert.equal(h.run(`ticketExpiry("")`), 0);
  assert.equal(h.run(`ticketExpiry(${JSON.stringify(Buffer.from("{}").toString("base64url") + ".x")})`), 0);
});

test("an ending the room announced is remembered; one the page inferred is not", () => {
  const announced = harness();
  announced.run("stopFollowing({reconnect: true}, ROOM_EXPIRED_CLOSE)");
  assert.equal(announced.state.closed, true);
  assert.equal(announced.saved.length, 1);

  // Nothing would ever clear a cached "ended" the gateway never sent, and a phone with a
  // wrong clock whose first connect merely failed would carry it across every later reload.
  const inferred = harness();
  inferred.run("stopFollowing({reconnect: true}, 1006)");
  assert.equal(inferred.state.closed, false);
  assert.equal(inferred.saved.length, 0);

  // Either way the participant sees the same thing, and the recording banner goes out.
  assert.deepEqual(announced.notice, inferred.notice);
});
