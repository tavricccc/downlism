import { test } from "node:test";
import assert from "node:assert/strict";
import { DEFAULTS, normalizeSettings, parseHosts, parseExtensions, permitsAutomatic } from "../dist-test/settings.js";

test("old preferences gain safe defaults", () => {
  const settings = normalizeSettings({ enabled: false, minimumBytes: 512 });
  assert.equal(settings.enabled, false);
  assert.equal(settings.sniffMedia, true);
  assert.deepEqual(settings.blockedHosts, []);
});
test("invalid storage does not enable takeover", () => {
  for (const raw of [{ enabled: "false" }, { minimumBytes: -1 }, { skipExtensions: null }, { allowedHosts: [123] }]) {
    assert.equal(normalizeSettings(raw).enabled, false);
    assert.throws(() => normalizeSettings(raw, true));
  }
});
test("normalizes and validates user input", () => {
  assert.deepEqual(parseExtensions(".ZIP, EXE; zip mp4"), ["zip", "exe", "mp4"]);
  assert.deepEqual(parseHosts("*.Example.COM.\ncdn.example.org"), ["example.com", "cdn.example.org"]);
  assert.throws(() => parseHosts("https://example.com/path"));
  assert.throws(() => parseHosts("user@example.com"));
  assert.throws(() => parseExtensions("../zip"));
});
test("site exclusion wins over inclusion and checks subdomain boundaries", () => {
  const settings = { ...DEFAULTS, allowedHosts: ["example.com"], blockedHosts: ["private.example.com"] };
  assert.equal(permitsAutomatic(settings, "https://cdn.example.com/file"), true);
  assert.equal(permitsAutomatic(settings, "https://notexample.com/file"), false);
  assert.equal(permitsAutomatic(settings, "https://example.com/file", "https://private.example.com/page"), false);
  assert.equal(permitsAutomatic({ ...settings, enabled: false }, "https://example.com/file"), false);
});
