// Decision 0011 (run W4, R1): _RunOutput's live side. streamRun follows a run's events (GET /api/runs/{id}/events, SSE: "event" frames
// {seq, atMs, event} then one "status" frame; EventSource resumes after the last id when the connection drops) and appends what streams
// to the output pane as plain text (textContent only): the answer, the thinking, an Events row per event (deltas after the first of a
// run hidden while "Collapse deltas" is on). When the status frame arrives the page swaps in the server's _RunOutput for the run, where the
// answer is rendered as Markdown by the sanitizing MarkdownRenderer. The Events filters and the collapse switch work on either.

/** The summary of one Pi wire event, as RunOutputView.Row writes it. */
export function eventSummary(event) {
  const type = event?.type ?? '';
  const index = Number.isInteger(event?.contentIndex) ? `#${event.contentIndex}` : '';
  const str = (value) => (typeof value === 'string' ? value : '');
  const quote = (text) => '"' + (text.length > 60 ? text.slice(0, 60) + '…' : text).replaceAll('\n', '\\n') + '"';
  const num = (value) => (typeof value === 'number' ? Math.round(value).toLocaleString('en-US') : '0');
  let data;
  switch (type) {
    case 'start':
      data = [str(event.partial?.provider), str(event.partial?.model), str(event.partial?.api)].filter(Boolean).join(' · ');
      break;
    case 'text_start': case 'thinking_start':
      data = index;
      break;
    case 'text_delta': case 'thinking_delta': case 'toolcall_delta':
      data = quote(str(event.delta));
      break;
    case 'text_end': case 'thinking_end':
      data = `${index} · ${str(event.content).length.toLocaleString('en-US')} chars`;
      break;
    case 'toolcall_start': case 'toolcall_provisional_start':
      data = [index, str(event.toolCall?.name), str(event.toolCall?.id)].filter(Boolean).join(' · ');
      break;
    case 'toolcall_end':
      data = `${index} · ${str(event.toolCall?.name)} ${JSON.stringify(event.toolCall?.arguments ?? null)}`;
      break;
    case 'done': case 'error': {
      const message = event.message ?? {};
      const parts = [str(message.stopReason) || str(event.reason)];
      if (message.usage) parts.push(`${num(message.usage.input)} in · ${num(message.usage.output)} out · ${num(message.usage.cacheRead)} cache read`);
      if (str(message.errorMessage)) parts.push(message.errorMessage);
      data = parts.filter(Boolean).join(' · ');
      break;
    }
    default:
      data = index;
  }
  return data.length > 300 ? data.slice(0, 300) + '…' : data;
}

function group(type) {
  return type.startsWith('thinking') ? 'thinking' : type.startsWith('text') ? 'text' : type.startsWith('toolcall') ? 'toolcall' : 'other';
}

function span(className, text) {
  const element = document.createElement('span');
  element.className = className;
  element.textContent = text;
  return element;
}

/** Applies the Events tab's filter and collapse state to its rows. */
export function applyEventView(section) {
  const rows = section.querySelector('[data-event-rows]');
  if (!rows) return;
  const filter = section.querySelector('[data-event-filter][aria-pressed="true"]')?.dataset.eventFilter ?? 'all';
  rows.dataset.filter = filter;
  rows.dataset.collapsed = section.querySelector('[data-events-collapse]')?.checked === false ? 'false' : 'true';
}

/** Wires the Events tab's filters and collapse switch (delegated on `host`, so a swapped-in pane keeps working). */
export function initEventControls(host) {
  host.addEventListener('click', (event) => {
    const chip = event.target.closest('[data-event-filter]');
    if (!chip) return;
    const section = chip.closest('[data-run-output]');
    section.querySelectorAll('[data-event-filter]').forEach((other) => {
      const on = other === chip;
      other.setAttribute('aria-pressed', on ? 'true' : 'false');
      if (on) other.dataset.tone = 'accent';
      else delete other.dataset.tone;
    });
    applyEventView(section);
  });
  host.addEventListener('change', (event) => {
    if (event.target.matches('[data-events-collapse]')) applyEventView(event.target.closest('[data-run-output]'));
  });
}

/** Follows run `id`'s events into `section` (its _RunOutput in the streaming state). Calls onEnd(status) once, with the status frame. */
export function streamRun(section, id, { after = 0, onEnd, onEvent } = {}) {
  const rows = section.querySelector('[data-event-rows]');
  const text = section.querySelector('[data-stream-text]');
  const plain = section.querySelector('[data-output-plain]');
  const thinking = section.querySelector('[data-stream-thinking]');
  const thinkingBox = section.querySelector('[data-output-thinking]');
  const count = section.querySelector('[data-event-count]');
  const status = section.querySelector('[data-stream-status]');
  let events = Number(count?.textContent.replace(/\D/g, '') || 0);
  let lastType = rows?.lastElementChild?.querySelector('.ev')?.textContent ?? '';
  let run = 0;
  let ended = false;
  const started = performance.now();
  const timer = window.setInterval(() => {
    if (status) status.textContent = `streaming · ${((performance.now() - started) / 1000).toFixed(1)} s`;
  }, 200);

  const addRow = (item) => {
    if (!rows) return;
    const type = item.event?.type ?? '';
    const delta = type.endsWith('_delta');
    run = delta && type === lastType ? run + 1 : 0;
    const row = document.createElement('div');
    row.className = 'sse-row';
    row.dataset.group = group(type);
    if (run > 0) row.dataset.extra = 'true';
    const tone = type === 'done' ? ' text-ps-ok' : type === 'error' ? ' text-ps-danger' : '';
    row.append(span('t', `+${Math.round(item.atMs ?? 0).toLocaleString('en-US')} ms`), span('ev' + tone, type), span('data', eventSummary(item.event)));
    rows.append(row);
    lastType = type;
  };

  const source = new EventSource(`/api/runs/${encodeURIComponent(id)}/events?after=${after}`);
  source.addEventListener('event', (message) => {
    let item;
    try { item = JSON.parse(message.data); } catch { return; }
    const event = item.event ?? {};
    if (event.type === 'text_delta' && typeof event.delta === 'string') {
      if (text) text.append(event.delta);
      if (plain) plain.append(event.delta);
    } else if (event.type === 'thinking_delta' && typeof event.delta === 'string' && thinking) {
      thinking.append(event.delta);
      if (thinkingBox) thinkingBox.hidden = false;
    }
    addRow(item);
    events++;
    if (count) count.textContent = `${events.toLocaleString('en-US')}…`;
    section.dataset.lastSeq = String(item.seq ?? section.dataset.lastSeq);
    onEvent?.(item);
  });
  source.addEventListener('status', (message) => {
    if (ended) return;
    ended = true;
    source.close();
    window.clearInterval(timer);
    let final = null;
    try { final = JSON.parse(message.data); } catch { final = null; }
    onEnd?.(final);
  });
  return {
    close() {
      ended = true;
      source.close();
      window.clearInterval(timer);
    },
  };
}
