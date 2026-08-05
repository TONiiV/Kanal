import { env, runInDurableObject, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import {
  ACTIVATE_MAX_ATTEMPTS,
  ACTIVATE_WINDOW_MS,
  ACTIVATION_CODE_TTL_MS,
} from "../src/index";

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
const HOUR = 60 * 60 * 1000;

let clientAddress = 0;

/**
 * Cloudflare overwrites `CF-Connecting-IP` at the edge, so the activation rate limit buckets
 * on it. Every request here comes from its own address unless a test deliberately pins one —
 * a shared bucket would make every case in this file share one budget.
 */
function nextClientIp(): string {
  clientAddress += 1;
  return `203.0.113.${clientAddress}`;
}

function post(action: string, body: unknown, token?: string, headers?: Record<string, string>) {
  return SELF.fetch(`${GW}?action=${action}`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      "CF-Connecting-IP": nextClientIp(),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...headers,
    },
    body: JSON.stringify(body),
  });
}

async function sha256Hex(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value));
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

/**
 * workerd's clock advances in real time, so a 24 h code lifetime can only be exercised by
 * moving the row backwards — never by waiting. Ages exactly one code so codes minted by
 * other cases in this file are untouched.
 */
async function ageCode(code: string, ms: number): Promise<void> {
  const codeHash = await sha256Hex(code);
  const stub = env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
  await runInDurableObject(stub, (_instance, state) => {
    state.storage.sql.exec(
      "UPDATE codes SET created_at = created_at - ? WHERE code_hash = ?",
      ms,
      codeHash,
    );
  });
}

/** The same trick as `ageCode`, for the limiter's fixed window: one caller, moved back. */
async function ageActivationWindow(client: string, ms: number): Promise<void> {
  const stub = env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
  await runInDurableObject(stub, (_instance, state) => {
    state.storage.sql.exec(
      "UPDATE activation_attempts SET window_start = window_start - ? WHERE client = ?",
      ms,
      client,
    );
  });
}

/**
 * Rows the registry reads to serve one activation attempt while `rows` other callers hold an
 * open window. Wraps the instance's own `SqlStorage` handle and sums `rowsRead` across every
 * cursor the call opens, so the measurement follows whatever statements `allowActivationAttempt`
 * actually runs — restating its SQL here would drift the moment the queries change.
 */
async function rowsReadForOneAttempt(rows: number): Promise<number> {
  const stub = env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
  return runInDurableObject(stub, (instance, state) => {
    const now = Date.now();
    for (let index = 0; index < rows; index++) {
      state.storage.sql.exec(
        "INSERT OR REPLACE INTO activation_attempts (client, window_start, attempts) VALUES (?, ?, 1)",
        `seed-${index}`,
        now,
      );
    }
    const registry = instance as unknown as { sql: SqlStorage };
    const real = registry.sql;
    const cursors: { rowsRead: number }[] = [];
    registry.sql = {
      exec: (query: string, ...bindings: unknown[]) => {
        const cursor = real.exec(query, ...bindings);
        cursors.push(cursor);
        return cursor;
      },
    } as unknown as SqlStorage;
    try {
      instance.allowActivationAttempt(`measured-${rows}`);
    } finally {
      registry.sql = real;
      state.storage.sql.exec(
        "DELETE FROM activation_attempts WHERE client LIKE 'seed-%' OR client LIKE 'measured-%'",
      );
    }
    return cursors.reduce((total, cursor) => total + cursor.rowsRead, 0);
  });
}

async function mintCode(note = "test"): Promise<string> {
  const response = await post("admin.code", { note }, ADMIN);
  expect(response.status).toBe(200);
  return (await response.json<{ code: string }>()).code;
}

async function deviceCount(): Promise<number> {
  const response = await SELF.fetch(`${GW}?action=admin.devices`, {
    headers: { Authorization: `Bearer ${ADMIN}` },
  });
  return (await response.json<{ devices: unknown[] }>()).devices.length;
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
    closes: [],
  };
  socket.accept();
  socket.addEventListener("message", (event) => {
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

  it("an activation code older than 24 h is refused and enrols nothing", async () => {
    const code = await mintCode("forgotten-in-shell-history");
    const before = await deviceCount();
    await ageCode(code, ACTIVATION_CODE_TTL_MS + HOUR);

    const response = await post("activate", { code, deviceName: "late" });
    // Deliberately the same 401 an unknown code gets: an attacker holding a leaked code
    // must not learn from the status whether it was ever real.
    expect(response.status).toBe(401);
    expect(await deviceCount()).toBe(before);
  });

  it("an expired activation code cannot be revived by retrying", async () => {
    const code = await mintCode("expired-then-hammered");
    await ageCode(code, ACTIVATION_CODE_TTL_MS + HOUR);

    for (let attempt = 0; attempt < 3; attempt++) {
      expect((await post("activate", { code, deviceName: "retry" })).status).toBe(401);
    }
  });

  it("an activation code just inside the 24 h window still works, once", async () => {
    const code = await mintCode("same-day");
    await ageCode(code, ACTIVATION_CODE_TTL_MS - HOUR);

    expect((await post("activate", { code, deviceName: "same-day" })).status).toBe(200);
    expect((await post("activate", { code, deviceName: "again" })).status).toBe(401);
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

/**
 * `?action=activate` is unauthenticated by necessity — a new desktop has nothing to
 * authenticate with — and it is the only route on which an anonymous caller can reach the
 * registry Durable Object. The limit bounds that reach per client address.
 */
describe("activation rate limit", () => {
  const from = (ip: string) => ({ "CF-Connecting-IP": ip });

  it("cuts a caller off after the per-address budget is spent", async () => {
    const attacker = from("198.51.100.10");
    for (let attempt = 0; attempt < ACTIVATE_MAX_ATTEMPTS; attempt++) {
      const guess = await post("activate", { code: `guess-${attempt}-0000` }, undefined, attacker);
      expect(guess.status).toBe(401);
    }
    const cut = await post("activate", { code: "guess-over-the-line" }, undefined, attacker);
    expect(cut.status).toBe(429);
  });

  it("does not spend a neighbouring caller's budget", async () => {
    const noisy = from("198.51.100.20");
    for (let attempt = 0; attempt <= ACTIVATE_MAX_ATTEMPTS; attempt++) {
      await post("activate", { code: `guess-${attempt}-000000` }, undefined, noisy);
    }
    const code = await mintCode("quiet-neighbour");
    const activated = await post(
      "activate",
      { code, deviceName: "quiet-neighbour" },
      undefined,
      from("198.51.100.21"),
    );
    expect(activated.status).toBe(200);
  });

  it("hands the budget back once the window has passed", async () => {
    const caller = "198.51.100.40";
    for (let attempt = 0; attempt <= ACTIVATE_MAX_ATTEMPTS; attempt++) {
      await post("activate", { code: `guess-${attempt}-0000000` }, undefined, from(caller));
    }
    const cut = await post("activate", { code: "still-inside-window" }, undefined, from(caller));
    expect(cut.status).toBe(429);

    await ageActivationWindow(caller, ACTIVATE_WINDOW_MS + 1000);

    const code = await mintCode("after-the-window");
    const activated = await post(
      "activate",
      { code, deviceName: "after-the-window" },
      undefined,
      from(caller),
    );
    expect(activated.status).toBe(200);
  });

  /** Spends the whole budget from one address and returns the status of the attempt after it. */
  async function spendBudgetFrom(address: string, label: string): Promise<number> {
    for (let attempt = 0; attempt < ACTIVATE_MAX_ATTEMPTS; attempt++) {
      await post("activate", { code: `guess-${label}-${attempt}` }, undefined, from(address));
    }
    return (await post("activate", { code: `guess-${label}-over` }, undefined, from(address)))
      .status;
  }

  /** Returns 200 only if `address` still has budget left to redeem a fresh code with. */
  async function activateFrom(address: string, label: string): Promise<number> {
    const code = await mintCode(label);
    return (await post("activate", { code, deviceName: label }, undefined, from(address))).status;
  }

  it("shares one budget across an IPv6 caller's whole /64", async () => {
    // One subscriber prefix, three spellings: fully expanded with leading zeros, and two
    // compressed forms. Keying on the exact address would give the same caller 2^64 budgets.
    expect(await spendBudgetFrom("2001:0db8:0001:0002:0000:0000:0000:0001", "same64")).toBe(429);
    expect(await activateFrom("2001:db8:1:2::beef", "sibling-in-prefix")).toBe(429);
    expect(await activateFrom("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "another-in-prefix")).toBe(429);
  });

  it("gives a different IPv6 /64 its own budget", async () => {
    expect(await spendBudgetFrom("2001:db8:1:3::1", "noisy64")).toBe(429);
    expect(await activateFrom("2001:db8:1:4::1", "quiet64")).toBe(200);
  });

  it("keeps every IPv4 caller on its own budget", async () => {
    expect(await spendBudgetFrom("198.51.100.50", "v4")).toBe(429);
    expect(await activateFrom("198.51.100.51", "neighbour-v4")).toBe(200);
  });

  it("keeps IPv4-mapped callers apart instead of collapsing them into one /64", async () => {
    // ::ffff:a.b.c.d carries a real IPv4 client; its /64 is all zeros for everyone.
    expect(await spendBudgetFrom("::ffff:192.0.2.7", "mapped")).toBe(429);
    expect(await activateFrom("::ffff:192.0.2.8", "neighbour-mapped")).toBe(200);
  });

  it("keeps IPv4-translated callers apart instead of collapsing them into one /64", async () => {
    // ::ffff:0:a.b.c.d (RFC 2765) carries an IPv4 client in its last two hextets exactly as the
    // mapped form does, and its /64 is all zeros for everyone just the same.
    expect(await spendBudgetFrom("::ffff:0:192.0.2.17", "translated")).toBe(429);
    expect(await activateFrom("::ffff:0:192.0.2.18", "neighbour-translated")).toBe(200);
  });

  it("keeps IPv4-compatible callers apart instead of collapsing them into one /64", async () => {
    // ::a.b.c.d is deprecated but shares the same all-zero /64, and so do :: and ::1.
    expect(await spendBudgetFrom("::192.0.2.27", "compatible")).toBe(429);
    expect(await activateFrom("::192.0.2.28", "neighbour-compatible")).toBe(200);
    expect(await spendBudgetFrom("::1", "loopback")).toBe(429);
    expect(await activateFrom("::", "unspecified")).toBe(200);
  });

  it("treats every encoding of one IPv4 client as the same caller", async () => {
    expect(await spendBudgetFrom("192.0.2.33", "encodings")).toBe(429);
    expect(await activateFrom("::ffff:192.0.2.33", "mapped-same")).toBe(429);
    expect(await activateFrom("::ffff:0:192.0.2.33", "translated-same")).toBe(429);
    expect(await activateFrom("::192.0.2.33", "compatible-same")).toBe(429);
  });

  /**
   * `bucketAddress` falls back to the raw string when it cannot parse an address, and that
   * string becomes a durable `TEXT PRIMARY KEY`. Cloudflare overwrites `CF-Connecting-IP`, so
   * this is not client-reachable in production — but nothing in the code enforces that.
   */
  it("caps the stored bucket key rather than trusting the header's length", async () => {
    await post("activate", { code: "bloat-probe-0000" }, undefined, from(`${"9".repeat(5000)}:1`));

    const stub = env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
    const widest = await runInDurableObject(stub, (_instance, state) =>
      state.storage.sql
        .exec<{ width: number }>("SELECT MAX(LENGTH(client)) AS width FROM activation_attempts")
        .toArray()[0].width);
    expect(widest).toBeLessThanOrEqual(64);
  });

  /**
   * The size of `activation_attempts` is set by the caller, not by us: rotating source prefixes
   * inserts a fresh row per request and never trips the limit. If the window sweep scans, the
   * cost of every attempt grows with a table the attacker is filling — the limiter would make
   * the abuse case worse than having no limiter at all. This pins the sweep to a constant.
   */
  it("sweeps the expired window without scanning the whole table", async () => {
    const sparse = await rowsReadForOneAttempt(200);
    const crowded = await rowsReadForOneAttempt(5000);

    expect(crowded).toBeLessThan(50);
    expect(crowded).toBeLessThan(sparse + 25);
  });

  it("leaves room creation, publishing and streaming alone", async () => {
    const operator = from("198.51.100.30");
    const { deviceToken } = await activateDevice("busy-laptop");
    for (let attempt = 0; attempt <= ACTIVATE_MAX_ATTEMPTS * 2; attempt++) {
      const created = await post(
        "create",
        { roomId: `kanal-11000${attempt % 10}-BusyRoomAaaaaaaa`, verificationKey: VK },
        deviceToken,
        operator,
      );
      expect(created.status).toBe(200);
      const { hostTicket, inviteTicket } = await created.json<{
        hostTicket: string;
        inviteTicket: string;
      }>();
      const published = await post("publish", { payload: signedEnvelope }, hostTicket, operator);
      expect(published.status).toBe(200);
      const reader = await openReader(inviteTicket);
      expect(reader).not.toBeInstanceOf(Response);
      (reader as Reader).socket.close();
    }
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
