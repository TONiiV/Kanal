import { defineWorkersConfig } from "@cloudflare/vitest-pool-workers/config";

export default defineWorkersConfig({
  test: {
    poolOptions: {
      workers: {
        wrangler: { configPath: "./wrangler.toml" },
        miniflare: {
          bindings: {
            // Test doubles for `wrangler secret put` values. 32+ bytes each, like production.
            KANAL_TICKET_SECRET: "test-ticket-secret-0123456789-0123456789",
            KANAL_ADMIN_TOKEN: "test-admin-token-0123456789-0123456789",
          },
        },
      },
    },
  },
});
