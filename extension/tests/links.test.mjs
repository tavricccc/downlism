import { test } from "node:test";
import assert from "node:assert/strict";
import { looksLikeADownload, extensionOfPath } from "../dist-test/links.js";

const BASE = "https://example.com/page";

test("takes links the author marked as downloads", () => {
  assert.equal(looksLikeADownload("https://example.com/report", true, BASE), true);
});

test("takes binaries by extension", () => {
  for (const name of ["a.zip", "setup.exe", "ubuntu.iso", "clip.mp4", "lib.tar.gz"]) {
    assert.equal(looksLikeADownload(`https://example.com/${name}`, false, BASE), true, name);
  }
});

test("leaves ordinary navigation alone", () => {
  // A false positive here stops a real page from opening, which is far worse than missing a
  // download the fallback route would have caught anyway.
  for (const href of [
    "https://example.com/",
    "https://example.com/docs/guide",
    "https://example.com/index.html",
    "https://example.com/data.json",
    "https://example.com/report.pdf",
    "/relative/page",
    "#anchor",
    "?query=1",
  ]) {
    assert.equal(looksLikeADownload(href, false, BASE), false, href);
  }
});

test("ignores schemes that are not web downloads", () => {
  for (const href of ["mailto:a@b.c", "javascript:alert(1)", "file:///C:/a.zip", "ftp://h/a.zip"]) {
    assert.equal(looksLikeADownload(href, false, BASE), false, href);
  }
});

test("query strings do not hide the extension", () => {
  assert.equal(looksLikeADownload("https://example.com/a.zip?token=1", false, BASE), true);
});

test("a dot in a directory name is not an extension", () => {
  assert.equal(looksLikeADownload("https://example.com/v1.2/download", false, BASE), false);
});

test("extensionOfPath reads the last segment only", () => {
  assert.equal(extensionOfPath("/a/b.c/d.zip"), "zip");
  assert.equal(extensionOfPath("/a/b"), "");
});
