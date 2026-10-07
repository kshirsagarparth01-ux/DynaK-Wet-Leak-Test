const state = {
  currentPart: null,
  parts: [],
  config: null,
  plcSignals: [],
  runtimeStatus: "Stopped",
  settingsEditing: false,
  stationBusy: false,
  historyExportBusy: false
};

const STATE_POLL_MS = 1000;
const $ = (id) => document.getElementById(id);

document.querySelectorAll("[data-view]").forEach((button) => {
  button.addEventListener("click", () => showView(button.dataset.view));
});

document.querySelectorAll("[data-date-field]").forEach((field) => {
  const input = field.querySelector("input[type='date']");
  const update = () => {
    field.querySelector("[data-date-display]").textContent = displayDateValue(input.value);
  };
  input.addEventListener("change", update);
  input.addEventListener("input", update);
  input.addEventListener("click", () => input.showPicker?.());
  update();
});

$("historyFilters").addEventListener("submit", (event) => {
  event.preventDefault();
  loadHistory();
});
$("exportHistoryExcel").addEventListener("click", exportHistoryExcel);

$("settingsForm").addEventListener("submit", async (event) => {
  event.preventDefault();
  await saveSettings();
});

$("editSettings").addEventListener("click", () => {
  if (!settingsCanEdit()) {
    setSettingsStatus("STOP STATION TO EDIT", "error");
    return;
  }
  setSettingsEditing(true);
  setSettingsStatus("EDITING", "");
});

$("cancelSettings").addEventListener("click", () => loadSettings());
$("addShift").addEventListener("click", addShiftRow);
$("shiftRows").addEventListener("click", (event) => {
  const remove = event.target.closest("[data-shift-remove]");
  if (!remove || !state.settingsEditing) return;
  const rows = $("shiftRows").querySelectorAll("tr");
  if (rows.length <= 1) {
    setSettingsStatus("AT LEAST ONE SHIFT IS REQUIRED", "error");
    return;
  }
  remove.closest("tr").remove();
  updateShiftActionAvailability();
});
$("browseDatabase").addEventListener("click", browseDatabase);
$("stationControl").addEventListener("click", toggleStation);
$("mappingRows").addEventListener("change", (event) => {
  const select = event.target.closest("[data-map-data-type], [data-map-address-type]");
  if (!select) return;
  const row = select.closest("tr");
  updateMappingDerivedControl(row);
});
$("mappingRows").addEventListener("input", (event) => {
  const rangeInput = event.target.closest("[data-map-range-input]");
  if (rangeInput) {
    const row = rangeInput.closest("tr");
    const parsed = parseEditableRegisterRange(rangeInput.value, row?.querySelector("[data-map-address]")?.value);
    rangeInput.setCustomValidity(parsed ? "" : "Use a range end such as D2064.");
    if (!parsed || !row) return;
    row.querySelector("[data-map-address]").value = parsed.address;
    row.querySelector("[data-map-length]").value = String(parsed.count);
    updateByteOrderControl(row);
    return;
  }

  const field = event.target.closest("[data-map-address], [data-map-length]");
  if (!field) return;
  updateMappingDerivedControl(field.closest("tr"));
});
$("closeDrawer").addEventListener("click", closeDrawer);
$("drawerOverlay").addEventListener("click", closeDrawer);
//code change by chatgpt
// $("currentQr").addEventListener("click", () => {
//   const part = state.parts.find((item) => item.qrCode === $("currentQr").textContent);
//   if (part) openDrawer(part);
// });

window.chrome?.webview?.addEventListener("message", (event) => {
  const message = event.data || {};
  if (message.type === "database-selected" && message.path) {
    $("settingsForm").databasePath.value = message.path;
    setSettingsStatus("DATABASE LOCATION SELECTED - SAVE TO APPLY", "");
    return;
  }

  if (message.type === "history-export-result") {
    setHistoryExportBusy(false);
    if (message.cancelled) {
      setHistoryExportStatus("EXPORT CANCELLED", "");
      return;
    }

    if (message.success) {
      const count = message.recordCount == null ? "" : ` (${message.recordCount} RECORD${Number(message.recordCount) === 1 ? "" : "S"})`;
      setHistoryExportStatus(`EXPORTED${count}`, "saved");
      return;
    }

    setHistoryExportStatus(message.message || "EXCEL EXPORT FAILED", "error");
  }
});

function showView(name) {
  document.querySelectorAll(".view").forEach((view) => view.classList.remove("active"));
  document.querySelectorAll(".nav-item").forEach((item) => item.classList.toggle("active", item.dataset.view === name));
  $(`${name}View`).classList.add("active");
  if (name === "history") loadHistory();
  if (name === "faults") loadEvents();
  if (name === "settings") loadSettings();
}

async function apiJson(url, options) {
  const response = await fetch(url, options);
  const text = await response.text();
  let data = null;
  if (text) {
    try {
      data = JSON.parse(text);
    } catch {
      data = null;
    }
  }
  if (!response.ok) {
    const message = data?.errors?.join(" ") || data?.message || `${url} failed: ${response.status}`;
    throw new Error(message);
  }
  return data;
}

async function toggleStation() {
  if (state.stationBusy) return;
  const shouldStop = ["Running", "Starting", "ConnectionError"].includes(state.runtimeStatus);
  state.stationBusy = true;
  renderStationControl();
  try {
    await apiJson(`/api/station/${shouldStop ? "stop" : "start"}`, { method: "POST" });
    await loadState();
  } catch (error) {
    showApplicationNotice(`Station control failed: ${error.message}`);
  } finally {
    state.stationBusy = false;
    renderStationControl();
  }
}

async function loadState() {
  try {
    const data = await apiJson("/api/state");
    state.currentPart = data.currentPart;
    state.parts = (data.recentParts || []).slice(0, 10);
    state.runtimeStatus = data.runtimeStatus || "Stopped";
    renderState(data);
  } catch (error) {
    state.currentPart = null;
    setStatus($("apiStatus"), "API OFFLINE", "bad");
    setStatus($("plcStatus"), "PLC UNKNOWN", "idle");
    setStatus($("runtimeStatus"), "STATE UNKNOWN", "idle");
    setStatus($("machineBadge"), "MACHINE UNKNOWN", "idle");
    showApplicationNotice(`Application API unavailable: ${error.message}`);
    clearCurrentPart();
    state.plcSignals = [];
    renderMappingLiveValues([]);
  }
}

function renderState(data) {
  const now = new Date(data.now);
  const runtime = String(data.runtimeStatus || "Stopped");
  const runtimeText = data.runtimeStatusText || runtime.toUpperCase();

  state.runtimeStatus = runtime;
  $("dateText").textContent = formatDate(now);
  $("timeText").textContent = formatTime(now);
  $("shiftText").textContent = data.shift || "SHIFT --";
  setStatus($("apiStatus"), "API ONLINE", "good");
  setStatus(
    $("plcStatus"),
    data.plcConnected
      ? "PLC CONNECTED • HANDSHAKE WRITES"
      : `PLC ${data.serviceStatusText || (data.plcConfigured ? "DISCONNECTED" : "NOT CONFIGURED")} • HANDSHAKE WRITES`,
    data.plcConnected ? "good" : runtime === "Stopped" ? "idle" : "bad"
  );
  setStatus(
    $("runtimeStatus"),
    runtimeText,
    runtime === "Running" ? "good" : ["Starting", "Stopping"].includes(runtime) ? "warning" : runtime === "ConnectionError" ? "bad" : "idle"
  );
  setStatus($("machineBadge"), data.machineRunning ? "MACHINE RUNNING" : "MACHINE IDLE", data.machineRunning ? "good" : "idle");

  $("targetCount").textContent = data.targetPartsPerShift ?? "--";
  $("actualCount").textContent = data.actualPartCount ?? "--";
  $("okCount").textContent = data.okCount ?? 0;
  $("ngCount").textContent = data.ngCount ?? 0;

  if (runtime === "ConnectionError" && data.lastError) showApplicationNotice(data.lastError);
  else hideApplicationNotice();

  renderCurrentPart(data.currentPart, data.livePlcSignals, data.liveResult);
  renderPlcDiagnostic(data);
  renderRecords($("recentRows"), state.parts, false);
  if ($("historyView").classList.contains("active")) loadHistory();
  renderStationControl();
  updateSettingsEditAvailability();
}

function renderPlcDiagnostic(data) {
  const diagnostic = data.plcDiagnostic || {};
  const lastRead = diagnostic.lastSuccessfulRead ? new Date(diagnostic.lastSuccessfulRead) : null;
  setStatus($("diagnosticReadOnly"), diagnostic.readOnly ? "HANDSHAKE WRITES DISABLED" : "HANDSHAKE WRITES ENABLED", diagnostic.readOnly ? "warning" : "idle");
  $("lastPlcRead").textContent = `Last PLC read: ${lastRead ? formatTime(lastRead) : "--"}`;

  const interfaces = diagnostic.pcNetworkInterfaces || [];
  const ethernet = interfaces.filter((item) => String(item.type || "").toLowerCase().includes("ethernet"));
  const shownInterfaces = ethernet.length ? ethernet : interfaces;
  $("diagnosticPcNetwork").textContent = shownInterfaces.length
    ? shownInterfaces.map((item) => `${item.ipv4Address} / ${item.subnetMask || "mask unknown"}${item.compatibleWithPlc ? " • COMPATIBLE" : " • DIFFERENT SUBNET"}`).join(" | ")
    : "NO ACTIVE IPv4 INTERFACE";
  $("diagnosticEndpoint").textContent = diagnostic.plcIpAddress && diagnostic.plcPort
    ? `${diagnostic.plcIpAddress}:${diagnostic.plcPort} • Unit ${diagnostic.unitId ?? "--"} • D offset ${diagnostic.dRegisterModbusOffset ?? 0}`
    : "NOT CONFIGURED";
  $("diagnosticProtocol").textContent = `${diagnostic.protocol || "--"} • ${diagnostic.communicationLibrary || "--"}`;
  $("diagnosticConnection").textContent = `${data.serviceStatusText || "DISCONNECTED"} • HANDSHAKE WRITES`;
  $("diagnosticLastRead").textContent = lastRead ? `${formatDate(lastRead)} ${formatTime(lastRead)}` : "--";
  $("diagnosticTiming").textContent = `${diagnostic.pollingIntervalMs ?? "--"} ms poll • ${diagnostic.connectionTimeoutMs ?? "--"} ms timeout`;
  $("diagnosticError").textContent = diagnostic.lastError
    ? `${diagnostic.lastErrorCategory || "ERROR"}: ${diagnostic.lastError}`
    : diagnostic.compatibleSubnetFound ? "--" : "NETWORK: No active IPv4 interface appears to share the PLC subnet";

  state.plcSignals = diagnostic.signals || [];
  renderMappingLiveValues(state.plcSignals);
}

function renderStationControl() {
  const button = $("stationControl");
  const shouldStop = ["Running", "Starting", "ConnectionError"].includes(state.runtimeStatus);
  button.disabled = state.stationBusy || state.runtimeStatus === "Stopping";
  button.classList.toggle("stop", shouldStop);
  $("stationControlText").textContent = state.stationBusy
    ? "PLEASE WAIT"
    : shouldStop ? "STOP" : state.runtimeStatus === "Stopping" ? "STOPPING" : "START";
}

//code change by chatgpt
// function renderCurrentPart(record, signals, liveResult) {
//   const qrCode = liveSignalText(signals, "QR Code Value");
//   const partNumber = liveSignalText(signals, "Part Number");
//   const leakValue = liveSignalText(signals, "Leak Test Value");
//   const result = liveResult ? String(liveResult).trim() : null;
//   const mode = liveSignalText(signals, "Auto / Manual");
//   const error = liveSignalText(signals, "Error");
//   const matchingRecord = record && (
//     (qrCode && qrCode === String(record.qrCode || "")) ||
//     (partNumber && partNumber === String(record.partNumber || ""))
//   ) ? record : null;
//
//   $("currentSerialNumber").textContent = matchingRecord?.serialNumber || "--";
//   $("currentQr").disabled = !qrCode || !state.parts.some((part) => part.qrCode === qrCode);
//   $("currentQr").textContent = qrCode || "--";
//   $("currentPartNo").textContent = partNumber || "--";
//   $("currentLeak").textContent = formatLeakTestValue(leakValue);
//   $("currentLeakUnit").textContent = leakUnit();
//   $("currentLeakRange").textContent = matchingRecord
//     ? formatLeakRange(matchingRecord.lowerLimit, matchingRecord.upperLimit)
//     : formatLeakRange(state.config?.lowerLimit, state.config?.upperLimit);
//   $("currentPlcQr").textContent = qrCode || "--";
//   $("currentResult").textContent = result || "--";
//   $("currentResult").className = `result-chip ${result ? resultClass(result) : "neutral"}`;
//   $("currentMode").textContent = mode || "--";
//   $("currentError").textContent = error || "--";
// }
function renderCurrentPart(record, signals, liveResult) {
  const partNumber = liveSignalText(signals, "Part Number");
  const leakValue = liveSignalText(signals, "Leak Test Value");
  const result = liveResult ? String(liveResult).trim() : null;
  const mode = liveSignalText(signals, "Auto / Manual");
  const error = liveSignalText(signals, "Error");
  const matchingRecord = record &&
    partNumber &&
    partNumber === String(record.partNumber || "")
    ? record
    : null;

  $("currentSerialNumber").textContent = matchingRecord?.serialNumber || "--";
  $("currentPartNo").textContent = partNumber || "--";
  $("currentLeak").textContent = formatLeakTestValue(leakValue);
  $("currentLeakUnit").textContent = leakUnit();
  $("currentLeakRange").textContent = matchingRecord
    ? formatLeakRange(matchingRecord.lowerLimit, matchingRecord.upperLimit)
    : formatLeakRange(state.config?.lowerLimit, state.config?.upperLimit);
  $("currentResult").textContent = result || "--";
  $("currentResult").className = `result-chip ${result ? resultClass(result) : "neutral"}`;
  $("currentMode").textContent = mode || "--";
  $("currentError").textContent = error || "--";
}

function liveSignalText(signals, signalName) {
  const signal = signals?.[signalName];
  if (!signal || signal.error) return null;
  const value = signal.interpretedValue ?? signal.rawValue;
  const text = value == null ? "" : String(value).trim();
  return text || null;
}

//code change by chatgpt
// function clearCurrentPart() {
//   $("currentSerialNumber").textContent = "--";
//   $("currentQr").disabled = true;
//   $("currentQr").textContent = "--";
//   $("currentPartNo").textContent = "--";
//   $("currentLeak").textContent = "--";
//   $("currentLeakUnit").textContent = leakUnit();
//   $("currentLeakRange").textContent = formatLeakRange(state.config?.lowerLimit, state.config?.upperLimit);
//   $("currentPlcQr").textContent = "--";
//   $("currentResult").textContent = "--";
//   $("currentResult").className = "result-chip neutral";
//   $("currentMode").textContent = "--";
//   $("currentError").textContent = "--";
// }
function clearCurrentPart() {
  $("currentSerialNumber").textContent = "--";
  $("currentPartNo").textContent = "--";
  $("currentLeak").textContent = "--";
  $("currentLeakUnit").textContent = leakUnit();
  $("currentLeakRange").textContent = formatLeakRange(state.config?.lowerLimit, state.config?.upperLimit);
  $("currentResult").textContent = "--";
  $("currentResult").className = "result-chip neutral";
  $("currentMode").textContent = "--";
  $("currentError").textContent = "--";
}

function renderRecords(target, parts, includeDate) {
  target.innerHTML = "";
  if (parts.length === 0) {
    //code change by chatgpt
    // target.innerHTML = `<tr><td colspan="${includeDate ? 11 : 9}">${includeDate ? "No records found" : "No records available"}</td></tr>`;
    target.innerHTML = `<tr><td colspan="${includeDate ? 10 : 8}">${includeDate ? "No records found" : "No records available"}</td></tr>`;
    return;
  }

  for (const part of parts) {
    const attempt = latestAttempt(part);
    const overallResult = logicalResult(part);
    const row = document.createElement("tr");
    row.dataset.partId = part.id;
    const cssResult = resultClass(overallResult);
    row.className = cssResult === "ng" ? "ng-row" : ["rework", "ng-rework"].includes(cssResult) ? "rework-row" : "";
    row.addEventListener("click", () => openDrawer(part));
    const date = new Date(attempt.timestamp);
    row.innerHTML = includeDate
      ? `<td>${formatDate(date)}</td><td>${formatTime(date)}</td>${partCells(part, attempt, true)}`
      : `<td>${formatTime(date)}</td>${partCells(part, attempt, false)}`;
    target.appendChild(row);
  }
}

function partCells(part, attempt, history) {
  const error = machineError(attempt, "-");
  // <!-- Changed manually using GPT -->
  const leak = attempt.leakTestValue == null
    ? "--"
    : `${formatLeakTestValue(attempt.leakTestValue)} ${escapeHtml(leakUnit())}`;
  // const leak = attempt.leakTestValue == null ? "--" : `${formatLeakTestValue(attempt.leakTestValue)} ${escapeHtml(attempt.leakTestUnit)}`;
  const leakRange = formatLeakRange(attempt.lowerLimit, attempt.upperLimit);
  const result = logicalResult(part);
  //code change by chatgpt
  // const common = `<td>${escapeHtml(attempt.serialNumber || "--")}</td><td>${escapeHtml(part.qrCode)}</td><td>${escapeHtml(part.partNumber)}</td><td class="right">${leak}</td><td>${escapeHtml(leakRange)}</td><td><span class="result-chip ${resultClass(result)}">${escapeHtml(result)}</span></td>`;
  const common = `<td>${escapeHtml(attempt.serialNumber || "--")}</td><td>${escapeHtml(part.partNumber)}</td><td class="right">${leak}</td><td>${escapeHtml(leakRange)}</td><td><span class="result-chip ${resultClass(result)}">${escapeHtml(result)}</span></td>`;
  return history
    ? `${common}<td>${escapeHtml(attempt.shift)}</td><td>${escapeHtml(modeText(attempt))}</td><td>${escapeHtml(error)}</td>`
    : `${common}<td>${escapeHtml(modeText(attempt))}</td><td>${escapeHtml(error)}</td>`;
}

async function loadHistory() {
  try {
    const params = historyQueryParams("500");
    renderRecords($("historyRows"), await apiJson(`/api/records?${params}`), true);
  } catch (error) {
    //code change by chatgpt
    // $("historyRows").innerHTML = `<tr><td colspan="11">Application API unavailable: ${escapeHtml(error.message)}</td></tr>`;
    $("historyRows").innerHTML = `<tr><td colspan="10">Application API unavailable: ${escapeHtml(error.message)}</td></tr>`;
  }
}

async function exportHistoryExcel() {
  if (state.historyExportBusy) return;

  const params = historyQueryParams();
  const query = params.toString();
  setHistoryExportBusy(true);
  setHistoryExportStatus("EXPORTING...", "");

  try {
    const suggestedFileName = historyExportFileName(new Date());
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage({
        type: "export-history-excel",
        query,
        suggestedFileName
      });
      return;
    }

    const url = `/api/records/export/excel${query ? `?${query}` : ""}`;
    const response = await fetch(url);
    if (!response.ok) {
      throw new Error(await responseErrorMessage(response, "Excel export failed"));
    }

    const blob = await response.blob();
    const fileName = responseFileName(response, suggestedFileName);
    const downloadUrl = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = downloadUrl;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(downloadUrl);
    const count = response.headers.get("X-DynaK-Record-Count");
    setHistoryExportStatus(`EXPORTED${count == null ? "" : ` (${count} RECORD${Number(count) === 1 ? "" : "S"})`}`, "saved");
  } catch (error) {
    setHistoryExportStatus(error.message || "EXCEL EXPORT FAILED", "error");
  } finally {
    if (!window.chrome?.webview) {
      setHistoryExportBusy(false);
    }
  }
}

function historyQueryParams(limit) {
  const form = new FormData($("historyFilters"));
  const params = new URLSearchParams();
  for (const [key, value] of form.entries()) {
    if (value) params.set(key, value);
  }
  if (limit) params.set("limit", limit);
  return params;
}

function setHistoryExportBusy(busy) {
  state.historyExportBusy = busy;
  const button = $("exportHistoryExcel");
  button.disabled = busy;
  button.textContent = busy ? "EXPORTING..." : "EXPORT EXCEL";
}

function setHistoryExportStatus(message, className) {
  const status = $("historyExportStatus");
  status.textContent = message;
  status.className = `history-export-status ${className || ""}`;
}

async function responseErrorMessage(response, fallback) {
  const text = await response.text();
  if (!text) return `${fallback}: ${response.status}`;
  try {
    const data = JSON.parse(text);
    return data?.errors?.join(" ") || data?.message || `${fallback}: ${response.status}`;
  } catch {
    return text;
  }
}

function responseFileName(response, fallback) {
  const disposition = response.headers.get("Content-Disposition") || "";
  const encoded = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
  if (encoded) {
    try {
      return decodeURIComponent(encoded[1]);
    } catch {
      return fallback;
    }
  }

  const quoted = /filename="?([^";]+)"?/i.exec(disposition);
  return quoted?.[1] || fallback;
}

async function loadEvents() {
  try {
    const events = await apiJson("/api/events?limit=500");
    const rows = $("eventRows");
    rows.innerHTML = "";
    if (events.length === 0) {
      rows.innerHTML = `<tr><td colspan="6">No events found.</td></tr>`;
      return;
    }

    for (const event of events) {
      const started = new Date(event.startedTimestamp);
      const cleared = event.clearedTimestamp ? new Date(event.clearedTimestamp) : null;
      const row = document.createElement("tr");
      row.className = event.severity === "ERROR" ? "ng-row" : "";
      row.innerHTML = `<td>${formatDate(started)} ${formatTime(started)}</td><td>${cleared ? `${formatDate(cleared)} ${formatTime(cleared)}` : "ACTIVE"}</td><td>${escapeHtml(event.eventType)}</td><td>${escapeHtml(event.severity)}</td><td>${escapeHtml(event.code || "-")}</td><td>${escapeHtml(event.description)}</td>`;
      rows.appendChild(row);
    }
  } catch (error) {
    $("eventRows").innerHTML = `<tr><td colspan="6">Application API unavailable: ${escapeHtml(error.message)}</td></tr>`;
  }
}

async function loadSettings() {
  loadBuildInfo();
  try {
    const config = await apiJson("/api/config");
    state.config = config;
    const form = $("settingsForm");
    form.stationName.value = config.stationName || "";
    form.plcIpAddress.value = config.plc?.ipAddress || "";
    form.plcPort.value = config.plc?.port ?? "";
    form.plcUnitId.value = config.plc?.unitId ?? "";
    form.dRegisterModbusOffset.value = config.plc?.dRegisterModbusOffset ?? 0;
    form.pollingIntervalMs.value = config.plc?.pollingIntervalMs ?? 1000;
    form.reconnectIntervalMs.value = config.plc?.reconnectIntervalMs ?? 5000;
    form.connectionTimeoutMs.value = config.plc?.connectionTimeoutMs ?? 3000;
    form.leakTestUnit.value = config.leakTestUnit || "bar";
    form.lowerLimit.value = config.lowerLimit ?? 0;
    form.upperLimit.value = config.upperLimit ?? 0.5;
    form.reportRootFolder.value = config.reportRootFolder || "";
    form.automaticDailyExportEnabled.checked = config.automaticDailyExportEnabled !== false;
    form.databasePath.value = config.databasePath || "";
    form.liveLeakValueFilePath.value = config.liveLeakValueFilePath || "";
    renderShiftRows(config.shifts || []);
    renderMappingRows(config.signalMappings || []);
    setSettingsEditing(false);
    setSettingsStatus(settingsCanEdit() ? config.plcConfigured ? "PLC CONFIGURED" : "PLC NOT CONFIGURED" : "STOP STATION TO EDIT", settingsCanEdit() && config.plcConfigured ? "saved" : "");
  } catch (error) {
    setSettingsStatus(`API OFFLINE: ${error.message}`, "error");
  }
}

async function loadBuildInfo() {
  try {
    renderBuildInfo(await apiJson("/api/build"));
  } catch {
    renderBuildInfo(null);
  }
}

function renderBuildInfo(info) {
  $("buildAppVersion").textContent = info?.appVersion || "UNAVAILABLE";
  $("buildUiId").textContent = info?.uiBuildId || "UNAVAILABLE";
  $("buildDate").textContent = info?.buildDate || "UNAVAILABLE";
  $("buildSourceRevision").textContent = info?.sourceRevision || "UNAVAILABLE";
}

async function saveSettings() {
  if (!state.settingsEditing) return;

  try {
    const update = readSettings();
    const validationErrors = validateSettingsBeforeSave(update);
    if (validationErrors.length > 0) {
      setSettingsStatus(validationErrors.join(" "), "error");
      return;
    }

    await apiJson("/api/config", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(update)
    });
    await loadSettings();
    setSettingsStatus("SAVED", "saved");
  } catch (error) {
    setSettingsStatus(error.message, "error");
  }
}

function readSettings() {
  const form = $("settingsForm");
  const mappingRows = [...$("mappingRows").querySelectorAll("tr")];
  const signalMappings = (state.config?.signalMappings || []).map((mapping, index) => {
    const row = mappingRows[index];
    const dataType = row.querySelector("[data-map-data-type]").value;
    const valueMap = row.querySelector("[data-map-value-map]");
    return {
      ...mapping,
      enabled: row.querySelector("[data-map-enabled]").checked,
      address: blankToNull(row.querySelector("[data-map-address]").value),
      addressType: row.querySelector("[data-map-address-type]").value,
      dataType,
      direction: mapping.direction || "Read",
      scalingFactor: mapping.scalingFactor ?? 1,
      offset: mapping.offset ?? 0,
      byteOrder: row.querySelector("[data-map-byte-order]")?.value ?? mapping.byteOrder ?? "",
      wordOrder: mapping.wordOrder || "",
      encoding: dataType === "AsciiString" ? (mapping.encoding || "ASCII") : (mapping.encoding || ""),
      format: mapping.format || "",
      valueMap: valueMap ? blankToNull(valueMap.value) : (mapping.valueMap ?? null),
      description: mapping.description ?? null,
      length: numberOrDefault(row.querySelector("[data-map-length]").value, mapping.length || 1)
    };
  });

  return {
    stationName: form.stationName.value,
    databasePath: form.databasePath.value,
    liveLeakValueFilePath: form.liveLeakValueFilePath.value,
    leakTestUnit: form.leakTestUnit.value,
    lowerLimit: Number(form.lowerLimit.value),
    upperLimit: Number(form.upperLimit.value),
    reportRootFolder: form.reportRootFolder.value,
    automaticDailyExportEnabled: form.automaticDailyExportEnabled.checked,
    plc: {
      ipAddress: blankToNull(form.plcIpAddress.value),
      port: numberOrNull(form.plcPort.value),
      unitId: numberOrNull(form.plcUnitId.value),
      dRegisterModbusOffset: numberOrDefault(form.dRegisterModbusOffset.value, 0),
      pollingIntervalMs: Number(form.pollingIntervalMs.value),
      reconnectIntervalMs: Number(form.reconnectIntervalMs.value),
      connectionTimeoutMs: Number(form.connectionTimeoutMs.value)
    },
    shifts: [...$("shiftRows").querySelectorAll("tr")].map((row) => ({
      name: row.querySelector("[data-shift-field='name']").value,
      startsAt: normalizeTime(row.querySelector("[data-shift-field='startsAt']").value),
      endsAt: normalizeTime(row.querySelector("[data-shift-field='endsAt']").value)
    })),
    signalMappings
  };
}

function renderShiftRows(shifts) {
  $("shiftRows").innerHTML = shifts.map(shiftRowMarkup).join("");
}

function renderMappingRows(mappings) {
  const typeOptions = dataTypeOptions();
  $("mappingRows").innerHTML = mappings.map((mapping) => {
    const registerCount = mapping.length || registerCountForDataType(mapping.dataType) || 1;
    const byteOrderRelevant = isByteOrderRelevant(registerCount);
    return `
    <tr data-signal-name="${escapeHtml(mapping.signalName)}">
      <td><input type="checkbox" data-setting-input data-map-enabled ${mapping.enabled ? "checked" : ""} aria-label="Enable ${escapeHtml(mapping.signalName)}"></td>
      <td><code class="mapping-live-value" data-live-raw="${escapeHtml(mapping.signalName)}">--</code></td>
      <td><code class="mapping-live-value" data-live-decoded="${escapeHtml(mapping.signalName)}">--</code></td>
      <td><span class="signal-label" title="Fixed application signal contract">${escapeHtml(mapping.signalName)}</span><small class="fixed-contract">FIXED CONTRACT</small></td>
      <td><input class="address-input" data-setting-input data-map-address value="${escapeHtml(mapping.address || "")}" placeholder="Pending"></td>
      <td><select data-setting-input data-map-address-type>${selectOptions(state.config?.supportedAddressTypes, mapping.addressType, "NOT SET")}</select></td>
      <td><select data-setting-input data-map-data-type><option value="" ${mapping.dataType ? "" : "selected"}>NOT SET</option>${typeOptions.map((option) => `<option value="${escapeHtml(option.value)}" ${option.value === mapping.dataType ? "selected" : ""}>${escapeHtml(option.label)}</option>`).join("")}</select></td>
      <td class="register-range-cell">
        <input class="register-range-input" data-setting-input data-map-range-input value="${escapeHtml(formatRegisterEnd(mapping.address, registerCount))}" aria-label="${escapeHtml(mapping.signalName)} register range end">
        <label>COUNT <input class="register-count-input" type="number" min="1" max="120" data-setting-input data-map-length value="${escapeHtml(registerCount)}"></label>
      </td>
      <td>
        <select data-setting-input data-map-byte-order ${byteOrderRelevant ? "" : "hidden"}>${selectOptions(state.config?.supportedByteOrders, mapping.byteOrder, "DEFAULT")}</select>
        <span class="empty-cell" data-byte-order-empty ${byteOrderRelevant ? "hidden" : ""}>—</span>
      </td>
      <td>${valueMapMarkup(mapping)}</td>
    </tr>`;
  }).join("");
  updateMappingDerivedControls();
  renderMappingLiveValues(state.plcSignals);
}

function shiftRowMarkup(shift) {
  return `<tr>
    <td><input data-setting-input data-shift-field="name" value="${escapeHtml(shift.name || "")}" required></td>
    <td><input type="time" data-setting-input data-shift-field="startsAt" value="${formatTimeInput(shift.startsAt)}" required></td>
    <td><input type="time" data-setting-input data-shift-field="endsAt" value="${formatTimeInput(shift.endsAt)}" required></td>
    <td><button type="button" class="remove-row" data-setting-input data-shift-remove>REMOVE</button></td>
  </tr>`;
}

function addShiftRow() {
  if (!state.settingsEditing) return;
  $("shiftRows").insertAdjacentHTML("beforeend", shiftRowMarkup({ name: "", startsAt: "", endsAt: "" }));
  updateShiftActionAvailability();
  const name = $("shiftRows").lastElementChild?.querySelector("[data-shift-field='name']");
  name?.focus();
  setSettingsStatus("ENTER THE NEW SHIFT NAME AND TIMES", "");
}

function browseDatabase() {
  if (!state.settingsEditing) return;
  if (!window.chrome?.webview) {
    setSettingsStatus("DATABASE BROWSE IS AVAILABLE IN THE INSTALLED DESKTOP APP", "error");
    return;
  }

  window.chrome.webview.postMessage({
    type: "select-database",
    currentPath: $("settingsForm").databasePath.value
  });
}

function setSettingsEditing(editing) {
  if (editing && !settingsCanEdit()) editing = false;
  state.settingsEditing = editing;
  document.querySelectorAll("[data-setting-input]").forEach((input) => {
    input.disabled = !editing;
  });
  $("editSettings").classList.toggle("hidden", editing);
  $("saveSettings").classList.toggle("hidden", !editing);
  $("cancelSettings").classList.toggle("hidden", !editing);
  updateMappingDerivedControls();
  updateShiftActionAvailability();
  updateSettingsEditAvailability();
}

function setSettingsStatus(message, className) {
  $("settingsStatus").textContent = message;
  $("settingsStatus").className = `settings-message ${className || ""}`;
}

function dataTypeOptions() {
  const options = state.config?.supportedDataTypeOptions || [];
  if (options.length > 0) return options;
  return (state.config?.supportedDataTypes || []).map((value) => ({ value, label: value }));
}

function selectOptions(values, selected, emptyLabel) {
  return (values || []).map((value) => {
    const label = value || emptyLabel || "NOT SET";
    return `<option value="${escapeHtml(value)}" ${value === (selected || "") ? "selected" : ""}>${escapeHtml(label)}</option>`;
  }).join("");
}

function valueMapMarkup(mapping) {
  return `<span data-map-value-map-control>
    <textarea data-setting-input data-map-value-map rows="2" placeholder="1=Label; 2=Label">${escapeHtml(mapping.valueMap || "")}</textarea>
    <span class="empty-cell" data-value-map-empty>—</span>
  </span>`;
}

function mappingNeedsValueMap(mapping) {
  const mappedSignals = new Set(["OK", "NG", "Auto / Manual", "Error", "Running Status", "Part Data Ready", "System Ready", "Communication OK", "Data Saved"]);
  const addressType = String(mapping.addressType || "").toLowerCase();
  return mappedSignals.has(mapping.signalName) ||
    mapping.dataType === "Bool" ||
    ["coil", "discrete input", "m bit"].includes(addressType) ||
    Boolean(String(mapping.valueMap || "").trim());
}

function updateMappingDerivedControls() {
  document.querySelectorAll("#mappingRows tr").forEach(updateMappingDerivedControl);
}

function updateMappingDerivedControl(row) {
  if (!row) return;
  updateRegisterRangeControl(row);
  updateByteOrderControl(row);
  updateValueMapControl(row);
}

function updateRegisterRangeControl(row) {
  if (!row) return;
  const select = row.querySelector("[data-map-data-type]");
  const input = row.querySelector("[data-map-length]");
  const rangeInput = row.querySelector("[data-map-range-input]");
  if (!select || !input) return;
  input.disabled = !state.settingsEditing;
  if (rangeInput) {
    rangeInput.disabled = !state.settingsEditing;
    rangeInput.value = formatRegisterEnd(row.querySelector("[data-map-address]")?.value, input.value);
    rangeInput.setCustomValidity("");
  }
}

function updateByteOrderControl(row) {
  if (!row) return;
  const select = row.querySelector("[data-map-byte-order]");
  const empty = row.querySelector("[data-byte-order-empty]");
  const registerCount = Number(row.querySelector("[data-map-length]")?.value) || 1;
  if (!select || !empty) return;
  const relevant = isByteOrderRelevant(registerCount);
  select.hidden = !relevant;
  empty.hidden = relevant;
  select.classList.toggle("hidden", !relevant);
  empty.classList.toggle("hidden", relevant);
  select.disabled = !state.settingsEditing || !relevant;
}

function isByteOrderRelevant(registerCount) {
  return Number(registerCount) >= 2;
}

function updateValueMapControl(row) {
  if (!row) return;
  const textarea = row.querySelector("[data-map-value-map]");
  const empty = row.querySelector("[data-value-map-empty]");
  if (!textarea || !empty) return;
  const relevant = mappingNeedsValueMap({
    signalName: row.dataset.signalName,
    addressType: row.querySelector("[data-map-address-type]")?.value,
    dataType: row.querySelector("[data-map-data-type]")?.value,
    valueMap: textarea.value
  });
  textarea.classList.toggle("hidden", !relevant);
  empty.classList.toggle("hidden", relevant);
  textarea.disabled = !state.settingsEditing || !relevant;
}

function formatRegisterEnd(address, count) {
  const start = String(address || "").trim();
  if (!start) return "--";
  const registerCount = Math.max(1, Number(count) || 1);
  const parsed = /^([a-zA-Z]*)(\d+)$/.exec(start);
  if (!parsed) return start.toUpperCase();

  const prefix = parsed[1].toUpperCase();
  const startNumber = Number(parsed[2]);
  return `${prefix}${startNumber + registerCount - 1}`;
}

function parseEditableRegisterRange(value, currentAddress) {
  const text = String(value || "").trim();
  const parsed = /^([a-zA-Z]*)(\d+)(?:\s*-\s*([a-zA-Z]*)(\d+))?$/.exec(text);
  if (!parsed) return null;

  if (parsed[4] == null) {
    const current = /^([a-zA-Z]*)(\d+)$/.exec(String(currentAddress || "").trim());
    if (!current) return null;
    const startPrefix = current[1].toUpperCase();
    const endPrefix = (parsed[1] || startPrefix).toUpperCase();
    const start = Number(current[2]);
    const end = Number(parsed[2]);
    if (startPrefix !== endPrefix || end < start) return null;
    return { address: `${startPrefix}${start}`, count: end - start + 1 };
  }

  const startPrefix = parsed[1].toUpperCase();
  const endPrefix = (parsed[3] || startPrefix).toUpperCase();
  const start = Number(parsed[2]);
  const end = Number(parsed[4]);
  if (startPrefix !== endPrefix || end < start) return null;
  return { address: `${startPrefix}${start}`, count: end - start + 1 };
}

function validateSettingsBeforeSave(update) {
  const errors = [];
  const spans = [];
  const supportedByteOrders = new Set(state.config?.supportedByteOrders || []);
  const handshakeSignals = new Set(["System Ready", "Communication OK", "Data Saved"]);

  for (const mapping of update.signalMappings || []) {
    const count = Number(mapping.length);
    if (!Number.isInteger(count) || count < 1) {
      errors.push(`${mapping.signalName} register count must be a positive whole number.`);
      continue;
    }

    if (count >= 2 && !supportedByteOrders.has(mapping.byteOrder || "")) {
      errors.push(`${mapping.signalName} byte order '${mapping.byteOrder}' is not supported.`);
    }

    const entries = parseValueMap(mapping.valueMap);
    if (entries.some((entry) => !entry.plcValue || !entry.meaning)) {
      errors.push(`${mapping.signalName} value mapping must use 'PLC value=Meaning'.`);
    }
    if (new Set(entries.map((entry) => entry.plcValue.toLowerCase())).size !== entries.length) {
      errors.push(`${mapping.signalName} value mapping contains a duplicate PLC value.`);
    }

    if (!mapping.enabled) continue;
    if (!mapping.address || !mapping.addressType || !mapping.dataType) {
      errors.push(`${mapping.signalName} requires an address, address type, and data type.`);
      continue;
    }

    const range = parseMappingRange(mapping, count);
    if (!range) {
      errors.push(`${mapping.signalName} address '${mapping.address}' is not valid for ${mapping.addressType}.`);
    } else {
      spans.push({ ...range, signalName: mapping.signalName, addressType: mapping.addressType });
    }

    if (["Coil", "Discrete Input", "M Bit"].includes(mapping.addressType) && count !== 1) {
      errors.push(`${mapping.signalName} address type ${mapping.addressType} supports exactly one bit.`);
    }
    if (handshakeSignals.has(mapping.signalName)) {
      const meanings = new Set(entries.map((entry) => entry.meaning.toUpperCase()));
      if (!meanings.has("ON") || !meanings.has("OFF")) {
        errors.push(`${mapping.signalName} requires ON and OFF value mappings.`);
      }
    }
    if (mapping.signalName === "Part Data Ready") {
      const meanings = new Set(entries.map((entry) => entry.meaning.toUpperCase()));
      if (!meanings.has("HIGH") || !meanings.has("LOW")) {
        errors.push("Part Data Ready requires HIGH and LOW value mappings.");
      }
    }
  }

  const groups = new Map();
  spans.forEach((span) => {
    const key = span.addressType.toLowerCase();
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(span);
  });
  groups.forEach((group) => {
    group.sort((left, right) => left.start - right.start || left.end - right.end);
    for (let index = 0; index < group.length - 1; index += 1) {
      const current = group[index];
      const next = group[index + 1];
      if (current.end < next.start) continue;
      const maximum = next.start - 1;
      const maximumText = maximum >= current.start
        ? ` Maximum allowed range: ${formatNormalizedRange(current.addressType, current.start, maximum)}.`
        : " Move one of the start addresses.";
      errors.push(`${current.signalName} ${formatNormalizedRange(current.addressType, current.start, current.end)} overlaps ${next.signalName} ${formatNormalizedRange(next.addressType, next.start, next.end)}.${maximumText}`);
    }
  });

  if (!String(update.liveLeakValueFilePath || "").trim()) {
    errors.push("Live leak value text-file location is required.");
  }
  if (!Number.isFinite(update.lowerLimit) || !Number.isFinite(update.upperLimit) || update.lowerLimit >= update.upperLimit) {
    errors.push("Leak OK minimum must be less than Leak OK maximum.");
  }
  if (!String(update.reportRootFolder || "").trim()) {
    errors.push("Report root folder is required.");
  }
  return errors;
}

function parseMappingRange(mapping, count) {
  let text = String(mapping.address || "").trim();
  if (mapping.addressType === "D Register") text = text.replace(/^D/i, "");
  if (!/^\d+$/.test(text)) return null;
  let start = Number(text);
  const referenceBase = {
    "Holding Register": 40001,
    "Input Register": 30001,
    "Discrete Input": 10001,
    "Coil": 1,
    "M Bit": 1
  }[mapping.addressType];
  if (referenceBase && start >= referenceBase) start -= referenceBase;
  if (!Number.isInteger(start) || start < 0 || start > 65535 || start + count - 1 > 65535) return null;
  return { start, end: start + count - 1 };
}

function formatNormalizedRange(addressType, start, end) {
  const prefix = addressType === "D Register" ? "D" : `${addressType} `;
  return start === end ? `${prefix}${start}` : `${prefix}${start}-${prefix}${end}`;
}

function parseValueMap(valueMap) {
  return String(valueMap || "").split(/[;,\r\n]+/).map((entry) => entry.trim()).filter(Boolean).map((entry) => {
    const separator = entry.indexOf("=");
    return separator < 1
      ? { plcValue: entry, meaning: "" }
      : { plcValue: entry.slice(0, separator).trim(), meaning: entry.slice(separator + 1).trim() };
  });
}

function renderMappingLiveValues(signals) {
  const values = new Map((signals || []).map((signal) => [String(signal.signalName || "").toLowerCase(), signal]));
  document.querySelectorAll("[data-live-raw], [data-live-decoded]").forEach((cell) => {
    const signalName = cell.dataset.liveRaw || cell.dataset.liveDecoded || "";
    const signal = values.get(String(signalName).toLowerCase());
    cell.textContent = cell.dataset.liveRaw !== undefined
      ? liveValueText(signal?.rawValue)
      : liveValueText(signal?.interpretedValue ?? signal?.rawValue);
  });
}

function liveValueText(value) {
  const text = value == null ? "" : String(value).trim();
  return text || "--";
}

function registerCountForDataType(dataType) {
  if (["Bool", "Int16", "UInt16", "Word16", "Bcd16"].includes(dataType)) return 1;
  if (["Int32", "UInt32", "DWord32", "Float32", "Bcd32"].includes(dataType)) return 2;
  if (["Int64", "UInt64", "QWord64", "Float64"].includes(dataType)) return 4;
  return null;
}

function updateShiftActionAvailability() {
  const rows = [...$("shiftRows").querySelectorAll("tr")];
  $("addShift").disabled = !state.settingsEditing;
  rows.forEach((row) => {
    const remove = row.querySelector("[data-shift-remove]");
    if (remove) remove.disabled = !state.settingsEditing || rows.length <= 1;
  });
}

function settingsCanEdit() {
  return String(state.runtimeStatus || "Stopped").toLowerCase() === "stopped";
}

function updateSettingsEditAvailability() {
  const edit = $("editSettings");
  if (!edit) return;
  if (!settingsCanEdit() && state.settingsEditing) {
    setSettingsEditing(false);
    setSettingsStatus("STOP STATION TO EDIT", "error");
    return;
  }
  edit.disabled = !settingsCanEdit();
}

async function openDrawer(part) {
  $("drawerOverlay").classList.remove("hidden");
  $("partDrawer").classList.add("open");
  $("partDrawer").setAttribute("aria-hidden", "false");
  $("drawerBody").innerHTML = `<div class="drawer-loading">Loading part details...</div>`;

  try {
    renderDrawer(await apiJson(`/api/records/${part.id}`));
  } catch (error) {
    $("drawerBody").innerHTML = `<div class="application-notice">Application API unavailable: ${escapeHtml(error.message)}</div>`;
  }
}

function renderDrawer(part) {
  const attempts = part.attempts || [];
  const isReworked = String(part.overallResult || "").toUpperCase() === "NG-REWORK" || attempts.length > 1;
  $("drawerBody").innerHTML = attempts.map((attempt, index) => {
    const heading = !isReworked ? "" : index === 0 ? "NG TEST" : index === 1 ? "REWORK TEST" : `REWORK TEST ${index}`;
    const rows = [
      ["DATE", formatDate(new Date(attempt.timestamp))],
      ["TIME", formatTime(new Date(attempt.timestamp))],
      ["SR NO.", attempt.serialNumber || "--"],
      //code change by chatgpt
      // ["QR CODE", attempt.qrCode],
      ["PART NO.", attempt.partNumber],
      // <!-- Changed manually using GPT -->
      ["LEAK VALUE", attempt.leakTestValue == null ? "--" : `${formatLeakTestValue(attempt.leakTestValue)} ${leakUnit()}`],
      // ["LEAK VALUE", attempt.leakTestValue == null ? "--" : `${formatLeakTestValue(attempt.leakTestValue)} ${attempt.leakTestUnit}`],
      ["LEAK OK RANGE", formatLeakRange(attempt.lowerLimit, attempt.upperLimit)],
      ["RESULT", resultText(attempt)],
      ["SHIFT", attempt.shift],
      ["MODE", modeText(attempt)],
      ["ERROR", machineError(attempt)]
    ];
    return `<section class="attempt-section">${heading ? `<h3>${heading}</h3>` : ""}<div class="detail-list">${rows.map(([label, value]) => detailRow(label, value)).join("")}</div></section>`;
  }).join("") || `<div class="drawer-loading">No test attempts found.</div>`;
}

function closeDrawer() {
  $("drawerOverlay").classList.add("hidden");
  $("partDrawer").classList.remove("open");
  $("partDrawer").setAttribute("aria-hidden", "true");
}

function detailRow(label, value) {
  return `<div class="detail-row"><label>${label}</label><code>${escapeHtml(String(value))}</code></div>`;
}

function setStatus(element, text, variant) {
  element.classList.toggle("bad", variant === "bad");
  element.classList.toggle("warning", variant === "warning");
  element.classList.toggle("idle", variant === "idle");
  element.innerHTML = `<span></span>${escapeHtml(text)}`;
}

function showApplicationNotice(message) {
  $("applicationNotice").textContent = message;
  $("applicationNotice").classList.remove("hidden");
}

function hideApplicationNotice() {
  $("applicationNotice").classList.add("hidden");
  $("applicationNotice").textContent = "";
}

function latestAttempt(part) {
  return part.latestAttempt || part;
}

function logicalResult(part) {
  return part.overallResult || resultText(latestAttempt(part));
}

function resultText(record) {
  return record.resolvedResult || record.result || "--";
}

function modeText(record) {
  return record.resolvedMode || (record.isAutoMode ? "AUTO" : "MANUAL");
}

function machineError(record, empty = "None") {
  const value = record.errorDescription || record.errorCode;
  return !value || String(value).toLowerCase() === "no error" ? empty : value;
}

function formatLeakTestValue(value) {
  const numeric = Number(value);
  return value != null && String(value).trim() && Number.isFinite(numeric) ? numeric.toFixed(4) : "--";
}

// <!-- Changed manually using GPT -->
function formatLeakRange(minimum, maximum) {
  const min = Number(minimum);
  const max = Number(maximum);

  return Number.isFinite(min) && Number.isFinite(max)
    ? `${min.toFixed(2)} TO ${max.toFixed(3)} ${leakUnit()}`
    : "--";
}
// function formatLeakRange(minimum, maximum) {
//   const min = Number(minimum);
//   const max = Number(maximum);
//   return Number.isFinite(min) && Number.isFinite(max) ? `${min.toFixed(2)} TO ${max.toFixed(3)}` : "--";
// }

function resultClass(result) {
  const normalized = String(result || "").trim().toUpperCase();
  if (normalized === "OK") return "ok";
  if (normalized === "NG-REWORK") return "ng-rework";
  if (normalized === "NG" || normalized.startsWith("NG ") || normalized.startsWith("NG/")) return "ng";
  if (normalized === "REWORK") return "rework";
  return "neutral";
}

function blankToNull(value) {
  const trimmed = String(value || "").trim();
  return trimmed.length === 0 ? null : trimmed;
}

function numberOrNull(value) {
  return String(value || "").trim() === "" ? null : Number(value);
}

function numberOrDefault(value, fallback) {
  return String(value || "").trim() === "" ? fallback : Number(value);
}

function normalizeTime(value) {
  return value && value.length === 5 ? `${value}:00` : value;
}

function formatTimeInput(value) {
  return String(value || "").slice(0, 5);
}

function displayDateValue(value) {
  if (!value) return "dd-mm-yyyy";
  const [year, month, day] = value.split("-");
  return `${day}-${month}-${year}`;
}

function historyExportFileName(date) {
  const year = date.getFullYear();
  const month = String(date.getMonth() + 1).padStart(2, "0");
  const day = String(date.getDate()).padStart(2, "0");
  const hour = String(date.getHours()).padStart(2, "0");
  const minute = String(date.getMinutes()).padStart(2, "0");
  return `DynaK_Part_History_${year}-${month}-${day}_${hour}${minute}.xlsx`;
}

function formatDate(date) {
  return date.toLocaleDateString("en-GB", { day: "2-digit", month: "short", year: "numeric" }).replace(/ /g, "-");
}

function formatTime(date) {
  return date.toLocaleTimeString("en-GB", { hour12: false });
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (char) => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    "\"": "&quot;",
    "'": "&#039;"
  }[char]));
}

// <!-- Changed using GPT -->
function leakUnit() {
  const configured = String(state.config?.leakTestUnit || "").trim();
  return configured || "LPM";
}

loadState();
loadSettings();
setInterval(loadState, STATE_POLL_MS);
