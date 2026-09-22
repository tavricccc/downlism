import { test } from "node:test";
import assert from "node:assert/strict";
import {
  extensionForMime,
  isPageManagedDownload,
  TakeoverState,
} from "../dist-test/takeover.js";

const response = (url) => ({ url, fileName: "archive.zip", totalBytes: 2_000_000 });

test("recognises MEGA pages and encrypted storage hosts as page-managed", () => {
  assert.equal(isPageManagedDownload("https://mega.nz/file/abc"), true);
  assert.equal(isPageManagedDownload("https://gfs270n123.userstorage.mega.co.nz/chunk"), true);
  assert.equal(isPageManagedDownload("https://notmega.nz/file"), false);
  assert.equal(isPageManagedDownload("blob:https://mega.nz/id"), true);
  assert.equal(isPageManagedDownload("https://mega.io/file/abc"), true);
  assert.equal(isPageManagedDownload("https://cdn.example/chunk", "https://mega.nz/file/abc"), true);
  assert.equal(isPageManagedDownload("https://mega.nz.evil.example/file"), false);
});

test("maps browser-viewable MIME types to skip extensions", () => {
  assert.equal(extensionForMime("application/json; charset=utf-8"), "json");
  assert.equal(extensionForMime("application/problem+json"), "json");
  assert.equal(extensionForMime("text/html"), "html");
  assert.equal(extensionForMime("application/pdf"), "pdf");
  assert.equal(extensionForMime("application/octet-stream"), undefined);
});

test("response headers alone never claim a download", () => {
  const state = new TakeoverState();
  state.remember(response("https://api.example/responses"), 1_000);

  assert.equal(state.pendingCount, 1);
  assert.equal(state.claimFallback(7), true);
});

test("a matching browser download consumes the response once", () => {
  const state = new TakeoverState();
  const candidate = response("https://example.com/archive.zip");
  state.remember(candidate, 1_000);

  assert.deepEqual(state.claimResponse(7, [candidate.url], 2_000), candidate);
  assert.equal(state.claimResponse(7, [candidate.url], 2_000), undefined);
  assert.equal(state.claimFallback(7, 2_000), false);
  assert.equal(state.pendingCount, 0);
});

test("unrelated fetch responses cannot claim a real browser download", () => {
  const state = new TakeoverState();
  state.remember(response("https://mega.nz/encrypted-chunk/1"), 1_000);

  assert.equal(state.claimResponse(9, ["blob:https://mega.nz/file-id"], 2_000), undefined);
  assert.equal(state.claimFallback(9), true);
});

test("expired response evidence is ignored", () => {
  const state = new TakeoverState(30_000);
  const candidate = response("https://example.com/archive.zip");
  state.remember(candidate, 1_000);

  assert.equal(state.claimResponse(4, [candidate.url], 31_000), undefined);
  assert.equal(state.pendingCount, 0);
});

test("pending response storage is bounded during chunk storms", () => {
  const state = new TakeoverState(30_000, 3);
  for (let index = 0; index < 10; index += 1) {
    state.remember(response(`https://mega.nz/chunk/${index}`), 1_000 + index);
  }

  assert.equal(state.pendingCount, 3);
  assert.equal(state.claimResponse(1, ["https://mega.nz/chunk/0"], 2_000), undefined);
  assert.equal(state.claimResponse(2, ["https://mega.nz/chunk/9"], 2_000)?.url, "https://mega.nz/chunk/9");
});

test("cancellation tombstones expire without browser cleanup events", () => {
  const state = new TakeoverState();
  assert.equal(state.claimFallback(5, 1_000), true);
  assert.equal(state.claimFallback(5, 299_000), false);
  assert.equal(state.claimFallback(5, 301_000), true);
});

test("released IDs may be used by a later browser event", () => {
  const state = new TakeoverState();
  assert.equal(state.claimFallback(5), true);
  assert.equal(state.claimFallback(5), false);

  state.release(5);
  assert.equal(state.claimFallback(5), true);
});
