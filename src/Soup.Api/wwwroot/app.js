const state = {
  apiKey: localStorage.getItem("soup.apiKey") || "",
  runs: [],
  selectedRun: null,
  events: new Map(),
  cursor: 0,
  streamAbort: null
};

const $ = (selector) => document.querySelector(selector);
const keyInput = $("#api-key");
keyInput.value = state.apiKey;

function setStatus(text, kind = "neutral") {
  const element = $("#connection");
  element.textContent = text;
  element.className = `status ${kind}`;
}

function message(text, isError = false) {
  const element = $("#message");
  element.textContent = text;
  element.style.color = isError ? "#ff8888" : "var(--accent-2)";
}

async function api(path, options = {}) {
  const headers = new Headers(options.headers || {});
  if (state.apiKey) headers.set("X-Api-Key", state.apiKey);
  if (options.body) headers.set("Content-Type", "application/json");
  const response = await fetch(path, { ...options, headers });
  if (!response.ok) {
    const detail = await response.text();
    throw new Error(`${response.status} ${detail || response.statusText}`);
  }
  return response;
}

function saveKey(value) {
  state.apiKey = value.trim();
  localStorage.setItem("soup.apiKey", state.apiKey);
  keyInput.value = state.apiKey;
  message("API key saved in this browser.");
  loadRuns();
}

$("#project-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  try {
    const response = await fetch("/api/projects", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: $("#project-name").value, apiKeyName: "browser" })
    });
    if (!response.ok) throw new Error(await response.text());
    const project = await response.json();
    saveKey(project.apiKey);
    message("Project created. Copy the API key now; the server cannot reveal it again.");
  } catch (error) { message(error.message, true); }
});

$("#save-key").addEventListener("click", () => saveKey(keyInput.value));
$("#refresh-runs").addEventListener("click", loadRuns);

$("#run-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  try {
    await api("/api/runs", { method: "POST", body: JSON.stringify({ name: $("#run-name").value }) });
    $("#run-name").value = "";
    await loadRuns();
  } catch (error) { message(error.message, true); }
});

async function loadRuns() {
  if (!state.apiKey) return;
  try {
    const response = await api("/api/runs");
    state.runs = await response.json();
    renderRuns();
    setStatus("project connected", "live");
  } catch (error) {
    setStatus("authentication failed", "error");
    message(error.message, true);
  }
}

function renderRuns() {
  const list = $("#run-list");
  list.replaceChildren();
  if (!state.runs.length) {
    list.innerHTML = '<p class="muted">No runs yet.</p>';
    return;
  }
  for (const run of state.runs) {
    const button = document.createElement("button");
    button.className = `run ${state.selectedRun?.id === run.id ? "selected" : ""}`;
    button.innerHTML = `${escapeHtml(run.name)}<small>${run.status} · ${new Date(run.createdAt).toLocaleString()}</small>`;
    button.addEventListener("click", () => selectRun(run));
    list.append(button);
  }
}

async function selectRun(run) {
  state.streamAbort?.abort();
  state.selectedRun = run;
  state.events.clear();
  state.cursor = 0;
  $("#run-title").textContent = run.name;
  renderRuns();
  renderEvents();

  try {
    const response = await api(`/api/runs/${run.id}/metrics`);
    for (const metricEvent of await response.json()) addEvent(metricEvent);
    renderEvents();
    connectStream(run.id);
  } catch (error) { message(error.message, true); }
}

function addEvent(metricEvent) {
  state.events.set(metricEvent.eventId, metricEvent);
  state.cursor = Math.max(state.cursor, metricEvent.sequence);
}

async function connectStream(runId) {
  while (state.selectedRun?.id === runId) {
    const controller = new AbortController();
    state.streamAbort = controller;
    try {
      setStatus("live · connecting", "neutral");
      const response = await api(`/api/runs/${runId}/events?afterSequence=${state.cursor}`, { signal: controller.signal });
      setStatus("live · receiving", "live");
      await parseSse(response.body, (metricEvent) => {
        addEvent(metricEvent);
        renderEvents();
      }, controller.signal);
    } catch (error) {
      if (state.selectedRun?.id !== runId) return;
      setStatus("live · reconnecting", "error");
      await new Promise(resolve => setTimeout(resolve, 1000));
    }
  }
}

async function parseSse(body, onEvent, signal) {
  const reader = body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = "";
  while (!signal.aborted) {
    const { value, done } = await reader.read();
    if (done) return;
    buffer += value.replaceAll("\r\n", "\n");
    let boundary;
    while ((boundary = buffer.indexOf("\n\n")) >= 0) {
      const block = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      const data = block.split("\n").find(line => line.startsWith("data: "));
      if (data) onEvent(JSON.parse(data.slice(6)));
    }
  }
}

function renderEvents() {
  const events = [...state.events.values()].sort((a, b) => a.sequence - b.sequence);
  $("#event-count").textContent = `${events.length} event${events.length === 1 ? "" : "s"}`;
  const table = $("#event-table");
  if (!events.length) {
    table.innerHTML = '<tr><td colspan="5" class="muted">Waiting for metric events…</td></tr>';
  } else {
    table.innerHTML = events.slice(-100).reverse().map(item => `<tr data-event-id="${item.eventId}" data-sequence="${item.sequence}" data-recorded-at="${item.recordedAt}">
      <td>${item.sequence}</td><td>${escapeHtml(item.metric)}</td><td>${item.step}</td>
      <td>${item.value}</td><td>${new Date(item.recordedAt).toLocaleTimeString()}</td>
    </tr>`).join("");
  }
  drawChart(events);
}

function drawChart(events) {
  const canvas = $("#chart");
  const ratio = devicePixelRatio || 1;
  const width = canvas.clientWidth || 900;
  const height = canvas.clientHeight || 320;
  if (canvas.width !== width * ratio) canvas.width = width * ratio;
  if (canvas.height !== height * ratio) canvas.height = height * ratio;
  const context = canvas.getContext("2d");
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  context.clearRect(0, 0, width, height);
  const points = events.filter(item => item.metric === "loss").sort((a, b) => a.step - b.step);
  if (!points.length) {
    context.fillStyle = "#8f9baa";
    context.font = "14px system-ui";
    context.fillText("Loss events will appear here live", 24, 36);
    return;
  }
  const padding = 34;
  const minStep = Math.min(...points.map(item => item.step));
  const maxStep = Math.max(...points.map(item => item.step));
  const minValue = Math.min(...points.map(item => item.value));
  const maxValue = Math.max(...points.map(item => item.value));
  const x = value => padding + (value - minStep) / Math.max(1, maxStep - minStep) * (width - padding * 2);
  const y = value => height - padding - (value - minValue) / Math.max(.000001, maxValue - minValue) * (height - padding * 2);
  context.strokeStyle = "#ffca5c";
  context.lineWidth = 2.5;
  context.beginPath();
  points.forEach((point, index) => index ? context.lineTo(x(point.step), y(point.value)) : context.moveTo(x(point.step), y(point.value)));
  context.stroke();
  context.fillStyle = "#72e2ae";
  points.forEach(point => { context.beginPath(); context.arc(x(point.step), y(point.value), 3.5, 0, Math.PI * 2); context.fill(); });
}

function escapeHtml(value) {
  const node = document.createElement("span");
  node.textContent = value;
  return node.innerHTML;
}

if (state.apiKey) loadRuns();

window.addEventListener("offline", () => state.streamAbort?.abort());
