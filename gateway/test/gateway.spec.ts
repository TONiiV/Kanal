import { SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";

/**
 * The wire protocol under test is the one the desktop (`GatewayRelayPublisher`) and the
 * phone page (`web/index.html`) already speak: `?action=create|publish|stream`, bearer
 * tickets, the `ticket.` WebSocket subprotocol, `gateway.session`, and `relay.signed`
 * envelope pass-through. Nothing here may change without changing both clients.
 */

const GW = "https://gateway.test/";
const ADMIN = "test-admin-token-0123456789-0123456789";
const ROOM = "kanal-093015-AbCdEfGhIjKlMnOp";
const VK = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE_fake_spki_key_material_0123456789";

function post(action: string, body: unknown, token?: string, headers?: Record<string, string>) {
  return SELF.fetch(`${GW}?action=${action}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...headers,
    },
    body: JSON.stringify(body),
  });
}

/** Admin mints a one-time activation code; the desktop trades it for its own credential. */
async function activateDevice(name = "test-laptop"): Promise<{ deviceId: string; deviceToken: string }> {
  const codeResponse = await post("admin.code", { note: name }, ADMIN);
  expect(codeResponse.status).toBe(200);
  const { code } = await codeResponse.json<{ code: string }>();
  const activated = await post("activate", { code, deviceName: name });
  expect(activated.status).toBe(200);
  return activated.json();
}

async function createRoom(deviceToken: string, room = ROOM, vk = VK) {
  const response = await post("create", { roomId: room, verificationKey: vk }, deviceToken);
  expect(response.status).toBe(200);
  return response.json<{ hostTicket: string; inviteTicket: string; expiresAt: number }>();
}

interface Reader {
  socket: WebSocket;
  protocol: string | null;
  messages: unknown[];
  /** The frames exactly as they arrived, for assertions about byte-for-byte pass-through. */
  raw: string[];
  closes: { code: number; reason: string }[];
}

async function openReader(ticket: string, origin?: string): Promise<Reader | Response> {
  const response = await SELF.fetch(`${GW}?action=stream`, {
    headers: {
      Upgrade: "websocket",
      "Sec-WebSocket-Protocol": `kanal, ticket.${ticket}`,
      ...(origin ? { Origin: origin } : {}),
    },
  });
  if (response.status !== 101) return response;
  const socket = response.webSocket!;
  const reader: Reader = {
    socket,
    protocol: response.headers.get("Sec-WebSocket-Protocol"),
    messages: [],
    raw: [],
    closes: [],
  };
  socket.accept();
  socket.addEventListener("message", (event) => {
    if (typeof event.data === "string") reader.raw.push(event.data);
    reader.messages.push(
      typeof event.data === "string" && event.data.startsWith("{")
        ? JSON.parse(event.data)
        : event.data,
    );
  });
  socket.addEventListener("close", (event) => {
    reader.closes.push({ code: event.code, reason: event.reason });
  });
  return reader;
}

async function until(condition: () => boolean, ms = 2000): Promise<void> {
  const deadline = Date.now() + ms;
  while (!condition() && Date.now() < deadline) {
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
  expect(condition()).toBe(true);
}

const signedEnvelope = {
  type: "relay.signed",
  version: 1,
  data: "eyJ0eXBlIjoicm9vbS5zbmFwc2hvdCJ9",
  signature: "c2ln",
};

/**
 * The desktop's `SignedRelayMessage`: the exact relay JSON, base64url-encoded, plus a P-256
 * signature over it. Signed, not encrypted — the discriminator is readable to the gateway and
 * verified by the phone.
 */
function envelope(message: unknown, signature = "c2ln") {
  const data = btoa(JSON.stringify(message))
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
  return { type: "relay.signed", version: 1, data, signature };
}

describe("device authorization", () => {
  it("minting an activation code requires the admin token", async () => {
    const response = await post("admin.code", {}, "wrong-token-0123456789-0123456789-xx");
    expect(response.status).toBe(401);
  });

  it("an activation code is single use", async () => {
    const codeResponse = await post("admin.code", {}, ADMIN);
    const { code } = await codeResponse.json<{ code: string }>();
    expect((await post("activate", { code, deviceName: "first" })).status).toBe(200);
    expect((await post("activate", { code, deviceName: "second" })).status).toBe(401);
  });

  it("a made-up activation code is rejected", async () => {
    const response = await post("activate", { code: "not-a-real-code", deviceName: "x" });
    expect(response.status).toBe(401);
  });

  it("device tokens satisfy the desktop's 32-byte minimum", async () => {
    const { deviceToken } = await activateDevice();
    expect(new TextEncoder().encode(deviceToken).byteLength).toBeGreaterThanOrEqual(32);
  });

  it("a revoked device can no longer create rooms", async () => {
    const { deviceId, deviceToken } = await activateDevice("stolen-laptop");
    await createRoom(deviceToken);
    const revoke = await post("admin.revoke", { deviceId }, ADMIN);
    expect(revoke.status).toBe(200);
    const denied = await post(
      "create",
      { roomId: ROOM, verificationKey: VK },
      deviceToken,
    );
    expect(denied.status).toBe(401);
  });

  it("admin can list devices with names and revocation state", async () => {
    const { deviceId } = await activateDevice("meeting-laptop");
    const response = await SELF.fetch(`${GW}?action=admin.devices`, {
      headers: { Authorization: `Bearer ${ADMIN}` },
    });
    expect(response.status).toBe(200);
    const { devices } = await response.json<{
      devices: { deviceId: string; name: string; revoked: boolean }[];
    }>();
    const mine = devices.find((device) => device.deviceId === deviceId);
    expect(mine).toBeDefined();
    expect(mine!.name).toBe("meeting-laptop");
    expect(mine!.revoked).toBe(false);
  });
});

describe("room creation", () => {
  it("requires a device token", async () => {
    const response = await post("create", { roomId: ROOM, verificationKey: VK });
    expect(response.status).toBe(401);
  });

  it("rejects the shared admin token as a room credential", async () => {
    const response = await post("create", { roomId: ROOM, verificationKey: VK }, ADMIN);
    expect(response.status).toBe(401);
  });

  it("rejects malformed room metadata", async () => {
    const { deviceToken } = await activateDevice();
    const response = await post(
      "create",
      { roomId: "../../etc", verificationKey: VK },
      deviceToken,
    );
    expect(response.status).toBe(400);
  });

  it("returns distinct host and invite tickets with an expiry", async () => {
    const { deviceToken } = await activateDevice();
    const { hostTicket, inviteTicket, expiresAt } = await createRoom(deviceToken);
    expect(hostTicket).not.toBe(inviteTicket);
    expect(expiresAt).toBeGreaterThan(Math.floor(Date.now() / 1000));
  });
});

describe("publish", () => {
  it("requires a host ticket", async () => {
    const response = await post("publish", { payload: signedEnvelope });
    expect(response.status).toBe(401);
  });

  it("rejects the reader invite ticket for publishing", async () => {
    const { deviceToken } = await activateDevice();
    const { inviteTicket } = await createRoom(deviceToken);
    const response = await post("publish", { payload: signedEnvelope }, inviteTicket);
    expect(response.status).toBe(401);
  });

  it("accepts only signed relay envelopes", async () => {
    const { deviceToken } = await activateDevice();
    const { hostTicket } = await createRoom(deviceToken);
    const response = await post(
      "publish",
      { payload: { type: "room.snapshot" } },
      hostTicket,
    );
    expect(response.status).toBe(400);
  });

  it("rejects oversized payloads", async () => {
    const { deviceToken } = await activateDevice();
    const { hostTicket } = await createRoom(deviceToken);
    const response = await post(
      "publish",
      { payload: { ...signedEnvelope, data: "x".repeat(300 * 1024) } },
      hostTicket,
    );
    expect(response.status).toBe(413);
  });
});

describe("reader stream", () => {
  it("requires a WebSocket upgrade", async () => {
    const response = await SELF.fetch(`${GW}?action=stream`);
    expect(response.status).toBe(426);
  });

  it("rejects a forged ticket", async () => {
    const result = await openReader("forged.ticket");
    expect(result).toBeInstanceOf(Response);
    expect((result as Response).status).toBe(401);
  });

  it("rejects the host ticket for reading", async () => {
    const { deviceToken } = await activateDevice();
    const { hostTicket } = await createRoom(deviceToken);
    const result = await openReader(hostTicket);
    expect((result as Response).status).toBe(401);
  });

  it("opens with the kanal subprotocol and announces the session", async () => {
    const { deviceToken } = await activateDevice();
    const { inviteTicket, expiresAt } = await createRoom(deviceToken);
    const reader = (await openReader(inviteTicket)) as Reader;
    // Browsers abort the connection unless the server echoes an offered subprotocol.
    expect(reader.protocol).toBe("kanal");
    await until(() => reader.messages.length >= 1);
    expect(reader.messages[0]).toEqual({
      type: "gateway.session",
      room: ROOM,
      verificationKey: VK,
      expiresAt,
    });
  });

  it("delivers published envelopes to a connected reader", async () => {
    const { deviceToken } = await activateDevice();
    const { hostTicket, inviteTicket } = await createRoom(deviceToken);
    const reader = (await openReader(inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1); // session first

    const published = await post("publish", { payload: signedEnvelope }, hostTicket);
    expect(published.status).toBe(200);

    await until(() => reader.messages.length >= 2);
    expect(reader.messages[1]).toEqual({ type: "relay", payload: signedEnvelope });
  });

  it("a reader in one room hears nothing from another room", async () => {
    const { deviceToken } = await activateDevice();
    const roomA = await createRoom(deviceToken, "kanal-100000-RoomAaaaaaaaaaaa");
    const roomB = await createRoom(deviceToken, "kanal-100001-RoomBbbbbbbbbbbb");
    const readerB = (await openReader(roomB.inviteTicket)) as Reader;
    await until(() => readerB.messages.length >= 1);

    await post("publish", { payload: signedEnvelope }, roomA.hostTicket);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(readerB.messages.length).toBe(1); // still only the session message
  });

  it("answers ping with pong", async () => {
    const { deviceToken } = await activateDevice();
    const { inviteTicket } = await createRoom(deviceToken);
    const reader = (await openReader(inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    reader.socket.send("ping");
    await until(() => reader.messages.includes("pong"));
  });

  it("closes readers that try to talk", async () => {
    const { deviceToken } = await activateDevice();
    const { inviteTicket } = await createRoom(deviceToken);
    const reader = (await openReader(inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    reader.socket.send(JSON.stringify({ type: "speaker.rename" }));
    await until(() => reader.closes.length >= 1);
    expect(reader.closes[0].code).toBe(1008);
  });
});

describe("origin policy", () => {
  it("denies browser requests from an unexpected origin", async () => {
    const { deviceToken } = await activateDevice();
    const { inviteTicket } = await createRoom(deviceToken);
    const result = await openReader(inviteTicket, "https://evil.example");
    expect((result as Response).status).toBe(403);
  });

  it("allows the configured mobile-page origin", async () => {
    const { deviceToken } = await activateDevice();
    const { inviteTicket } = await createRoom(deviceToken);
    const reader = await openReader(inviteTicket, "https://toniiv.github.io");
    expect(reader).not.toBeInstanceOf(Response);
  });

  it("answers preflight for the allowed origin", async () => {
    const response = await SELF.fetch(`${GW}?action=create`, {
      method: "OPTIONS",
      headers: { Origin: "https://toniiv.github.io" },
    });
    expect(response.status).toBe(204);
    expect(response.headers.get("Access-Control-Allow-Origin")).toBe(
      "https://toniiv.github.io",
    );
  });
});

/**
 * A phone that reconnects mid-meeting must not stare at its `localStorage` cache until the
 * host's next 15 s snapshot heartbeat. The room remembers the last snapshot envelope *and every
 * frame published after it*, and replays that sequence right after `gateway.session` — ordinary
 * `{type:"relay"}` frames, which the phone already verifies and applies like any other.
 *
 * The tail is not optional. `applySnapshot()` on the phone clears its speakers, aliases, and
 * utterances before repopulating, while every incremental frame is written straight to
 * `localStorage`. A phone reconnecting between heartbeats therefore holds *newer* state than the
 * snapshot, and a snapshot replayed alone would visibly delete up to 15 s of transcript.
 */
describe("snapshot replay", () => {
  const snapshot = envelope({ type: "room.snapshot", snapshot: { utterances: [] } }, "c25hcDE");
  const laterSnapshot = envelope({ type: "room.snapshot", snapshot: { utterances: [1] } }, "c25hcDI");
  const utterance = envelope({ type: "utterance.upsert", utterance: { id: "u1" } }, "dXR0");
  const secondUtterance = envelope({ type: "utterance.upsert", utterance: { id: "u2" } }, "dXR0Mg");
  const closed = envelope({ type: "room.closed" }, "Y2xz");
  const moved = envelope(
    { type: "room.moved", newRoomId: "kanal-110008-ReplayMoveTarget" },
    "bXZk",
  );
  /** Mirrors `MAX_REPLAY_BYTES` in `gateway/src/index.ts`. */
  const MAX_REPLAY_BYTES = 4 * 256 * 1024;
  /**
   * Every test that fills the buffer to a cap gets this instead of vitest's 5 s default. Their
   * runtime is a function of `MAX_REPLAY_FRAMES` / `MAX_REPLAY_BYTES` and of how fast the machine
   * answers a publish, so a CI runner slower than a laptop — or anyone raising a cap — pushes them
   * over. That matters more than a slow test: a per-test timeout firing mid-request corrupts
   * `vitest-pool-workers`' isolated-storage teardown and takes unrelated tests down with it, so the
   * suite fails somewhere else entirely. Do not remove these as noise; see `gateway/README.md`.
   */
  const CAP_TIMEOUT_MS = 60_000;
  /** The bytes the room accounts for one envelope: the frame it fans out, in UTF-8. */
  const frameBytes = (payload: unknown) =>
    new TextEncoder().encode(JSON.stringify({ type: "relay", payload })).byteLength;
  /**
   * The largest utterance envelope whose frame still fits in `bytes`. Base64 makes the mapping
   * from text length to frame size non-linear, so the length is searched rather than computed;
   * it lands within a byte or two of the target.
   */
  function sizedEnvelope(id: string, bytes: number) {
    const at = (length: number) =>
      envelope({ type: "utterance.upsert", utterance: { id, text: "x".repeat(length) } }, "ZmlsbA");
    let low = 0;
    let high = bytes;
    while (low < high) {
      const mid = Math.ceil((low + high) / 2);
      if (frameBytes(at(mid)) <= bytes) low = mid;
      else high = mid - 1;
    }
    return at(low);
  }
  /** ~200 KiB on the wire: under the per-frame `MAX_PAYLOAD_BYTES`, six of them over the buffer cap. */
  const bulky = (index: number) =>
    envelope(
      { type: "utterance.upsert", utterance: { id: `big-${index}`, text: "x".repeat(150_000) } },
      `Ymln${index}`,
    );

  it("greets a reader that joins after a snapshot with that snapshot", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110000-ReplayJoinLate");
    expect((await post("publish", { payload: snapshot }, room.hostTicket)).status).toBe(200);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 2);
    expect((reader.messages[0] as { type: string }).type).toBe("gateway.session");
    expect(reader.messages[1]).toEqual({ type: "relay", payload: snapshot });
  });

  it("replays the envelope byte for byte", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110001-ReplayVerbatim");
    const live = (await openReader(room.inviteTicket)) as Reader;
    await until(() => live.messages.length >= 1);
    await post("publish", { payload: laterSnapshot }, room.hostTicket);
    await until(() => live.messages.length >= 2);

    const rejoined = (await openReader(room.inviteTicket)) as Reader;
    await until(() => rejoined.raw.length >= 2);
    // The replayed frame is the same bytes the live reader received, not a re-encoding.
    expect(rejoined.raw[1]).toBe(live.raw[1]);
    expect(JSON.parse(rejoined.raw[1]).payload).toEqual(laterSnapshot);
  });

  it("sends nothing but the session when no snapshot has been published", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110002-ReplayNoneYet");
    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(1);
  });

  it("replays only the most recent snapshot", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110003-ReplayLatestOne");
    await post("publish", { payload: snapshot }, room.hostTicket);
    await post("publish", { payload: laterSnapshot }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 2);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(2);
    expect(reader.messages[1]).toEqual({ type: "relay", payload: laterSnapshot });
  });

  it("ignores frames published before the first snapshot", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110004-ReplayUtterOnly");
    await post("publish", { payload: utterance }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(1);
  });

  it("replays the snapshot and every frame published after it, in order", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110005-ReplayThenUtter");
    await post("publish", { payload: snapshot }, room.hostTicket);
    await post("publish", { payload: utterance }, room.hostTicket);
    await post("publish", { payload: secondUtterance }, room.hostTicket);

    // Exactly the sequence a reader connected the whole time saw — no rollback, no gap.
    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 4);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(4);
    expect(reader.messages.slice(1)).toEqual([
      { type: "relay", payload: snapshot },
      { type: "relay", payload: utterance },
      { type: "relay", payload: secondUtterance },
    ]);
  });

  it("starts a fresh tail at each new snapshot", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110012-ReplayTailReset");
    await post("publish", { payload: snapshot }, room.hostTicket);
    await post("publish", { payload: utterance }, room.hostTicket);
    await post("publish", { payload: laterSnapshot }, room.hostTicket);

    // The heartbeat already contains that utterance; replaying it again would be noise.
    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 2);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(2);
    expect(reader.messages[1]).toEqual({ type: "relay", payload: laterSnapshot });
  });

  it("drops the snapshot with its tail when the buffer outgrows the byte cap", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110013-ReplayByteCap");
    await post("publish", { payload: snapshot }, room.hostTicket);
    for (let index = 0; index < 6; index++) {
      expect((await post("publish", { payload: bulky(index) }, room.hostTicket)).status).toBe(200);
    }

    // Truncating the tail would resurrect the rollback bug; dropping everything only degrades
    // to waiting for the next heartbeat, which is what the phone did before replay existed.
    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(1);
  }, CAP_TIMEOUT_MS);

  it("drops the snapshot with its tail when the buffer outgrows the frame cap", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110014-ReplayFrameCap");
    await post("publish", { payload: snapshot }, room.hostTicket);
    for (let index = 0; index < 257; index++) {
      await post(
        "publish",
        { payload: envelope({ type: "utterance.upsert", utterance: { id: `u${index}` } }, "dGFpbA") },
        room.hostTicket,
      );
    }

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(1);
  }, CAP_TIMEOUT_MS);

  it("replays again from the next snapshot after an overflow", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110015-ReplayAfterFlood");
    await post("publish", { payload: snapshot }, room.hostTicket);
    for (let index = 0; index < 6; index++) {
      await post("publish", { payload: bulky(index) }, room.hostTicket);
    }
    await post("publish", { payload: laterSnapshot }, room.hostTicket);
    await post("publish", { payload: utterance }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 3);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(3);
    expect(reader.messages.slice(1)).toEqual([
      { type: "relay", payload: laterSnapshot },
      { type: "relay", payload: utterance },
    ]);
  }, CAP_TIMEOUT_MS);

  it("replays the close announcement after the snapshot and its tail", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110006-ReplayAfterEnd");
    // The host's stop sequence: a final snapshot, then the announcement (MainViewModel.cs).
    await post("publish", { payload: snapshot }, room.hostTicket);
    await post("publish", { payload: utterance }, room.hostTicket);
    await post("publish", { payload: closed }, room.hostTicket);

    // A phone locked when the meeting ended reconnects to a stopped host: nothing will ever
    // arrive to correct it, so the two frames it missed are the two it most needs.
    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 4);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(4);
    expect(reader.messages.slice(1)).toEqual([
      { type: "relay", payload: snapshot },
      { type: "relay", payload: utterance },
      { type: "relay", payload: closed },
    ]);
  });

  it("replays the move announcement so a reader on the dead room follows it", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110007-ReplayAfterMove");
    await post("publish", { payload: snapshot }, room.hostTicket);
    await post("publish", { payload: moved }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 3);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(3);
    expect(reader.messages[2]).toEqual({ type: "relay", payload: moved });
  });

  it("buffers nothing more once the room is terminal", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110016-ReplayTerminal");
    await post("publish", { payload: snapshot }, room.hostTicket);
    await post("publish", { payload: closed }, room.hostTicket);
    // The host has stopped; nothing legitimate follows the announcement.
    await post("publish", { payload: utterance }, room.hostTicket);
    await post("publish", { payload: laterSnapshot }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 3);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(3);
    expect(reader.messages.slice(1)).toEqual([
      { type: "relay", payload: snapshot },
      { type: "relay", payload: closed },
    ]);
  });

  it("replays the restart's move in place of the close it followed", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110018-ReplayRestart");
    // The real Stop → Start sequence. `_relay` outlives its session by design
    // (`MainViewModel`: "Outlives its session: the next Start uses it to redirect phones to the
    // new room"), so StopAsync publishes the final snapshot and `room.closed` here, and the next
    // StartAsync publishes `room.moved` on this same, already-closed room before disposing it.
    await post("publish", { payload: snapshot }, room.hostTicket);
    await post("publish", { payload: utterance }, room.hostTicket);
    await post("publish", { payload: closed }, room.hostTicket);
    await post("publish", { payload: moved }, room.hostTicket);

    // A phone locked across the restart must follow it, not sit on "ended" for good. The move
    // supersedes the close: the room it names is where the meeting actually is.
    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 4);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(4);
    expect(reader.messages.slice(1)).toEqual([
      { type: "relay", payload: snapshot },
      { type: "relay", payload: utterance },
      { type: "relay", payload: moved },
    ]);
  });

  it("keeps the close announcement when the announcement is what overflows the buffer", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110019-ReplayEndAtCap");
    // Fill to the byte cap exactly, so the announcement is the frame that pushes the buffer
    // over — the production path. Overflowing beforehand, as the test below does, leaves the
    // announcement arriving at an empty buffer and never exercises the retry at all.
    let used = frameBytes(snapshot);
    await post("publish", { payload: snapshot }, room.hostTicket);
    for (let index = 0; index < 5; index++) {
      const filler = bulky(index);
      used += frameBytes(filler);
      await post("publish", { payload: filler }, room.hostTicket);
    }
    const last = sizedEnvelope("last", MAX_REPLAY_BYTES - used);
    await post("publish", { payload: last }, room.hostTicket);
    // Within a byte or two of the cap, and an announcement is ~150 bytes: it cannot fit.
    expect(MAX_REPLAY_BYTES - used - frameBytes(last)).toBeLessThan(frameBytes(closed));
    await post("publish", { payload: closed }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 2);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(2);
    expect(reader.messages[1]).toEqual({ type: "relay", payload: closed });
  }, CAP_TIMEOUT_MS);

  it("keeps the close announcement even when the buffer has already overflowed", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110017-ReplayEndOverflow");
    await post("publish", { payload: snapshot }, room.hostTicket);
    for (let index = 0; index < 6; index++) {
      await post("publish", { payload: bulky(index) }, room.hostTicket);
    }
    await post("publish", { payload: closed }, room.hostTicket);

    // A lone announcement is safe where a lone snapshot is not: `applyClosed()` sets a flag and
    // leaves the transcript alone, so the phone's own cache stays, correctly marked ended.
    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 2);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(2);
    expect(reader.messages[1]).toEqual({ type: "relay", payload: closed });
  }, CAP_TIMEOUT_MS);

  it("never replays one room's snapshot into another room", async () => {
    const { deviceToken } = await activateDevice();
    const roomA = await createRoom(deviceToken, "kanal-110009-ReplayIsolateA");
    const roomB = await createRoom(deviceToken, "kanal-110010-ReplayIsolateB");
    await post("publish", { payload: snapshot }, roomA.hostTicket);

    const reader = (await openReader(roomB.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(1);
  });

  it("still fans a snapshot out live to readers already connected", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110011-ReplayLiveToo");
    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);

    await post("publish", { payload: snapshot }, room.hostTicket);
    await until(() => reader.messages.length >= 2);
    expect(reader.messages[1]).toEqual({ type: "relay", payload: snapshot });
    // The reader that was already there is not sent the cached copy a second time.
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(2);
  });
});
