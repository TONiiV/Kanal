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
 * host's next 15 s snapshot heartbeat. The room remembers the last snapshot envelope and
 * replays it right after `gateway.session` — one ordinary `{type:"relay"}` frame, which the
 * phone already verifies and applies like any other.
 */
describe("snapshot replay", () => {
  const snapshot = envelope({ type: "room.snapshot", snapshot: { utterances: [] } }, "c25hcDE");
  const laterSnapshot = envelope({ type: "room.snapshot", snapshot: { utterances: [1] } }, "c25hcDI");
  const utterance = envelope({ type: "utterance.upsert", utterance: { id: "u1" } }, "dXR0");

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

  it("does not cache envelopes that are not snapshots", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110004-ReplayUtterOnly");
    await post("publish", { payload: utterance }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(1);
  });

  it("keeps the snapshot as the replayed frame when later utterances arrive", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110005-ReplayThenUtter");
    await post("publish", { payload: snapshot }, room.hostTicket);
    await post("publish", { payload: utterance }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 2);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(2);
    expect(reader.messages[1]).toEqual({ type: "relay", payload: snapshot });
  });

  it("forgets the snapshot once the room is closed", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110006-ReplayAfterEnd");
    await post("publish", { payload: snapshot }, room.hostTicket);
    await post("publish", { payload: envelope({ type: "room.closed" }, "Y2xz") }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(1);
  });

  it("forgets the snapshot once the room has moved", async () => {
    const { deviceToken } = await activateDevice();
    const room = await createRoom(deviceToken, "kanal-110007-ReplayAfterMove");
    await post("publish", { payload: snapshot }, room.hostTicket);
    const moved = envelope(
      { type: "room.moved", newRoomId: "kanal-110008-ReplayMoveTarget" },
      "bXZk",
    );
    await post("publish", { payload: moved }, room.hostTicket);

    const reader = (await openReader(room.inviteTicket)) as Reader;
    await until(() => reader.messages.length >= 1);
    await new Promise((resolve) => setTimeout(resolve, 300));
    expect(reader.messages.length).toBe(1);
  });

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
