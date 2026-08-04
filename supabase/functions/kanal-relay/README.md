# kanal-relay

Server-side gateway between Kanal clients and private Supabase Realtime channels.

Required runtime secret:

- `KANAL_HOST_TOKEN`: at least 32 random bytes, shared only with the operator's desktop
  environment/credential store. It authenticates room creation and signs short-lived room tickets.

Optional runtime secret:

- `KANAL_ALLOWED_ORIGIN`: mobile-page origin; defaults to `https://toniiv.github.io`.

Deploy with JWT verification disabled because the function performs capability authentication on
all three routes itself:

```bash
supabase secrets set KANAL_HOST_TOKEN='<at-least-32-random-bytes>'
supabase functions deploy kanal-relay --no-verify-jwt --use-api
```

Then provision the desktop at runtime (not during compilation or packaging):

```bash
export KANAL_RELAY_URL='https://<project-ref>.supabase.co/functions/v1/kanal-relay'
export KANAL_RELAY_HOST_TOKEN='<the-exact-same-secret>'
```

The endpoint is public by nature and appears in the join QR. It is not a credential: room creation
requires the bootstrap token, publishing requires a host ticket, and streaming requires a reader
ticket. Never pass the server-side Supabase secret or `KANAL_HOST_TOKEN` to GitHub Pages, GitHub
Actions build variables, or release artifacts.
