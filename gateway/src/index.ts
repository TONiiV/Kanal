import { DurableObject } from "cloudflare:workers";

/**
 * Kanal relay gateway.
 *
 * The public repository, the desktop build, and the GitHub Pages copy contain no backing-store
 * URL or key: phones and hosts only ever see this Worker's address, and every route requires a
 * capability — a per-device credential to create rooms, a host ticket to publish, a reader
 * ticket to stream. Fan-out happens inside a per-room Durable Object over hibernated
 * WebSockets, which the Workers Free plan allows to stay open for the whole meeting —
 * the property serverless functions (hard wall-clock caps, or no WebSockets at all)
 * cannot offer.
 *
 * Wire protocol (must match `GatewayRelayPublisher.cs` and `web/index.html`):
 *   POST ?action=create   Bearer <device token>   {roomId, verificationKey}
 *                         -> {hostTicket, inviteTicket, expiresAt}
 *   POST ?action=publish  Bearer <host ticket>    {payload: <relay.signed envelope>}
 *   GET  ?action=stream   WebSocket upgrade, subprotocols "kanal, ticket.<reader ticket>"
 *                         -> "kanal", then {type:"gateway.session"|"relay", ...}
 *
 * Device authorization (operator-only, curl or a future settings pane):
 *   POST ?action=admin.code     Bearer <KANAL_ADMIN_TOKEN>  {note?} -> {code}
 *   POST ?action=activate       {code, deviceName?} -> {deviceId, deviceToken}
 *                               codes are single-use and expire 24 h after minting; the route
 *                               is rate limited per client address, per /64 for IPv6
 *                               (429 once the budget is up)
 *   POST ?action=admin.revoke   Bearer <KANAL_ADMIN_TOKEN>  {deviceId}
 *   GET  ?action=admin.devices  Bearer <KANAL_ADMIN_TOKEN>
 */

export interface Env {
  ROOMS: DurableObjectNamespace<RoomRelay>;
  REGISTRY: DurableObjectNamespace<DeviceRegistry>;
  KANAL_TICKET_SECRET: string;
  KANAL_ADMIN_TOKEN: string;
  KANAL_ALLOWED_ORIGIN?: string;
}

const encoder = new TextEncoder();
const decoder = new TextDecoder();
const MAX_ROOM_SECONDS = 12 * 60 * 60;
const MAX_PAYLOAD_BYTES = 256 * 1024;
const DEFAULT_ALLOWED_ORIGIN = "https://toniiv.github.io";
/**
 * A code is handed over for one machine; a leaked one must not still work weeks later.
 * Exported so the suite asserts against these numbers rather than copies of them.
 */
export const ACTIVATION_CODE_TTL_MS = 24 * 60 * 60 * 1000;
export const ACTIVATE_WINDOW_MS = 10 * 60 * 1000;
export const ACTIVATE_MAX_ATTEMPTS = 10;

type Role = "host" | "reader";

interface TicketClaims {
  v: 1;
  role: Role;
  room: string;
  vk: string;
  iat: number;
  exp: number;
}

function base64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

function decodeBase64Url(value: string): Uint8Array {
  const standard = value.replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(standard + "=".repeat((4 - standard.length % 4) % 4));
  return Uint8Array.from(binary, (char) => char.charCodeAt(0));
}

function randomToken(bytes: number): string {
  const buffer = new Uint8Array(bytes);
  crypto.getRandomValues(buffer);
  return base64Url(buffer);
}

async function sha256Hex(value: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", encoder.encode(value));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

/** Hash both sides so comparison time is independent of where the strings differ. */
async function constantTimeEquals(supplied: string, expected: string): Promise<boolean> {
  const [left, right] = await Promise.all([sha256Hex(supplied), sha256Hex(expected)]);
  let difference = 0;
  for (let index = 0; index < left.length; index++) {
    difference |= left.charCodeAt(index) ^ right.charCodeAt(index);
  }
  return difference === 0;
}

function requiredSecret(env: Env, name: "KANAL_TICKET_SECRET" | "KANAL_ADMIN_TOKEN"): string {
  const value = env[name];
  if (!value || encoder.encode(value).byteLength < 32) {
    throw new Error(`Missing server configuration: ${name} (>= 32 bytes)`);
  }
  return value;
}

async function ticketKey(env: Env): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    "raw",
    encoder.encode(requiredSecret(env, "KANAL_TICKET_SECRET")),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign", "verify"],
  );
}

async function issueTicket(env: Env, claims: TicketClaims): Promise<string> {
  const payload = base64Url(encoder.encode(JSON.stringify(claims)));
  const signature = await crypto.subtle.sign("HMAC", await ticketKey(env), encoder.encode(payload));
  return `${payload}.${base64Url(new Uint8Array(signature))}`;
}

async function verifyTicket(env: Env, ticket: string, role: Role): Promise<TicketClaims | null> {
  try {
    const parts = ticket.split(".");
    if (parts.length !== 2) return null;
    const valid = await crypto.subtle.verify(
      "HMAC",
      await ticketKey(env),
      decodeBase64Url(parts[1]).buffer as ArrayBuffer,
      encoder.encode(parts[0]),
    );
    if (!valid) return null;
    const claims = JSON.parse(decoder.decode(decodeBase64Url(parts[0]))) as TicketClaims;
    const now = Math.floor(Date.now() / 1000);
    if (
      claims.v !== 1 || claims.role !== role || claims.exp <= now ||
      typeof claims.room !== "string" || typeof claims.vk !== "string"
    ) return null;
    return claims;
  } catch {
    return null;
  }
}

function bearer(request: Request): string {
  return request.headers.get("Authorization")?.replace(/^Bearer\s+/i, "") ?? "";
}

function response(body: unknown, status = 200, origin?: string | null): Response {
  const headers = new Headers({
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
  });
  if (origin) {
    headers.set("Access-Control-Allow-Origin", origin);
    headers.set("Vary", "Origin");
  }
  return new Response(JSON.stringify(body), { status, headers });
}

/** null: no Origin header (curl, desktop). "": browser origin that is not the mobile page. */
function allowedOrigin(request: Request, env: Env): string | null {
  const origin = request.headers.get("Origin");
  if (!origin) return null;
  return origin === (env.KANAL_ALLOWED_ORIGIN ?? DEFAULT_ALLOWED_ORIGIN) ? origin : "";
}

function registry(env: Env): DurableObjectStub<DeviceRegistry> {
  return env.REGISTRY.get(env.REGISTRY.idFromName("registry"));
}

async function adminAuthorized(request: Request, env: Env): Promise<boolean> {
  return constantTimeEquals(bearer(request), requiredSecret(env, "KANAL_ADMIN_TOKEN"));
}

async function mintActivationCode(request: Request, env: Env): Promise<Response> {
  if (!(await adminAuthorized(request, env))) return response({ error: "Unauthorized" }, 401);
  const body = await request.json().catch(() => ({})) as { note?: unknown };
  const code = randomToken(16);
  await registry(env).registerCode(
    await sha256Hex(code),
    typeof body.note === "string" ? body.note.slice(0, 128) : "",
  );
  return response({ code });
}

/**
 * `activate` is the one route an anonymous caller may reach, so it is the one route on which
 * an anonymous caller can make the registry do work. Cloudflare overwrites `CF-Connecting-IP`
 * at the edge, so it is a usable bucket key; a caller arriving without one (curl against a
 * bare workerd, an internal probe) shares a single bucket rather than escaping the limit.
 *
 * `admin.code` is deliberately not limited: its unauthenticated path returns 401 at the admin
 * token check, before any Durable Object call, so flooding it costs the registry nothing —
 * and limiting it by address would only throttle the operator, who holds the token anyway.
 */
function clientKey(request: Request): string {
  const address = request.headers.get("CF-Connecting-IP");
  // Truncated before parsing: the longest textual IPv6 address is 45 characters, and an
  // unparseable value is used verbatim as a durable primary key. Cloudflare overwrites the
  // header so this is not client-reachable, but the cap is what makes that safe to rely on.
  return address ? bucketAddress(address.trim().slice(0, 64)) : "unknown";
}

/**
 * IPv4 buckets on the whole address. IPv6 buckets on the /64, because the smallest prefix an
 * ordinary subscriber is handed is a /64 — keying on the exact address would give one caller
 * 2^64 budgets and leave the limit inert. The prefixes that carry an IPv4 client in their last
 * two hextets bucket on that IPv4 instead, which also makes every spelling of one IPv4 caller
 * a single bucket.
 */
function bucketAddress(address: string): string {
  if (!address.includes(":")) return address;
  const hextets = expandIpv6(address.split("%")[0].toLowerCase());
  if (!hextets) return address;
  return embeddedIpv4(hextets)
    ?? `${hextets.slice(0, 4).map((hextet) => hextet.toString(16)).join(":")}::/64`;
}

/**
 * The IPv4 address carried by `::ffff:a.b.c.d` (mapped), `::a.b.c.d` (compatible, deprecated)
 * and `::ffff:0:a.b.c.d` (translated, RFC 2765); null for anything else. All three sit in the
 * same all-zero /64, so bucketing them by prefix would put every such caller — who are
 * unrelated IPv4 clients — into one shared budget.
 *
 * Known and accepted: two further prefixes also put unrelated clients in one bucket, for
 * different reasons. Under NAT64 `64:ff9b::/96` the prefix is fixed and identical for every
 * translated client, so they share the `64:ff9b:0:0` /64 whatever their own IPv4 is. Under
 * Teredo `2001:0::/32` hextets 2-3 carry the *server's* IPv4, so one /64 covers every client of
 * that server. Neither is realistically emittable as a source address at the Cloudflare edge —
 * 64:ff9b is a destination-synthesis prefix and Teredo is effectively dead — so both are left
 * to the /64 rule rather than special-cased.
 */
function embeddedIpv4(hextets: number[]): string | null {
  if (!hextets.slice(0, 4).every((hextet) => hextet === 0)) return null;
  const carrier = (hextets[4] === 0 && (hextets[5] === 0 || hextets[5] === 0xffff)) ||
    (hextets[4] === 0xffff && hextets[5] === 0);
  if (!carrier) return null;
  return [hextets[6] >> 8, hextets[6] & 0xff, hextets[7] >> 8, hextets[7] & 0xff].join(".");
}

/** The eight hextets of an IPv6 address, accepting `::` and a trailing dotted quad; null if malformed. */
function expandIpv6(address: string): number[] | null {
  let text = address;
  const dotted = /(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})$/.exec(text);
  if (dotted) {
    const octets = dotted.slice(1).map(Number);
    if (octets.some((octet) => octet > 255)) return null;
    const pair = [(octets[0] << 8) | octets[1], (octets[2] << 8) | octets[3]];
    text = text.slice(0, dotted.index) + pair.map((v) => v.toString(16)).join(":");
  }
  const halves = text.split("::");
  if (halves.length > 2) return null;
  const head = halves[0] ? halves[0].split(":") : [];
  const tail = halves.length === 2 && halves[1] ? halves[1].split(":") : [];
  const missing = 8 - head.length - tail.length;
  if (halves.length === 1 ? missing !== 0 : missing < 0) return null;
  const groups = halves.length === 2
    ? [...head, ...Array<string>(missing).fill("0"), ...tail]
    : head;
  if (!groups.every((group) => /^[0-9a-f]{1,4}$/.test(group))) return null;
  return groups.map((group) => parseInt(group, 16));
}

async function activateDevice(request: Request, env: Env): Promise<Response> {
  const body = await request.json().catch(() => null) as
    | { code?: unknown; deviceName?: unknown }
    | null;
  if (!body || typeof body.code !== "string" || body.code.length < 8) {
    return response({ error: "Invalid activation code" }, 401);
  }
  if (!(await registry(env).allowActivationAttempt(clientKey(request)))) {
    return response({ error: "Too many activation attempts" }, 429);
  }
  const deviceId = randomToken(8);
  const deviceToken = randomToken(32);
  const redeemed = await registry(env).redeemCode(
    await sha256Hex(body.code),
    deviceId,
    typeof body.deviceName === "string" ? body.deviceName.slice(0, 128) : "",
    await sha256Hex(deviceToken),
  );
  if (!redeemed) return response({ error: "Invalid activation code" }, 401);
  return response({ deviceId, deviceToken });
}

async function revokeDevice(request: Request, env: Env): Promise<Response> {
  if (!(await adminAuthorized(request, env))) return response({ error: "Unauthorized" }, 401);
  const body = await request.json().catch(() => null) as { deviceId?: unknown } | null;
  if (!body || typeof body.deviceId !== "string") {
    return response({ error: "Invalid device id" }, 400);
  }
  const revoked = await registry(env).revoke(body.deviceId);
  return revoked ? response({ ok: true }) : response({ error: "Unknown device" }, 404);
}

async function listDevices(request: Request, env: Env): Promise<Response> {
  if (!(await adminAuthorized(request, env))) return response({ error: "Unauthorized" }, 401);
  return response({ devices: await registry(env).listDevices() });
}

async function createRoom(request: Request, env: Env): Promise<Response> {
  const deviceId = await registry(env).deviceIdFor(await sha256Hex(bearer(request)));
  if (!deviceId) return response({ error: "Unauthorized" }, 401);

  const body = await request.json().catch(() => null) as
    | { roomId?: unknown; verificationKey?: unknown }
    | null;
  if (
    !body ||
    typeof body.roomId !== "string" ||
    !/^kanal-[A-Za-z0-9_-]{10,96}$/.test(body.roomId) ||
    typeof body.verificationKey !== "string" ||
    !/^[A-Za-z0-9_-]{40,512}$/.test(body.verificationKey)
  ) {
    return response({ error: "Invalid room metadata" }, 400);
  }

  const now = Math.floor(Date.now() / 1000);
  const common = {
    v: 1 as const,
    room: body.roomId,
    vk: body.verificationKey,
    iat: now,
    exp: now + MAX_ROOM_SECONDS,
  };
  const [hostTicket, inviteTicket] = await Promise.all([
    issueTicket(env, { ...common, role: "host" }),
    issueTicket(env, { ...common, role: "reader" }),
  ]);
  return response({ hostTicket, inviteTicket, expiresAt: common.exp });
}

async function publish(request: Request, env: Env): Promise<Response> {
  const claims = await verifyTicket(env, bearer(request), "host");
  if (!claims) return response({ error: "Unauthorized" }, 401);

  const raw = await request.text();
  if (encoder.encode(raw).byteLength > MAX_PAYLOAD_BYTES) {
    return response({ error: "Payload too large" }, 413);
  }
  let payload: unknown;
  try {
    payload = (JSON.parse(raw) as { payload?: unknown }).payload;
  } catch {
    return response({ error: "Invalid JSON" }, 400);
  }
  if (
    !payload || typeof payload !== "object" ||
    (payload as { type?: unknown }).type !== "relay.signed"
  ) {
    return response({ error: "Only signed relay envelopes are accepted" }, 400);
  }

  const room = env.ROOMS.get(env.ROOMS.idFromName(claims.room));
  await room.publish(payload);
  return response({ ok: true });
}

async function stream(request: Request, env: Env): Promise<Response> {
  if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
    return response({ error: "WebSocket upgrade required" }, 426);
  }
  const protocols = (request.headers.get("Sec-WebSocket-Protocol") ?? "")
    .split(",")
    .map((value) => value.trim());
  const ticketProtocol = protocols.find((value) => value.startsWith("ticket."));
  const claims = ticketProtocol
    ? await verifyTicket(env, ticketProtocol.slice("ticket.".length), "reader")
    : null;
  if (!claims) return response({ error: "Unauthorized" }, 401);

  // The room object needs the claims to greet the reader; the original headers stay behind.
  const forward = new URL("https://room.internal/stream");
  forward.searchParams.set("room", claims.room);
  forward.searchParams.set("vk", claims.vk);
  forward.searchParams.set("exp", String(claims.exp));
  const room = env.ROOMS.get(env.ROOMS.idFromName(claims.room));
  return room.fetch(new Request(forward, { headers: { Upgrade: "websocket" } }));
}

export default {
  async fetch(request: Request, env: Env): Promise<Response> {
    try {
      const origin = allowedOrigin(request, env);
      if (origin === "") return response({ error: "Origin denied" }, 403);
      if (request.method === "OPTIONS") {
        return new Response(null, {
          status: 204,
          headers: {
            "Access-Control-Allow-Origin": origin ?? env.KANAL_ALLOWED_ORIGIN ?? DEFAULT_ALLOWED_ORIGIN,
            "Access-Control-Allow-Headers": "authorization, content-type",
            "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
            "Access-Control-Max-Age": "86400",
          },
        });
      }

      const action = new URL(request.url).searchParams.get("action");
      if (action === "create" && request.method === "POST") return await createRoom(request, env);
      if (action === "publish" && request.method === "POST") return await publish(request, env);
      if (action === "stream") return await stream(request, env);
      if (action === "activate" && request.method === "POST") {
        return await activateDevice(request, env);
      }
      if (action === "admin.code" && request.method === "POST") {
        return await mintActivationCode(request, env);
      }
      if (action === "admin.revoke" && request.method === "POST") {
        return await revokeDevice(request, env);
      }
      if (action === "admin.devices" && request.method === "GET") {
        return await listDevices(request, env);
      }
      return response({ error: "Not found" }, 404, origin);
    } catch (error) {
      console.error("kanal-relay failure", error);
      return response({ error: "Relay unavailable" }, 500);
    }
  },
} satisfies ExportedHandler<Env>;

/**
 * One instance per room, addressed by room id. Readers park here as hibernated WebSockets:
 * the object is evicted from memory between messages, the sockets stay open, and ping/pong
 * is answered by the runtime without waking anything — a 90-minute meeting costs milliseconds
 * of compute, and nothing ever forces the phones to reconnect.
 */
export class RoomRelay extends DurableObject<Env> {
  async fetch(request: Request): Promise<Response> {
    if (request.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
      return response({ error: "WebSocket upgrade required" }, 426);
    }
    const url = new URL(request.url);
    const room = url.searchParams.get("room") ?? "";
    const verificationKey = url.searchParams.get("vk") ?? "";
    const expiresAt = Number(url.searchParams.get("exp") ?? "0");

    const pair = new WebSocketPair();
    const client = pair[0];
    const server = pair[1];
    this.ctx.acceptWebSocket(server);
    server.serializeAttachment({ expiresAt });
    this.ctx.setWebSocketAutoResponse(new WebSocketRequestResponsePair("ping", "pong"));
    await this.scheduleExpiry(expiresAt);

    // Same contract as before: the reader treats the session message as proof it is
    // subscribed to the room it was invited to, before trusting any relayed envelope.
    server.send(JSON.stringify({ type: "gateway.session", room, verificationKey, expiresAt }));

    return new Response(null, {
      status: 101,
      webSocket: client,
      headers: { "Sec-WebSocket-Protocol": "kanal" },
    });
  }

  /** Fan a signed envelope out to every live reader. Called over RPC from the Worker. */
  async publish(payload: unknown): Promise<void> {
    const message = JSON.stringify({ type: "relay", payload });
    const now = Math.floor(Date.now() / 1000);
    for (const socket of this.ctx.getWebSockets()) {
      if (this.expired(socket, now)) continue;
      try {
        socket.send(message);
      } catch {
        // A socket mid-close is the phone's reconnect logic's problem, not the room's.
      }
    }
  }

  async webSocketMessage(socket: WebSocket, _message: ArrayBuffer | string): Promise<void> {
    // "ping" never reaches here (auto-response). Anything else is a protocol violation:
    // phones are projections and must not be able to inject state.
    socket.close(1008, "Reader connections are receive-only");
  }

  async webSocketError(): Promise<void> {}

  async webSocketClose(): Promise<void> {}

  /** Rooms end. Tickets expire after 12 h and the sockets they opened close with them. */
  async alarm(): Promise<void> {
    const now = Math.floor(Date.now() / 1000);
    let next = Number.POSITIVE_INFINITY;
    for (const socket of this.ctx.getWebSockets()) {
      if (!this.expired(socket, now)) {
        const { expiresAt } = socket.deserializeAttachment() as { expiresAt: number };
        next = Math.min(next, expiresAt);
      }
    }
    if (Number.isFinite(next)) await this.ctx.storage.setAlarm(next * 1000);
  }

  private expired(socket: WebSocket, now: number): boolean {
    const { expiresAt } = (socket.deserializeAttachment() ?? {}) as { expiresAt?: number };
    if (expiresAt && expiresAt > now) return false;
    try {
      socket.close(1000, "Room expired");
    } catch {
      // already closing
    }
    return true;
  }

  private async scheduleExpiry(expiresAt: number): Promise<void> {
    const current = await this.ctx.storage.getAlarm();
    const at = expiresAt * 1000;
    if (current === null || at < current) await this.ctx.storage.setAlarm(at);
  }
}

/**
 * The device registry replaces the old single shared host token: each trusted desktop trades
 * a one-time activation code for its own credential, and a lost laptop is revoked alone —
 * without rotating anyone else. Only SHA-256 hashes of codes and tokens are stored.
 */
export class DeviceRegistry extends DurableObject<Env> {
  private readonly sql: SqlStorage;

  constructor(ctx: DurableObjectState, env: Env) {
    super(ctx, env);
    this.sql = ctx.storage.sql;
    this.sql.exec(`
      CREATE TABLE IF NOT EXISTS codes (
        code_hash TEXT PRIMARY KEY,
        note TEXT NOT NULL DEFAULT '',
        created_at INTEGER NOT NULL,
        used_at INTEGER
      );
      CREATE TABLE IF NOT EXISTS devices (
        device_id TEXT PRIMARY KEY,
        name TEXT NOT NULL DEFAULT '',
        token_hash TEXT NOT NULL UNIQUE,
        created_at INTEGER NOT NULL,
        revoked_at INTEGER
      );
      CREATE TABLE IF NOT EXISTS activation_attempts (
        client TEXT PRIMARY KEY,
        window_start INTEGER NOT NULL,
        attempts INTEGER NOT NULL
      );
      CREATE INDEX IF NOT EXISTS activation_attempts_window
        ON activation_attempts (window_start);
    `);
  }

  /**
   * Fixed window per calling address. Durable rather than in-memory because a caller who
   * paces requests slowly enough for this object to be evicted would otherwise reset the
   * counter for free. Rows expire with their window, so the table stays the size of the set
   * of addresses currently trying.
   *
   * The sweep runs on `activation_attempts_window`, not as a scan. A caller rotating source
   * prefixes never trips the limit and inserts a row per request, so the table's size is his
   * to choose; an unindexed sweep would read all of it on every attempt and make the limiter
   * worse than no limiter. Indexed, the sweep reads only the rows it actually retires — each
   * row read and deleted exactly once in its life — so the cost is amortised-constant and
   * total work is linear in rows created rather than quadratic in table size.
   */
  allowActivationAttempt(client: string): boolean {
    const now = Date.now();
    this.sql.exec(
      "DELETE FROM activation_attempts WHERE window_start <= ?",
      now - ACTIVATE_WINDOW_MS,
    );
    const open = this.sql
      .exec<{ attempts: number }>(
        "SELECT attempts FROM activation_attempts WHERE client = ?",
        client,
      )
      .toArray();
    if (open.length === 0) {
      this.sql.exec(
        "INSERT INTO activation_attempts (client, window_start, attempts) VALUES (?, ?, 1)",
        client,
        now,
      );
      return true;
    }
    if (open[0].attempts >= ACTIVATE_MAX_ATTEMPTS) return false;
    this.sql.exec(
      "UPDATE activation_attempts SET attempts = attempts + 1 WHERE client = ?",
      client,
    );
    return true;
  }

  registerCode(codeHash: string, note: string): void {
    this.sql.exec(
      "INSERT INTO codes (code_hash, note, created_at) VALUES (?, ?, ?)",
      codeHash,
      note,
      Date.now(),
    );
  }

  /**
   * Atomically burns the code and enrols the device; false if the code is unknown, spent, or
   * older than `ACTIVATION_CODE_TTL_MS`. The age comes from the `created_at` the code was
   * already stored with, so no schema change reaches the deployed registry.
   */
  redeemCode(codeHash: string, deviceId: string, name: string, tokenHash: string): boolean {
    const now = Date.now();
    const burned = this.sql.exec(
      "UPDATE codes SET used_at = ? WHERE code_hash = ? AND used_at IS NULL AND created_at > ?",
      now,
      codeHash,
      now - ACTIVATION_CODE_TTL_MS,
    );
    if (burned.rowsWritten === 0) return false;
    this.sql.exec(
      "INSERT INTO devices (device_id, name, token_hash, created_at) VALUES (?, ?, ?, ?)",
      deviceId,
      name,
      tokenHash,
      Date.now(),
    );
    return true;
  }

  deviceIdFor(tokenHash: string): string | null {
    const rows = this.sql
      .exec<{ device_id: string }>(
        "SELECT device_id FROM devices WHERE token_hash = ? AND revoked_at IS NULL",
        tokenHash,
      )
      .toArray();
    return rows.length === 1 ? rows[0].device_id : null;
  }

  revoke(deviceId: string): boolean {
    const result = this.sql.exec(
      "UPDATE devices SET revoked_at = ? WHERE device_id = ? AND revoked_at IS NULL",
      Date.now(),
      deviceId,
    );
    return result.rowsWritten > 0;
  }

  listDevices(): { deviceId: string; name: string; createdAt: number; revoked: boolean }[] {
    return this.sql
      .exec<{ device_id: string; name: string; created_at: number; revoked_at: number | null }>(
        "SELECT device_id, name, created_at, revoked_at FROM devices ORDER BY created_at",
      )
      .toArray()
      .map((row) => ({
        deviceId: row.device_id,
        name: row.name,
        createdAt: row.created_at,
        revoked: row.revoked_at !== null,
      }));
  }
}
