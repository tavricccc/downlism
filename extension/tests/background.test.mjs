import { test } from "node:test";
import assert from "node:assert/strict";
import vm from "node:vm";
import { build } from "esbuild";
import { fileURLToPath } from "node:url";

const bundled = await build({ entryPoints: [fileURLToPath(new URL("../src/background.ts", import.meta.url))], bundle: true, write: false, format: "iife" });
const contentBundle = await build({ entryPoints: [fileURLToPath(new URL("../src/content.ts", import.meta.url))], bundle: true, write: false, format: "iife" });
const tick = () => new Promise(resolve => setImmediate(resolve));
function event() {
  const listeners = [];
  return { addListener: fn => listeners.push(fn), fire: (...args) => listeners.forEach(fn => fn(...args)) };
}
function worker({ settings = {}, load } = {}) {
  const calls = { cancelled: [], native: [], media: [] };
  const chrome = {
    storage: {
      local: { get: () => load ?? Promise.resolve(settings), set: async () => {} },
      session: { get: async () => ({}), set: async value => calls.media.push(value), remove: async () => {} },
      onChanged: event(),
    },
    runtime: {
      onInstalled: event(), onMessage: event(),
      sendNativeMessage: async (_, message) => { calls.native.push(message); return { accepted: true }; },
    },
    downloads: {
      onCreated: event(), onChanged: event(), onErased: event(), onDeterminingFilename: event(),
      cancel: async id => { calls.cancelled.push(id); chrome.downloads.onChanged.fire({ id, state: { current: "interrupted" } }); },
      erase: async () => [], download: async () => 100,
    },
    webRequest: { onHeadersReceived: event() },
    tabs: { onUpdated: event(), onRemoved: event(), query: async () => [] },
    cookies: { getAll: async () => [] },
    action: { setBadgeText: async () => {}, setBadgeBackgroundColor: async () => {} },
    contextMenus: { onClicked: event(), removeAll: () => {}, create: () => {} },
  };
  vm.runInNewContext(bundled.outputFiles[0].text, { chrome, URL, navigator: { userAgent: "test" }, setTimeout });
  return { chrome, calls };
}
const url = "https://files.example/archive.zip";
const headers = (overrides = {}) => ({
  url, method: "GET", statusCode: 200, type: "main_frame", tabId: 1,
  responseHeaders: [
    { name: "Content-Disposition", value: 'attachment; filename="archive.zip"' },
    { name: "Content-Type", value: "application/octet-stream" },
    { name: "Content-Length", value: "2000000" },
  ], ...overrides,
});
const item = (overrides = {}) => ({ id: 1, url, finalUrl: url, filename: "archive.zip", mime: "application/octet-stream", fileSize: 2_000_000, totalBytes: 2_000_000, state: "in_progress", ...overrides });

test("MEGA chunk storms and API responses never hand over from headers", async () => {
  const { chrome, calls } = worker();
  await tick();
  for (let index = 0; index < 400; index++) {
    chrome.webRequest.onHeadersReceived.fire(headers({ url: `https://gfs.userstorage.mega.co.nz/${index}`, type: "xmlhttprequest", initiator: "https://mega.nz" }));
    chrome.webRequest.onHeadersReceived.fire(headers({ url: `https://api.example/responses?id=${index}`, type: "xmlhttprequest" }));
  }
  await tick();
  assert.equal(calls.native.length, 0);
  assert.equal(calls.cancelled.length, 0);
  assert.equal(calls.media.length, 0);
});

test("XHR attachment cannot supply evidence for the early download route", async () => {
  const { chrome, calls } = worker();
  await tick();
  chrome.webRequest.onHeadersReceived.fire(headers({ type: "xmlhttprequest" }));
  chrome.downloads.onCreated.fire(item());
  await tick();
  assert.equal(calls.cancelled.length, 0);
});

test("confirmed attachment is handed over once despite cancellation events", async () => {
  const { chrome, calls } = worker();
  await tick();
  chrome.webRequest.onHeadersReceived.fire(headers());
  chrome.downloads.onCreated.fire(item());
  chrome.downloads.onDeterminingFilename.fire(item());
  await tick();
  assert.deepEqual(calls.cancelled, [1]);
  assert.equal(calls.native.length, 1);
});

test("POST and unobserved responses cannot use the filename fallback", async () => {
  const { chrome, calls } = worker();
  await tick();
  chrome.webRequest.onHeadersReceived.fire(headers({ method: "POST" }));
  chrome.downloads.onCreated.fire(item());
  chrome.downloads.onDeterminingFilename.fire(item());
  chrome.downloads.onDeterminingFilename.fire(item({ id: 2, url: "https://api.example/responses", finalUrl: "https://api.example/responses" }));
  await tick();
  assert.equal(calls.cancelled.length, 0);
  assert.equal(calls.native.length, 0);
});

test("custom blocked sites prevent takeover and media sniffing", async () => {
  const { chrome, calls } = worker({ settings: { blockedHosts: ["files.example"] } });
  await tick();
  chrome.webRequest.onHeadersReceived.fire(headers());
  chrome.downloads.onCreated.fire(item());
  chrome.downloads.onDeterminingFilename.fire(item());
  await tick();
  assert.equal(calls.cancelled.length, 0);
  assert.equal(calls.media.length, 0);
});

test("cold settings, disabled takeover, blob, MEGA and JSON remain in browser", async () => {
  const cold = worker({ load: new Promise(() => {}) });
  cold.chrome.downloads.onDeterminingFilename.fire(item());
  assert.equal(cold.calls.cancelled.length, 0);
  const disabled = worker({ settings: { enabled: false } });
  const active = worker();
  await tick();
  disabled.chrome.downloads.onDeterminingFilename.fire(item());
  for (const overrides of [
    { url: "blob:https://mega.nz/id", finalUrl: "blob:https://mega.nz/id" },
    { referrer: "https://mega.nz/file/id" },
    { finalUrl: "https://gfs.userstorage.mega.co.nz/chunk" },
    { filename: "responses", mime: "application/json" },
    { fileSize: -1, totalBytes: -1 },
    { fileSize: -1, totalBytes: 512 },
  ]) active.chrome.downloads.onDeterminingFilename.fire(item(overrides));
  await tick();
  assert.equal(disabled.calls.cancelled.length, 0);
  assert.equal(active.calls.cancelled.length, 0);
});

test("HTTP file-looking links and download attributes do not suppress page clicks", () => {
  let click;
  let prevented = 0;
  let sent = 0;
  vm.runInNewContext(contentBundle.outputFiles[0].text, {
    document: { addEventListener: (_, fn) => { click = fn; } },
    chrome: { runtime: { sendMessage: async () => { sent++; return { handled: true }; } } },
    URL, location: { href: "https://mega.nz/file/id" },
  });
  for (const href of ["https://example.com/a.zip", "https://example.com/clip.mp4", "https://example.com/responses"]) {
    const anchor = { href, hasAttribute: name => name === "download", getAttribute: () => "a.zip" };
    click({ button: 0, target: { closest: () => anchor }, preventDefault: () => prevented++, stopPropagation: () => {} });
  }
  assert.equal(prevented, 0);
  assert.equal(sent, 0);
});
