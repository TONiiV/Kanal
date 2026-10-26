import { env, SELF } from "cloudflare:test";
import { describe, expect, it } from "vitest";
import worker, { type Env } from "../src/index";

const GW = "https://gateway.test/";
const ADMIN = "test-admin-token-0123456789-0123456789";
const ROOM = "kanal-093015-AbCdEfGhIjKlMnOp";
const VK = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE_fake_spki_key_material_0123456789";

type UpdateVars = Partial<
  Record<
    | "KANAL_LATEST_VERSION"
    | "KANAL_MIN_SUPPORTED_VERSION"
    | "KANAL_RELEASE_NOTES"
    | "KANAL_DOWNLOAD_URL",
    string
  >
>;

const MANIFEST_VARS: UpdateVars = {
  KANAL_LATEST_VERSION: "1.2.0",
  KANAL_MIN_SUPPORTED_VERSION: "1.0.0",
  KANAL_RELEASE_NOTES: "Fixes the pause button.",
  KANAL_DOWNLOAD_URL: "https://example.test/releases/latest",
};

function withVars(vars: UpdateVars, overrides: Record<string, unknown> = {}): Env {
  const cleared = {
    KANAL_LATEST_VERSION: undefined,
    KANAL_MIN_SUPPORTED_VERSION: undefined,
    KANAL_RELEASE_NOTES: undefined,
    KANAL_DOWNLOAD_URL: undefined,
  };
  return { ...env, ...cleared, ...vars, ...overrides } as unknown as Env;
}

function getUpdate(vars: UpdateVars, query = "") {
  return worker.fetch(new Request(`${GW}?action=update${query}`), withVars(vars));
}

function createWith(
  vars: UpdateVars,
  version: string | undefined,
  token = "unknown-device-token",
  overrides: Record<string, unknown> = {},
) {
  return worker.fetch(
    new Request(`${GW}?action=create`, {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
        ...(version === undefined ? {} : { "X-Kanal-Version": version }),
      },
      body: JSON.stringify({ roomId: ROOM, verificationKey: VK }),
    }),
    withVars(vars, overrides),
  );
}

async function activateDevice(): Promise<string> {
  const codeResponse = await SELF.fetch(`${GW}?action=admin.code`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${ADMIN}` },
    body: JSON.stringify({ note: "update-spec" }),
  });
  const { code } = await codeResponse.json<{ code: string }>();
  const activated = await SELF.fetch(`${GW}?action=activate`, {
    method: "POST",
    headers: { "Content-Type": "application/json", "CF-Connecting-IP": "198.51.100.77" },
    body: JSON.stringify({ code, deviceName: "update-spec" }),
  });
  return (await activated.json<{ deviceToken: string }>()).deviceToken;
}

function minimumAt(minimumSupported: string): UpdateVars {
  return { ...MANIFEST_VARS, KANAL_MIN_SUPPORTED_VERSION: minimumSupported };
}

describe("update manifest", () => {
  it("returns the four manifest fields from the environment", async () => {
    const response = await getUpdate(MANIFEST_VARS);
    expect(response.status).toBe(200);
    expect(await response.json()).toEqual({
      latest: "1.2.0",
      minimumSupported: "1.0.0",
      notes: "Fixes the pause button.",
      downloadUrl: "https://example.test/releases/latest",
    });
  });

  it("needs no authentication", async () => {
    const response = await getUpdate(MANIFEST_VARS);
    expect(response.status).toBe(200);
  });

  it("allows public caching for one hour", async () => {
    const response = await getUpdate(MANIFEST_VARS);
    expect(response.headers.get("Cache-Control")).toBe("public, max-age=3600");
  });

  it("answers with JSON", async () => {
    const response = await getUpdate(MANIFEST_VARS);
    expect(response.headers.get("Content-Type")).toContain("application/json");
  });

  it("returns null for an empty download URL", async () => {
    const response = await getUpdate({ ...MANIFEST_VARS, KANAL_DOWNLOAD_URL: "" });
    expect(response.status).toBe(200);
    expect((await response.json<{ downloadUrl: unknown }>()).downloadUrl).toBeNull();
  });

  it("returns null for a missing download URL", async () => {
    const { KANAL_DOWNLOAD_URL: _omitted, ...vars } = MANIFEST_VARS;
    const response = await getUpdate(vars);
    expect(response.status).toBe(200);
    expect((await response.json<{ downloadUrl: unknown }>()).downloadUrl).toBeNull();
  });

  it("returns null for empty or missing release notes", async () => {
    const empty = await getUpdate({ ...MANIFEST_VARS, KANAL_RELEASE_NOTES: "" });
    expect((await empty.json<{ notes: unknown }>()).notes).toBeNull();

    const { KANAL_RELEASE_NOTES: _omitted, ...vars } = MANIFEST_VARS;
    const missing = await getUpdate(vars);
    expect((await missing.json<{ notes: unknown }>()).notes).toBeNull();
  });

  it("ignores the version and platform query parameters", async () => {
    const plain = await getUpdate(MANIFEST_VARS);
    const withQuery = await getUpdate(MANIFEST_VARS, "&version=0.0.1&platform=linux");
    expect(withQuery.status).toBe(200);
    expect(await withQuery.json()).toEqual(await plain.json());
  });

  it.each([
    ["latest is missing", { KANAL_LATEST_VERSION: undefined }],
    ["latest is empty", { KANAL_LATEST_VERSION: "" }],
    ["latest is not x.y.z", { KANAL_LATEST_VERSION: "1.2" }],
    ["latest has a pre-release suffix", { KANAL_LATEST_VERSION: "1.2.0-beta" }],
    ["minimum is missing", { KANAL_MIN_SUPPORTED_VERSION: undefined }],
    ["minimum is empty", { KANAL_MIN_SUPPORTED_VERSION: "" }],
    ["minimum is not x.y.z", { KANAL_MIN_SUPPORTED_VERSION: "latest" }],
    ["minimum has a fourth part", { KANAL_MIN_SUPPORTED_VERSION: "1.0.0.1" }],
  ])("answers 404 when %s", async (_name, change) => {
    const vars = { ...MANIFEST_VARS, ...change } as UpdateVars;
    const response = await getUpdate(vars);
    expect(response.status).toBe(404);
  });
});

describe("shipped defaults", () => {
  it("serves latest 1.0.0 and minimum 0.0.0 from wrangler.toml", async () => {
    const response = await SELF.fetch(`${GW}?action=update`);
    expect(response.status).toBe(200);
    const manifest = await response.json<Record<string, unknown>>();
    expect(manifest.latest).toBe("1.0.0");
    expect(manifest.minimumSupported).toBe("0.0.0");
    expect(manifest.notes).toBeNull();
    expect(manifest.downloadUrl).toBe("https://github.com/TONiiV/Kanal/releases/latest");
  });
});

describe("minimum supported version on create", () => {
  it.each([
    ["older", "0.9.9"],
    ["a lower minor", "1.0.9"],
    ["missing", undefined],
    ["empty", ""],
    ["garbage", "banana"],
    ["two parts", "2.0"],
    ["a pre-release", "2.0.0-beta"],
  ])("answers 426 when the version is %s", async (_name, version) => {
    const response = await createWith(minimumAt("1.1.0"), version);
    expect(response.status).toBe(426);
    const body = await response.json<{ error: string; minimumSupported: string }>();
    expect(body.minimumSupported).toBe("1.1.0");
    expect(typeof body.error).toBe("string");
    expect(body.error.length).toBeGreaterThan(0);
  });

  it("compares each part as a number, not as text", async () => {
    const older = await createWith(minimumAt("1.10.0"), "1.9.0");
    expect(older.status).toBe(426);
    expect((await older.json<{ minimumSupported: string }>()).minimumSupported).toBe("1.10.0");

    const newer = await createWith(minimumAt("1.9.0"), "1.10.0");
    expect(newer.status).not.toBe(426);
  });

  it("answers 426 before any registry call", async () => {
    let registryCalls = 0;
    const registry = new Proxy(
      {},
      {
        get() {
          registryCalls += 1;
          throw new Error("registry must not be touched");
        },
      },
    );
    const response = await createWith(minimumAt("1.1.0"), "1.0.0", "unknown-device-token", {
      REGISTRY: registry,
    });
    expect(response.status).toBe(426);
    expect(registryCalls).toBe(0);
  });

  it("answers 426, not 401, for an unknown device token", async () => {
    const response = await createWith(minimumAt("1.1.0"), "1.0.0", "no-such-device-token");
    expect(response.status).toBe(426);
  });

  it("passes an equal version to the device check", async () => {
    const response = await createWith(minimumAt("1.1.0"), "1.1.0");
    expect(response.status).toBe(401);
  });

  it("passes a newer version to the device check", async () => {
    const response = await createWith(minimumAt("1.1.0"), "2.0.0");
    expect(response.status).toBe(401);
  });

  it("creates a room for a registered device on an equal or newer version", async () => {
    const deviceToken = await activateDevice();
    for (const version of ["1.1.0", "1.1.1", "2.0.0"]) {
      const response = await createWith(minimumAt("1.1.0"), version, deviceToken);
      expect(response.status).toBe(200);
      const body = await response.json<{ hostTicket: string; inviteTicket: string }>();
      expect(body.hostTicket).toBeTruthy();
      expect(body.inviteTicket).toBeTruthy();
    }
  });

  it("accepts a missing header while the minimum is 0.0.0", async () => {
    const deviceToken = await activateDevice();
    const response = await createWith(minimumAt("0.0.0"), undefined, deviceToken);
    expect(response.status).toBe(200);
  });

  it.each([
    ["missing", undefined],
    ["empty", ""],
    ["not x.y.z", "1.2"],
  ])("skips the version check when the minimum is %s", async (_name, minimum) => {
    const vars = { ...MANIFEST_VARS, KANAL_MIN_SUPPORTED_VERSION: minimum } as UpdateVars;
    const response = await createWith(vars, "0.0.1");
    expect(response.status).toBe(401);
  });

  it("does not gate other routes on the version", async () => {
    const response = await worker.fetch(
      new Request(`${GW}?action=publish`, {
        method: "POST",
        headers: { Authorization: "Bearer not-a-ticket", "Content-Type": "application/json" },
        body: "{}",
      }),
      withVars(minimumAt("9.9.9")),
    );
    expect(response.status).toBe(401);
  });
});
