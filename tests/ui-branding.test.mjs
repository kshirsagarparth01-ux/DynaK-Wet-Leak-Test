import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const read = (relativePath) => readFile(path.join(root, relativePath));
const text = async (relativePath) => (await read(relativePath)).toString("utf8");

function pngSize(buffer) {
  assert.equal(buffer.toString("ascii", 1, 4), "PNG");
  return [buffer.readUInt32BE(16), buffer.readUInt32BE(20)];
}

function icoSizes(buffer) {
  assert.equal(buffer.readUInt16LE(0), 0);
  assert.equal(buffer.readUInt16LE(2), 1);
  return Array.from({ length: buffer.readUInt16LE(4) }, (_, index) => {
    const offset = 6 + index * 16;
    const width = buffer[offset] || 256;
    const height = buffer[offset + 1] || 256;
    return `${width}x${height}`;
  }).sort((a, b) => Number(a.split("x")[0]) - Number(b.split("x")[0]));
}

test("approved branding assets keep their source dimensions and icon resolutions", async () => {
  const desktopHeader = await read("src/DynaK.WetLeakTest.Desktop/Assets/dynak-header.png");
  const webHeader = await read("src/DynaK.Service/wwwroot/images/dynak-header.png");
  assert.deepEqual(pngSize(desktopHeader), [1080, 100]);
  assert.deepEqual(webHeader, desktopHeader);
  assert.deepEqual(pngSize(await read("src/DynaK.WetLeakTest.Desktop/Assets/dynak-parc-app-icon.png")), [1254, 1254]);
  assert.deepEqual(icoSizes(await read("src/DynaK.WetLeakTest.Desktop/Assets/dynak-parc-app-icon.ico")), [
    "16x16", "24x24", "32x32", "48x48", "64x64", "128x128", "256x256",
  ]);
});

test("dashboard uses Dyna-K header and contains no test or cycle time", async () => {
  const index = await text("src/DynaK.Service/wwwroot/index.html");
  const app = await text("src/DynaK.Service/wwwroot/app.js");
  assert.match(index, /images\/dynak-header\.png/);
  assert.match(index, /DYNA-K WET LEAK TEST STATION/);
  assert.doesNotMatch(index, /parc-logo|TEST TIME|currentTestTime|CYCLE TIME|currentCycleTime/i);
  assert.doesNotMatch(app, /currentTestTime|currentCycleTime|cycleTimeSeconds|liveSignalText\(signals, "Time"\)/);
});

test("Windows and NSIS identity paths use the combined application icon", async () => {
  const project = await text("src/DynaK.WetLeakTest.Desktop/DynaK.WetLeakTest.Desktop.csproj");
  const window = await text("src/DynaK.WetLeakTest.Desktop/MainWindow.xaml");
  const installer = await text("installer/DynaK.Installer.nsi");
  const release = await text("build-release.ps1");
  assert.match(project, /<ApplicationIcon>Assets\\dynak-parc-app-icon\.ico<\/ApplicationIcon>/);
  assert.match(window, /Icon="Assets\/dynak-parc-app-icon\.ico"/);
  assert.match(window, /Assets\/dynak-header\.png/);
  assert.match(installer, /Icon "\$\{APP_ICON\}"/);
  assert.match(installer, /UninstallIcon "\$\{APP_ICON\}"/);
  assert.match(installer, /MUI_(?:UN)?ICON "\$\{APP_ICON\}"/);
  assert.match(installer, /DisplayIcon" "\$INSTDIR\\\$\{DESKTOP_EXE\},0"/);
  assert.match(release, /\[string\]\$Version = '1\.0\.34'/);
  assert.match(release, /\/DAPP_ICON=\$appIcon/);
});
