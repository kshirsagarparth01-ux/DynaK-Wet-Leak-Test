import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../src/DynaK.Service/wwwroot/app.js", import.meta.url), "utf8");
const indexSource = await readFile(new URL("../src/DynaK.Service/wwwroot/index.html", import.meta.url), "utf8");
const releaseSource = await readFile(new URL("../build-release.ps1", import.meta.url), "utf8");
const installerSource = await readFile(new URL("../installer/DynaK.Installer.nsi", import.meta.url), "utf8");
const desktopSource = await readFile(new URL("../src/DynaK.WetLeakTest.Desktop/MainWindow.xaml.cs", import.meta.url), "utf8");
const backendHostSource = await readFile(new URL("../src/DynaK.WetLeakTest.Desktop/BackendHost.cs", import.meta.url), "utf8");
const serviceProjectSource = await readFile(new URL("../src/DynaK.Service/DynaK.Service.csproj", import.meta.url), "utf8");
const mappingRows = { innerHTML: "" };
const buildFields = new Map(["buildAppVersion", "buildUiId", "buildDate", "buildSourceRevision"].map((id) => [id, { textContent: "" }]));
//code change by chatgpt
// const currentFields = new Map([
//   "currentSerialNumber", "currentQr", "currentPartNo", "currentLeak", "currentLeakUnit", "currentLeakRange", "currentPlcQr",
//   "currentResult", "currentMode", "currentError"
// ].map((id) => [id, { textContent: "", disabled: false, className: "" }]));
const currentFields = new Map([
  "currentSerialNumber", "currentPartNo", "currentLeak", "currentLeakUnit", "currentLeakRange",
  "currentResult", "currentMode", "currentError"
].map((id) => [id, { textContent: "", disabled: false, className: "" }]));
const context = vm.createContext({
  state: {
    config: { supportedByteOrders: ["", "ABCD", "BADC", "CDAB", "DCBA"] },
    settingsEditing: true,
    plcSignals: [],
    //code change by chatgpt
    // parts: [{ qrCode: "QR-123" }]
    parts: [{ partNumber: "PART-50" }]
  },
  $: (id) => id === "mappingRows" ? mappingRows : buildFields.get(id) ?? currentFields.get(id) ?? null,
  dataTypeOptions: () => [{ value: "AsciiString", label: "ASCII STRING" }],
  escapeHtml: (value) => String(value ?? ""),
  selectOptions: () => "<option>DEFAULT</option>",
  valueMapMarkup: () => "<span>—</span>",
  updateMappingDerivedControls: () => {},
  renderMappingLiveValues: () => {}
});

for (const name of [
  "registerCountForDataType",
  "isByteOrderRelevant",
  "renderMappingRows",
  "formatRegisterEnd",
  "parseEditableRegisterRange",
  "mappingNeedsValueMap",
  "updateRegisterRangeControl",
  "updateByteOrderControl",
  "updateValueMapControl",
  "parseMappingRange",
  "formatNormalizedRange",
  "parseValueMap",
  "validateSettingsBeforeSave",
  "liveSignalText",
  "formatLeakTestValue",
  "formatLeakRange",
  //code change by chatgpt
  // "renderCurrentPart",
  "leakUnit",
  "renderCurrentPart",
  "resultClass",
  "renderBuildInfo"
]) {
  vm.runInContext(extractFunction(name), context, { filename: "app.js" });
}

test("one register hides Byte Order and two registers show it", () => {
  assert.equal(context.isByteOrderRelevant(1), false);
  assert.equal(context.isByteOrderRelevant(2), true);
  assert.equal(context.isByteOrderRelevant(4), true);

  const byteOrder = control();
  const empty = control();
  const count = { value: "1" };
  const row = rowWith({
    "[data-map-byte-order]": byteOrder,
    "[data-byte-order-empty]": empty,
    "[data-map-length]": count
  });

  context.updateByteOrderControl(row);
  assert.equal(byteOrder.hidden, true);
  assert.equal(byteOrder.disabled, true);
  assert.equal(empty.hidden, false);

  count.value = "2";
  context.updateByteOrderControl(row);
  assert.equal(byteOrder.hidden, false);
  assert.equal(byteOrder.disabled, false);
  assert.equal(empty.hidden, true);
});

test("mapping rows render editable range ends and correct initial Byte Order visibility", () => {
  context.renderMappingRows([
    mapping("Time", "D1020", "AsciiString", 1),
    mapping("Leak Test Value", "D1025", "AsciiString", 2)
  ]);

  assert.match(mappingRows.innerHTML, /data-map-range-input value="D1020"/);
  assert.match(mappingRows.innerHTML, /data-map-range-input value="D1026"/);
  assert.equal((mappingRows.innerHTML.match(/data-map-byte-order hidden/g) || []).length, 1);
  assert.equal((mappingRows.innerHTML.match(/data-byte-order-empty hidden/g) || []).length, 1);
});

test("Serial Number mapping renders disabled with the editable D2000 start", () => {
  context.renderMappingRows([{
    ...mapping("Serial Number", "D2000", "", 1),
    addressType: "D Register",
    enabled: false
  }]);

  assert.match(mappingRows.innerHTML, /data-signal-name="Serial Number"/);
  assert.match(mappingRows.innerHTML, /data-map-enabled\s+aria-label="Enable Serial Number"/);
  assert.match(mappingRows.innerHTML, /data-map-address-type/);
  assert.match(mappingRows.innerHTML, /data-map-address value="D2000" placeholder="Pending"/);
});

test("displayed register range end is editable and stays synchronized with count", () => {
  const address = { value: "D2060" };
  const count = { value: "5", disabled: true };
  const range = control("");
  range.setCustomValidity = (message) => { range.validationMessage = message; };
  const row = rowWith({
    "[data-map-data-type]": { value: "AsciiString" },
    "[data-map-address]": address,
    "[data-map-length]": count,
    "[data-map-range-input]": range
  });

  context.updateRegisterRangeControl(row);
  assert.equal(range.value, "D2064");
  assert.equal(range.disabled, false);
  assert.equal(count.disabled, false);
  assert.equal(context.formatRegisterEnd("D2060", 5), "D2064");
  assert.deepEqual({ ...context.parseEditableRegisterRange("D2067", "D2060") }, { address: "D2060", count: 8 });
  assert.deepEqual({ ...context.parseEditableRegisterRange("D2060-D2067", "D2060") }, { address: "D2060", count: 8 });
  assert.equal(context.parseEditableRegisterRange("D2059", "D2060"), null);
  assert.equal(context.parseEditableRegisterRange("D2064-D2060", "D2060"), null);
});

test("bit and handshake mappings expose Value Mapping", () => {
  const textarea = control("");
  const empty = control();
  const row = rowWith({
    "[data-map-value-map]": textarea,
    "[data-value-map-empty]": empty,
    "[data-map-address-type]": { value: "Coil" },
    "[data-map-data-type]": { value: "Bool" }
  }, "Custom Discrete");

  context.updateValueMapControl(row);
  assert.equal(textarea.hidden, false);
  assert.equal(textarea.disabled, false);
  assert.equal(empty.hidden, true);
  assert.equal(context.mappingNeedsValueMap({ signalName: "Data Saved", addressType: "D Register", dataType: "UInt16", valueMap: "" }), true);
  assert.equal(context.mappingNeedsValueMap({ signalName: "Part Data Ready", addressType: "D Register", dataType: "UInt16", valueMap: "" }), true);
});

test("effective ranges and overlap errors are calculated before save", () => {
  assert.equal(context.formatRegisterEnd("D2020", 1), "D2020");
  assert.equal(context.formatRegisterEnd("D2020", 5), "D2024");

  const update = {
    liveLeakValueFilePath: "C:\\DynaK\\live_leak_value.txt",
    lowerLimit: 0,
    upperLimit: 0.5,
    reportRootFolder: "C:\\Users\\Public\\Documents\\DynaK Leak Test Report",
    signalMappings: [
      mapping("Time", "D2020", "AsciiString", 6),
      mapping("Leak Test Value", "D2025", "AsciiString", 5),
      { ...mapping("System Ready", "D2100", "UInt16", 1), direction: "Write", valueMap: "0=ON;1=OFF" },
      { ...mapping("Communication OK", "D2105", "UInt16", 1), direction: "Write", valueMap: "0=OFF;1=ON" },
      { ...mapping("Data Saved", "D2110", "UInt16", 1), direction: "Write", valueMap: "0=OFF;1=ON" }
    ]
  };

  const overlapErrors = context.validateSettingsBeforeSave(update);
  assert.ok(overlapErrors.some((error) => error.includes("Time D2020-D2025 overlaps Leak Test Value D2025-D2029")));
  assert.ok(overlapErrors.some((error) => error.includes("Maximum allowed range: D2020-D2024")));

  update.signalMappings[0].length = 5;
  assert.deepEqual([...context.validateSettingsBeforeSave(update)], []);
});

test("scalar datatype ranges larger than their minimum are accepted before save", () => {
  for (const count of [1, 2, 5]) {
    const errors = context.validateSettingsBeforeSave({
      liveLeakValueFilePath: "C:\\DynaK\\live_leak_value.txt",
      lowerLimit: 0,
      upperLimit: 0.5,
      reportRootFolder: "C:\\Users\\Public\\Documents\\DynaK Leak Test Report",
      signalMappings: [mapping("Actual Part Count", "D2005", "Int16", count)]
    });
    assert.deepEqual([...errors], []);
  }
  assert.doesNotMatch(source, /setCompatibleRegisterCount/);
});

test("Current Test renders the shared decoded PLC snapshot and configured value mappings", () => {
 // <!-- Changed manually using GPT -->
  context.state.config.leakTestUnit = "LPM";
  // context.state.config.leakTestUnit = "bar";
  context.state.config.lowerLimit = 0;
  context.state.config.upperLimit = 0.5;
  context.renderCurrentPart({
    serialNumber: "PLC-SN-123",
    //code change by chatgpt
    // qrCode: "QR-123",
    partNumber: "PART-50",
    // <!-- Changed manually using GPT -->
    leakTestUnit: "LPM",
    lowerLimit: 0,
    upperLimit: 0.5
  }, {
    //code change by chatgpt
    // "QR Code Value": { rawValue: "QR-123", interpretedValue: "QR-123", valueMapMatched: false },
    "Part Number": { rawValue: "PART-50", interpretedValue: "PART-50", valueMapMatched: false },
    "Leak Test Value": { rawValue: "0 .125", interpretedValue: 0.125, valueMapMatched: false },
    "OK": { rawValue: 1, interpretedValue: "OK", valueMapMatched: true },
    "NG": { rawValue: 0, interpretedValue: 0, valueMapMatched: false },
    "Auto / Manual": { rawValue: 1, interpretedValue: "Auto", valueMapMatched: true },
    "Time": { rawValue: "16:42:18", interpretedValue: "16:42:18", valueMapMatched: false },
    "Error": { rawValue: 2, interpretedValue: "Fixture clamp low", valueMapMatched: true }
  }, "OK");

  //code change by chatgpt
  // assert.equal(currentFields.get("currentQr").textContent, "QR-123");
  // assert.equal(currentFields.get("currentQr").disabled, false);
  assert.equal(currentFields.get("currentPartNo").textContent, "PART-50");
  assert.equal(currentFields.get("currentSerialNumber").textContent, "PLC-SN-123");
  assert.equal(currentFields.get("currentLeak").textContent, "0.1250");
  assert.equal(currentFields.get("currentLeakUnit").textContent, "LPM");
  assert.equal(currentFields.get("currentLeakRange").textContent, "0.00 TO 0.500 LPM");
  assert.equal(currentFields.get("currentResult").textContent, "OK");
  assert.equal(currentFields.get("currentMode").textContent, "Auto");
  assert.equal(currentFields.get("currentError").textContent, "Fixture clamp low");

  context.renderCurrentPart(null, {
    "Time": { rawValue: "bad", interpretedValue: null, error: "undecodable" }
  });
  //code change by chatgpt
  // assert.equal(currentFields.get("currentQr").textContent, "--");
  assert.equal(currentFields.get("currentPartNo").textContent, "--");
});

test("Leak Test Value displays four decimal places without changing its numeric meaning", () => {
  assert.equal(context.formatLeakTestValue(0.125), "0.1250");
  assert.equal(context.formatLeakTestValue(0.4876), "0.4876");
  assert.equal(context.formatLeakTestValue(0), "0.0000");
});

test("too-small multi-register values remain a per-signal runtime decode concern", () => {
  const errors = context.validateSettingsBeforeSave({
    liveLeakValueFilePath: "C:\\DynaK\\live_leak_value.txt",
    lowerLimit: 0,
    upperLimit: 0.5,
    reportRootFolder: "C:\\Users\\Public\\Documents\\DynaK Leak Test Report",
    signalMappings: [mapping("Time", "D2020", "Int32", 1)]
  });
  assert.deepEqual([...errors], []);
});

test("packaged build identity is rendered in Engineer Settings", () => {
  context.renderBuildInfo({
    appVersion: "1.0.13",
    uiBuildId: "20260821-120000-abc123def456",
    buildDate: "2026-08-21T12:00:00.0000000Z",
    sourceRevision: "abc1234"
  });

  assert.equal(buildFields.get("buildAppVersion").textContent, "1.0.13");
  assert.equal(buildFields.get("buildUiId").textContent, "20260821-120000-abc123def456");
  assert.equal(buildFields.get("buildDate").textContent, "2026-08-21T12:00:00.0000000Z");
  assert.equal(buildFields.get("buildSourceRevision").textContent, "abc1234");
  assert.match(indexSource, /SYSTEM INFO/);
  assert.match(indexSource, /id="buildUiId"/);
  assert.match(indexSource, /LEAK TEST SETTINGS/);
  assert.match(indexSource, /name="reportRootFolder"/);
  assert.match(indexSource, /name="automaticDailyExportEnabled"/);
});

test("release pipeline rejects stale or legacy renderer payloads", () => {
  assert.match(releaseSource, /function Assert-PublishedUi/);
  assert.match(releaseSource, /Get-FileHash/);
  assert.match(releaseSource, /renderer file is stale or differs from source/);
  assert.match(releaseSource, /legacy code\.html was included/);
  assert.match(releaseSource, /portable package contains the wrong UI build identity/);
});

test("service release embeds SQLitePCLRaw and uses the trusted Windows SQLite library", () => {
  assert.match(serviceProjectSource, /Microsoft\.Data\.Sqlite\.Core/);
  assert.match(serviceProjectSource, /SQLitePCLRaw\.provider\.winsqlite3/);
  assert.doesNotMatch(serviceProjectSource, /Include="Microsoft\.Data\.Sqlite"/);
  assert.match(releaseSource, /PublishSingleFile=true/);
  assert.match(releaseSource, /loose SQLite runtime binaries remain/);
  assert.match(releaseSource, /Zone\.Identifier/);
  assert.match(releaseSource, /Invoke-CodeSignPublishedBinaries/);
  assert.match(releaseSource, /\$makeNsisPath \/WX/);
});

test("installer success is gated on application-specific backend health", () => {
  const runningIndex = installerSource.indexOf("wait_service_running:");
  const healthIndex = installerSource.indexOf("wait_backend_health:");
  const successIndex = installerSource.indexOf("BACKEND HEALTH CONFIRMED");

  assert.ok(runningIndex >= 0 && runningIndex < healthIndex && healthIndex < successIndex);
  assert.match(installerSource, /http:\/\/127\.0\.0\.1:5055\/api\/health/);
  assert.match(installerSource, /DynaK\.WetLeakTest\.Service/);
  assert.match(installerSource, /SQLite\/backend initialization completed/);
});

test("desktop surfaces the latest service initialization failure", () => {
  assert.match(backendHostSource, /ReadLatestServiceFailure/);
  assert.match(backendHostSource, /Hosting failed|Latest service error/);
  assert.match(backendHostSource, /dynak-service\.log/);
  assert.match(desktopSource, /catch \(TimeoutException ex\)/);
  assert.match(desktopSource, /ShowStartupError\("DynaK service startup timed out\.", ex\.Message/);
});

test("NSIS upgrade preserves machine data and pre-creates report years without deleting reports", () => {
  const stopIndex = installerSource.indexOf("Call StopExistingService");
  const cleanIndex = installerSource.indexOf('RMDir /r "$INSTDIR\\service"');
  const copyIndex = installerSource.indexOf('SetOutPath "$INSTDIR\\service"');

  assert.ok(stopIndex >= 0 && stopIndex < cleanIndex && cleanIndex < copyIndex);
  assert.match(installerSource, /Machine data under ProgramData and reports under Public Documents are intentionally preserved/);
  for (const year of [2026, 2027, 2028, 2029]) {
    assert.match(installerSource, new RegExp(`CreateReportYear ${year}`));
  }
  assert.match(installerSource, /DynaK Leak Test Report/);
  assert.doesNotMatch(installerSource, /RMDir \/r "\$ReportRoot"/);
  assert.doesNotMatch(installerSource, /msiexec|\.msi\b|WiX|Burn/i);
});

test("desktop clears persistent WebView renderer caches before loading the HMI", () => {
  const clearIndex = desktopSource.indexOf("ClearBrowsingDataAsync");
  const navigateIndex = desktopSource.indexOf("NavigateToHmiAsync(settings.BackendUri)");

  assert.notEqual(clearIndex, -1);
  assert.ok(clearIndex < navigateIndex);
  assert.match(desktopSource, /CoreWebView2BrowsingDataKinds\.DiskCache/);
  assert.match(desktopSource, /CoreWebView2BrowsingDataKinds\.CacheStorage/);
  assert.match(desktopSource, /CoreWebView2BrowsingDataKinds\.ServiceWorkers/);
});

function extractFunction(name) {
  //code change by chatgpt
  // const start = source.indexOf(`function ${name}(`);
  // assert.notEqual(start, -1, `function ${name} was not found in app.js`);
  // const next = source.indexOf("\nfunction ", start + 1);
  // return source.slice(start, next < 0 ? source.length : next);
  const pattern = new RegExp(`^function ${name.replace(/[.*+?^\${}()|[\\]\\]/g, "\\function extractFunction(name) {
  const start = source.indexOf(`function ${name}(`);
  assert.notEqual(start, -1, `function ${name} was not found in app.js`);
  const next = source.indexOf("\nfunction ", start + 1);
  return source.slice(start, next < 0 ? source.length : next);
}")}\\(`, "m");
  const match = pattern.exec(source);
  assert.notEqual(match, null, `function ${name} was not found in app.js`);
  const start = match.index;
  const remainder = source.slice(start + 1);
  const nextMatch = /^function\s+[A-Za-z_$][\\w$]*\s*\(/m.exec(remainder);
  const next = nextMatch ? start + 1 + nextMatch.index : source.length;
  return source.slice(start, next);
}

function control(value = "") {
  return {
    value,
    disabled: false,
    hidden: false,
    classList: {
      toggle(_name, force) {
        this.owner.hidden = Boolean(force);
      },
      owner: null
    }
  };
}

function rowWith(elements, signalName = "Test") {
  for (const element of Object.values(elements)) {
    if (element?.classList) element.classList.owner = element;
  }
  return { dataset: { signalName }, querySelector: (selector) => elements[selector] ?? null };
}

function mapping(signalName, address, dataType, length) {
  return {
    signalName,
    address,
    addressType: "D Register",
    dataType,
    direction: "Read",
    length,
    byteOrder: "ABCD",
    valueMap: null,
    enabled: true
  };
}
