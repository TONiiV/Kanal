# kanal-relay gateway

Server-side gateway between Kanal hosts and read-only phones: a Cloudflare Worker with two
Durable Objects — one relay object per room fanning captions out over hibernated WebSockets,
and one device registry (SQLite) holding per-desktop credentials.

Why a Worker and not a serverless function: reader phones hold one WebSocket for the whole
meeting. Hibernated Durable Object sockets have no wall-clock limit and are included in the
**Workers Free plan**, whereas Supabase Edge Functions (150 s free / 400 s paid) and Vercel
Functions (no WebSockets) would forcibly disconnect every phone every few minutes.

No backing store is involved: the Worker holds no Supabase/database URL or key, messages are
fanned out in memory and never stored, and the repository, desktop build, and mobile page
contain no server credential of any kind.

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

**A code is valid for 24 hours.** Mint it when the machine is in front of you, not in advance:
after 24 hours it is refused with the same `401 Invalid activation code` an unknown code gets,
and you mint a new one. This is what keeps a code that leaks — shell history, a pasted message,
a note — from still being redeemable weeks later. Nothing has to be cleaned up when a code
expires; unredeemed codes simply stop working.

`?action=activate` is unauthenticated by necessity (a new desktop has nothing to authenticate
with), so it is rate limited to **10 attempts per 10 minutes per client address**; over that it
answers `429 Too many activation attempts`. One machine's activation needs one attempt, so the
budget is only reachable by fumbling a paste several times — wait out the window and retry.
`create`, `publish` and `stream` are not affected.

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
| `GET ?action=stream` | reader ticket in `Sec-WebSocket-Protocol: kanal, ticket.<t>` | receive `gateway.session`, then `{type:"relay", payload}` frames |
| `POST ?action=activate` | activation code | one-time exchange for a device credential; code expires 24 h after minting, 10 attempts / 10 min / address |
| `POST ?action=admin.code` / `admin.revoke`, `GET ?action=admin.devices` | admin token | device lifecycle |

The gateway never sees cleartext credentials at rest (codes and device tokens are stored as
SHA-256 hashes) and never inspects caption content — envelopes are signed by the host's P-256
key and verified on the phone.
