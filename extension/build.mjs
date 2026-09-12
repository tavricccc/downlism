// Bundles the extension into dist/. No framework: the whole thing is a few hundred lines,
// and a runtime would be larger than the code it carries.
import { build } from "esbuild";
import { cp, mkdir, rm } from "node:fs/promises";

const outdir = "dist";

await rm(outdir, { recursive: true, force: true });
await mkdir(outdir, { recursive: true });

await build({
  entryPoints: ["src/background.ts", "src/popup.ts"],
  outdir,
  bundle: true,
  format: "esm",
  target: "chrome120",
  minify: false,
  sourcemap: false,
});

for (const file of ["manifest.json", "popup.html", "icon128.png"]) {
  await cp(file, `${outdir}/${file}`).catch(() => {});
}

console.log(`Extension built into ${outdir}/`);
