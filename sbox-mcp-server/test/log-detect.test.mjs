// sbox-dev.log auto-detection must work off-Windows (GitHub issue #10).

import { test } from "node:test";
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";

import { locateSboxLog, steamRootsFor } from "../dist/tools/diagnostics.js";

function fakeHome() {
  return fs.mkdtempSync(path.join(os.tmpdir(), "sbox-home-"));
}

function plantLog(lib) {
  const logDir = path.join(lib, "steamapps", "common", "sbox", "logs");
  fs.mkdirSync(logDir, { recursive: true });
  const p = path.join(logDir, "sbox-dev.log");
  fs.writeFileSync(p, "log\n");
  return p;
}

test("SBOX_LOG_PATH override wins on every platform", () => {
  const home = fakeHome();
  const explicit = path.join(home, "custom.log");
  fs.writeFileSync(explicit, "x");
  for (const platform of ["linux", "darwin", "win32"]) {
    const r = locateSboxLog({ env: explicit, platform, home });
    assert.equal(r.path, explicit, platform);
  }
});

test("linux: finds the log under ~/.steam/steam (native Steam)", () => {
  const home = fakeHome();
  const steam = path.join(home, ".steam", "steam");
  fs.mkdirSync(path.join(steam, "steamapps"), { recursive: true });
  fs.writeFileSync(path.join(steam, "steamapps", "libraryfolders.vdf"), `"libraryfolders"\n{\n\t"0"\n\t{\n\t\t"path"\t\t"${steam}"\n\t}\n}\n`);
  const expected = plantLog(steam);
  const r = locateSboxLog({ env: undefined, platform: "linux", home });
  assert.equal(r.path, expected);
});

test("linux: follows a second library listed in libraryfolders.vdf", () => {
  const home = fakeHome();
  const steam = path.join(home, ".local", "share", "Steam");
  const extra = path.join(home, "games", "SteamLibrary");
  fs.mkdirSync(path.join(steam, "steamapps"), { recursive: true });
  fs.writeFileSync(
    path.join(steam, "steamapps", "libraryfolders.vdf"),
    `"libraryfolders"\n{\n\t"0" { "path" "${steam}" }\n\t"1" { "path" "${extra}" }\n}\n`
  );
  const expected = plantLog(extra);
  const r = locateSboxLog({ env: undefined, platform: "linux", home });
  assert.equal(r.path, expected);
  assert.ok(r.tried.length >= 2);
});

test("darwin: probes ~/Library/Application Support/Steam", () => {
  const home = fakeHome();
  const roots = steamRootsFor("darwin", home);
  assert.deepEqual(roots, [path.join(home, "Library", "Application Support", "Steam")]);
});

test("nothing found → path null with the probed candidates listed", () => {
  const home = fakeHome();
  const r = locateSboxLog({ env: undefined, platform: "linux", home });
  assert.equal(r.path, null);
  assert.ok(Array.isArray(r.tried));
});
