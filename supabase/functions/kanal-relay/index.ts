import "jsr:@supabase/functions-js/edge-runtime.d.ts";
import { createClient, type SupabaseClient } from "@supabase/supabase-js";

declare const EdgeRuntime: { waitUntil(promise: Promise<unknown>): void };

const encoder = new TextEncoder();
const decoder = new TextDecoder();
const MAX_ROOM_SECONDS = 12 * 60 * 60;
const MAX_PAYLOAD_BYTES = 256 * 1024;
const DEFAULT_ALLOWED_ORIGIN = "https://toniiv.github.io";

type Role = "host" | "reader";

interface TicketClaims {
  v: 1;
  role: Role;
  room: string;
  vk: string;
  iat: number;
  exp: number;
}

interface CreateRoomBody {
  roomId?: unknown;
  verificationKey?: unknown;
}

function requiredEnv(name: string): string {
  const value = Deno.env.get(name);
  if (!value) throw new Error(`Missing server configuration: ${name}`);
  return value;
}

function hostToken(): string {
  const value = requiredEnv("KANAL_HOST_TOKEN");
  if (encoder.encode(value).byteLength < 32) {
    throw new Error("KANAL_HOST_TOKEN must contain at least 32 bytes");
  }
  return value;
}

function secretApiKey(): string {
  const keys = Deno.env.get("SUPABASE_SECRET_KEYS");
  if (keys) {
    const parsed = JSON.parse(keys) as Record<string, string>;
    if (parsed.default) return parsed.default;
  }
  return requiredEnv("SUPABASE_SERVICE_ROLE_KEY");
}

function adminClient(): { client: SupabaseClient; key: string } {
  const key = secretApiKey();
  return {
    key,
    client: createClient(requiredEnv("SUPABASE_URL"), key, {
      auth: { persistSession: false, autoRefreshToken: false },
    }),
  };
}

function base64Url(bytes: Uint8Array): string {
  let binary = "";
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, "-").replace(/\//g, "_").replace(
    /=+$/,
    "",
  );
}

function decodeBase64Url(value: string): Uint8Array {
  const standard = value.replace(/-/g, "+").replace(/_/g, "/");
  const binary = atob(standard + "=".repeat((4 - standard.length % 4) % 4));
  return Uint8Array.from(binary, (char) => char.charCodeAt(0));
}

async function ticketKey(): Promise<CryptoKey> {
  return crypto.subtle.importKey(
    "raw",
    encoder.encode(hostToken()),
    { name: "HMAC", hash: "SHA-256" },
    false,
    ["sign", "verify"],
  );
}

async function issueTicket(claims: TicketClaims): Promise<string> {
  const payload = base64Url(encoder.encode(JSON.stringify(claims)));
  const signature = await crypto.subtle.sign(
    "HMAC",
    await ticketKey(),
    encoder.encode(payload),
  );
  return `${payload}.${base64Url(new Uint8Array(signature))}`;
}

async function verifyTicket(
  ticket: string,
  role: Role,
): Promise<TicketClaims | null> {
  try {
    const parts = ticket.split(".");
    if (parts.length !== 2) return null;
    const valid = await crypto.subtle.verify(
      "HMAC",
      await ticketKey(),
      decodeBase64Url(parts[1]).buffer as ArrayBuffer,
      encoder.encode(parts[0]),
    );
    if (!valid) return null;
    const claims = JSON.parse(
      decoder.decode(decodeBase64Url(parts[0])),
    ) as TicketClaims;
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

async function bootstrapAuthorized(req: Request): Promise<boolean> {
  const supplied =
    req.headers.get("Authorization")?.replace(/^Bearer\s+/i, "") ?? "";
  const expected = hostToken();
  const [left, right] = await Promise.all([
    crypto.subtle.digest("SHA-256", encoder.encode(supplied)),
    crypto.subtle.digest("SHA-256", encoder.encode(expected)),
  ]);
  const leftBytes = new Uint8Array(left);
  const rightBytes = new Uint8Array(right);
  let difference = leftBytes.length ^ rightBytes.length;
  for (let index = 0; index < leftBytes.length; index++) {
    difference |= leftBytes[index] ^ (rightBytes[index] ?? 0);
  }
  return difference === 0;
}

function bearer(req: Request): string {
  return req.headers.get("Authorization")?.replace(/^Bearer\s+/i, "") ?? "";
}

function response(
  body: unknown,
  status = 200,
  origin?: string | null,
): Response {
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

function allowedOrigin(req: Request): string | null {
  const origin = req.headers.get("Origin");
  if (!origin) return null;
  return origin ===
      (Deno.env.get("KANAL_ALLOWED_ORIGIN") ?? DEFAULT_ALLOWED_ORIGIN)
    ? origin
    : "";
}

async function createRoom(req: Request): Promise<Response> {
  if (!(await bootstrapAuthorized(req))) {
    return response({ error: "Unauthorized" }, 401);
  }
  const body = await req.json() as CreateRoomBody;
  if (
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
    issueTicket({ ...common, role: "host" }),
    issueTicket({ ...common, role: "reader" }),
  ]);
  return response({ hostTicket, inviteTicket, expiresAt: common.exp });
}

async function publish(req: Request): Promise<Response> {
  const claims = await verifyTicket(bearer(req), "host");
  if (!claims) return response({ error: "Unauthorized" }, 401);
  const raw = await req.text();
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

  const { client } = adminClient();
  const channel = client.channel(`kanal:${claims.room}`, {
    config: { private: true },
  });
  const result = await channel.send({
    type: "broadcast",
    event: "kanal",
    payload,
  });
  if (result !== "ok") {
    return response({ error: "Realtime publish failed" }, 502);
  }
  return response({ ok: true });
}

async function stream(req: Request): Promise<Response> {
  if (req.headers.get("Upgrade")?.toLowerCase() !== "websocket") {
    return response({ error: "WebSocket upgrade required" }, 426);
  }

  const protocols = (req.headers.get("Sec-WebSocket-Protocol") ?? "")
    .split(",")
    .map((value) => value.trim());
  const ticketProtocol = protocols.find((value) => value.startsWith("ticket."));
  const claims = ticketProtocol
    ? await verifyTicket(ticketProtocol.slice("ticket.".length), "reader")
    : null;
  if (!claims) return response({ error: "Unauthorized" }, 401);

  const { socket, response: upgradeResponse } = Deno.upgradeWebSocket(req, {
    protocol: "kanal",
    idleTimeout: 0,
  });
  const { client, key } = adminClient();
  client.realtime.setAuth(key);
  const channel = client.channel(`kanal:${claims.room}`, {
    config: { private: true, broadcast: { self: false } },
  });

  let resolveClosed!: () => void;
  const closed = new Promise<void>((resolve) => {
    resolveClosed = resolve;
  });
  // Edge Functions may otherwise retire after the upgrade response is returned.
  EdgeRuntime.waitUntil(closed);

  channel
    .on("broadcast", { event: "kanal" }, (message) => {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(
          JSON.stringify({ type: "relay", payload: message.payload }),
        );
      }
    })
    .subscribe((status) => {
      if (status === "SUBSCRIBED" && socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify({
          type: "gateway.session",
          room: claims.room,
          verificationKey: claims.vk,
          expiresAt: claims.exp,
        }));
      } else if (
        ["CHANNEL_ERROR", "TIMED_OUT", "CLOSED"].includes(status) &&
        socket.readyState === WebSocket.OPEN
      ) {
        socket.close(1012, "Relay reconnect required");
      }
    });

  socket.onmessage = (event) => {
    if (event.data === "ping") socket.send("pong");
    else socket.close(1008, "Reader connections are receive-only");
  };
  socket.onclose = () => {
    void client.removeChannel(channel);
    resolveClosed();
  };
  socket.onerror = () => resolveClosed();
  return upgradeResponse;
}

Deno.serve(async (req: Request) => {
  try {
    const origin = allowedOrigin(req);
    if (origin === "") return response({ error: "Origin denied" }, 403);
    if (req.method === "OPTIONS") {
      const headers = new Headers({
        "Access-Control-Allow-Origin": origin ?? DEFAULT_ALLOWED_ORIGIN,
        "Access-Control-Allow-Headers": "authorization, content-type",
        "Access-Control-Allow-Methods": "POST, OPTIONS",
        "Access-Control-Max-Age": "86400",
      });
      return new Response(null, { status: 204, headers });
    }

    const action = new URL(req.url).searchParams.get("action");
    if (action === "create" && req.method === "POST") {
      return await createRoom(req);
    }
    if (action === "publish" && req.method === "POST") {
      return await publish(req);
    }
    if (action === "stream") return await stream(req);
    return response({ error: "Not found" }, 404, origin);
  } catch (error) {
    console.error("kanal-relay failure", error);
    return response({ error: "Relay unavailable" }, 500);
  }
});
