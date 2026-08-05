# kanal-relay gateway

Server-side gateway between Kanal hosts and read-only phones: a Cloudflare Worker with two
Durable Objects — one relay object per room fanning captions out over hibernated WebSockets,
and one device registry (SQLite) holding per-desktop credentials.

Why a Worker and not a serverless function: reader phones hold one WebSocket for the whole
meeting. Hibernated Durable Object sockets have no wall-clock limit and are included in the
**Workers Free plan**, whereas Supabase Edge Functions (150 s free / 400 s paid) and Vercel
Functions (no WebSockets) would forcibly disconnect every phone every few minutes.

No backing store is involved: the Worker holds no Supabase/database URL or key, messages are
fanned out in memory and never written to disk — a room holds only a bounded in-memory replay
buffer, to greet a reconnecting phone — and the repository, desktop build, and mobile page
contain no server credential of any kind.

## Replay to a reconnecting reader

A room keeps the latest `room.snapshot` envelope **and every frame published after it**, and
sends that sequence to a newly accepted reader right after `gateway.session`. A phone that
locks, roams, or joins late sees exactly what a phone connected the whole time saw, instead of
waiting up to 15 s for the host's next snapshot heartbeat.

The tail is not optional. The mobile page replaces its whole transcript when it applies a
snapshot, and caches every incremental frame as it arrives, so a phone reconnecting between
heartbeats already holds newer state than the snapshot — a snapshot replayed alone would delete
up to 15 s of transcript on screen.

`room.closed` and `room.moved` are appended and make the room terminal: no ordinary frame is
buffered afterwards. This is the case the replay matters most for — the host publishes a final
snapshot, then the announcement, then stops, so a phone that was locked when the meeting ended has
nothing left running to correct it. Replaying snapshot → tail → announcement lands it in "ended",
or follows the move to the room the meeting continues in.

A *later* announcement supersedes an earlier one rather than stacking on it. A closed room can
legitimately receive one more: the host's publisher outlives its session, so a restart announces
`room.moved` on the room it already closed. Superseding means a phone locked across a stop-then-
restart follows the meeting instead of sitting on "ended" for good, and the buffer still holds at
most one terminal frame per dead room.

The buffer is bounded at 256 frames and 1 MiB (4 × the per-frame `MAX_PAYLOAD_BYTES`). On
overflow it is **dropped whole, snapshot and tail together**, rather than truncated: a snapshot
without its tail is the rollback above, whereas an empty buffer only degrades to no replay at
all, and the next `room.snapshot` starts a fresh one. The one exception is a terminal
announcement, which survives an overflow and stands alone if it has to — unlike a snapshot, it
does not rewrite the transcript from its own contents, so on its own it can only add the true
fact that the room ended or moved.

Headroom is thinner than the caps suggest. Every relay message is one publish, partials included
and nothing coalesced, so 15 s of continuous speech is roughly 35–80 frames at a streaming ASR's
usual 2–5 partials/s plus one `translation.upsert` per final — about 3–7× under the 256-frame cap,
and it is the frame cap rather than the byte cap that binds (256 partial envelopes at 0.4–1 KB is
a quarter of 1 MiB). Raising the frame cap costs almost nothing in memory and is worth doing —
but only once the mobile page serialises `onmessage`, since a larger cap means a larger replay
burst arriving at a client that does not yet apply frames in arrival order.

The buffer lives in the object's memory, not in Durable Object storage: an evicted object refills
it within one 15 s heartbeat, whereas storage would put a write on the fan-out path of every
frame for the whole meeting.

## Deploy (once)

```bash
cd gateway
npm install
npx wrangler login
npx wrangler deploy
```

Then set the two server secrets (32+ random bytes each — `openssl rand -base64 32`):

```bash
npx wrangler secret put KANAL_TICKET_SECRET   # HMAC key for short-lived room tickets
npx wrangler secret put KANAL_ADMIN_TOKEN     # authorises device activation-code minting
```

Optional: if the mobile page is not served from `https://toniiv.github.io`, override the
allowed browser origin with `npx wrangler deploy --var KANAL_ALLOWED_ORIGIN:https://your.host`.

> **Mainland-China reachability.** The default `<name>.workers.dev` hostname is blocked in
> mainland China, and a participant whose phone roams through a Chinese carrier is tunnelled
> through the Chinese network even abroad. If such a participant must receive captions, attach
> the Worker to a custom domain (Cloudflare dashboard → Workers → Settings → Domains & Routes);
> custom domains are available on the Free plan but require the zone to be on Cloudflare DNS.

## Authorise a desktop (once per machine)

Every trusted desktop gets its **own** credential — there is no shared host token, and a lost
laptop is revoked without touching the others.

```bash
# 1. Operator mints a one-time activation code (KANAL_ADMIN_TOKEN stays server-side/with you):
curl -s -X POST "https://<worker-host>/?action=admin.code" \
  -H "Authorization: Bearer $KANAL_ADMIN_TOKEN" \
  -H "Content-Type: application/json" -d '{"note":"yandong-thinkpad"}'
# -> {"code":"..."}

# 2. The desktop trades the code for its device credential (the code burns on first use):
curl -s -X POST "https://<worker-host>/?action=activate" \
  -H "Content-Type: application/json" \
  -d '{"code":"<the code>","deviceName":"yandong-thinkpad"}'
# -> {"deviceId":"...","deviceToken":"..."}
```

Provision the desktop at runtime (not during compilation or packaging):

```bash
export KANAL_RELAY_URL='https://<worker-host>/'
export KANAL_RELAY_HOST_TOKEN='<the deviceToken>'
```

Housekeeping:

```bash
# list devices                    -> {"devices":[{"deviceId","name","createdAt","revoked"}]}
curl -s "https://<worker-host>/?action=admin.devices" -H "Authorization: Bearer $KANAL_ADMIN_TOKEN"
# revoke one device
curl -s -X POST "https://<worker-host>/?action=admin.revoke" \
  -H "Authorization: Bearer $KANAL_ADMIN_TOKEN" \
  -H "Content-Type: application/json" -d '{"deviceId":"<id>"}'
```

## Who configures what

- The gateway operator deploys the Worker, keeps `KANAL_ADMIN_TOKEN`, and hands out one
  activation code per trusted desktop.
- A desktop needs `KANAL_RELAY_URL` and its own `KANAL_RELAY_HOST_TOKEN`. Nothing else.
- People who scan a QR configure nothing; their receive-only, room-scoped, 12-hour ticket is
  inside the QR. The gateway address is public by design and is not a credential.
- Someone who clones the repository can run transcription without any relay configuration but
  gets no QR; they deploy their own gateway for mobile delivery.

## Wire protocol

Frozen against `src/Kanal.Core/Relay/GatewayRelayPublisher.cs` and `web/index.html`; the vitest
suite (`npm test`, real workerd via `@cloudflare/vitest-pool-workers`) is the contract:

| Route | Auth | Purpose |
|---|---|---|
| `POST ?action=create` | device token | `{roomId, verificationKey}` → host + invite tickets |
| `POST ?action=publish` | host ticket | forward one `relay.signed` envelope to the room |
| `GET ?action=stream` | reader ticket in `Sec-WebSocket-Protocol: kanal, ticket.<t>` | receive `gateway.session`, then `{type:"relay", payload}` frames — the room's replay buffer first, if it has one |
| `POST ?action=activate` | activation code | one-time exchange for a device credential |
| `POST ?action=admin.code` / `admin.revoke`, `GET ?action=admin.devices` | admin token | device lifecycle |

The gateway never sees cleartext credentials at rest (codes and device tokens are stored as
SHA-256 hashes) and never inspects caption content — envelopes are signed by the host's P-256
key and verified on the phone.
