import { test } from "node:test";
import assert from "node:assert/strict";
import { classify, isPageOnlyHost, labelFor, merge } from "../dist-test/media.js";

const STREAM = "https://cdn.example.com/vod/9f2/master.m3u8";

test("takes manifests, whatever the content type says", () => {
  // Servers label m3u8 as everything from application/octet-stream to text/plain, so the
  // extension has to be enough on its own.
  for (const type of ["application/vnd.apple.mpegurl", "text/plain", ""]) {
    const found = classify(STREAM, type, 0);
    assert.equal(found?.form, "stream", type);
  }

  assert.equal(classify("https://cdn.example.com/v/index.mpd", "application/dash+xml", 0)?.form, "stream");
});

test("takes whole media files above the noise floor", () => {
  const found = classify("https://cdn.example.com/clip.mp4", "video/mp4", 40 * 1024 * 1024);
  assert.equal(found?.form, "file");
  assert.equal(found?.bytes, 40 * 1024 * 1024);
});

test("drops the fragments a manifest already accounts for", () => {
  // This is the whole reason the sniffer is usable: one hour of video is thousands of these,
  // and listing them would bury the single manifest that is actually worth downloading.
  for (const url of [
    "https://cdn.example.com/vod/9f2/seg-00412.ts",
    "https://cdn.example.com/vod/9f2/chunk-3.m4s",
    "https://cdn.example.com/vod/9f2/audio-88.aac",
    "https://cdn.example.com/vod/9f2/subs.vtt",
  ]) {
    assert.equal(classify(url, "video/mp2t", 900_000), null, url);
  }
});

test("drops previews, stings and everything that is not media", () => {
  assert.equal(classify("https://cdn.example.com/hover.mp4", "video/mp4", 40_000), null);
  assert.equal(classify("https://example.com/page", "text/html", 90_000), null);
  assert.equal(classify("https://example.com/app.js", "application/javascript", 900_000), null);
  assert.equal(classify("ftp://example.com/clip.mp4", "video/mp4", 9_000_000), null);
});

test("knows which sites can only be downloaded as a page", () => {
  for (const url of [
    "https://www.youtube.com/watch?v=abc",
    "https://youtu.be/abc",
    "https://m.bilibili.com/video/BV1",
    "https://x.com/someone/status/1",
  ]) {
    assert.equal(isPageOnlyHost(url), true, url);
  }

  assert.equal(isPageOnlyHost("https://cdn.example.com/clip.mp4"), false);
  assert.equal(isPageOnlyHost("https://notyoutube.com/watch"), false);
});

test("names a manifest after the folder when the file name says nothing", () => {
  assert.equal(labelFor(STREAM), "9f2/master.m3u8");
  assert.equal(labelFor("https://cdn.example.com/vod/interview-part-2.mp4"), "interview-part-2.mp4");
});

test("keeps the list deduplicated and bounded", () => {
  const one = { url: STREAM, form: "stream", label: "a", bytes: 0 };
  const list = merge([], one);

  // A live stream re-requests its manifest every few seconds; each of those is the same video.
  assert.equal(merge(list, { ...one }), list);

  let grown = list;
  for (let index = 0; index < 40; index++) {
    grown = merge(grown, { url: `${STREAM}?${index}`, form: "stream", label: "x", bytes: 0 }, 24);
  }

  assert.equal(grown.length, 24);
});
