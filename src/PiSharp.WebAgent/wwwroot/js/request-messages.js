// Decision 0011 (run W4, R1): _MessageListEditor's behaviour: add (by role, after the focused editor or at the end), duplicate, delete,
// reorder (drag the grip, or Alt+↑ / Alt+↓), change a role (each editor keeps every part and shows the ones its role uses), tool calls on an
// assistant message, the call a tool result answers (with its tool name), images on a user message (PNG, JPEG, GIF or WebP, resized in the
// page to 2,000 px), and "Paste messages JSON". Text is only ever set as a value or textContent. The first editor is the system prompt and
// stays first. Every change dispatches 'request-change' so the page can refresh its estimate.

const MAX_SIDE = 2000;
const IMAGE_TYPES = ['image/png', 'image/jpeg', 'image/gif', 'image/webp'];
const LABELS = { system: 'System message', user: 'User message', assistant: 'Assistant message', toolResult: 'Tool result content' };
let counter = 0;

function changed(list) {
  list.dispatchEvent(new CustomEvent('request-change', { bubbles: true }));
}

function editors(list) {
  return [...list.querySelectorAll('[data-msg-list] > [data-msg]')];
}

function prefix(list) {
  return list.dataset.idPrefix || 'pg';
}

/** A tool call id in the providers' style: call_ and 16 random letters and digits. */
export function newCallId() {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  const bytes = new Uint8Array(16);
  crypto.getRandomValues(bytes);
  return 'call_' + [...bytes].map((byte) => alphabet[byte % alphabet.length]).join('');
}

// Element ids (and the label/for pairs) of a cloned editor are made unique with a fresh key.
function rekey(editor, list) {
  const key = 'n' + (++counter) + Date.now().toString(36);
  const old = editor.dataset.key;
  const p = prefix(list);
  editor.dataset.key = key;
  editor.querySelectorAll('[id]').forEach((element) => { element.id = element.id.replace(`${p}-${old}-`, `${p}-${key}-`); });
  editor.querySelectorAll('label[for]').forEach((label) => { label.htmlFor = label.htmlFor.replace(`${p}-${old}-`, `${p}-${key}-`); });
}

function renumber(list) {
  const all = editors(list);
  all.forEach((editor, index) => {
    const number = index + 1;
    editor.querySelector('[data-msg-drag]')?.setAttribute('aria-label', `Move message ${number}`);
    const roleLabel = editor.querySelector('label[for$="-role"]');
    if (roleLabel) roleLabel.textContent = `Message ${number} role`;
    if (editor.dataset.msgPrompt !== 'true') {
      editor.querySelector('[data-msg-duplicate]')?.setAttribute('aria-label', `Duplicate message ${number}`);
      editor.querySelector('[data-msg-delete]')?.setAttribute('aria-label', `Delete message ${number}`);
      editor.querySelector('[data-msg-attach]')?.setAttribute('aria-label', `Attach image to message ${number}`);
    }
  });
  const count = list.querySelector('[data-msg-count]');
  if (count) count.textContent = String(all.length);
}

/** Shows the parts `role` uses in `editor` and sets its class, label and data-role. */
export function setRole(editor, role) {
  editor.dataset.role = role;
  editor.className = editor.className.replace(/\brole-\S+/g, '').trim() + ' role-' + (role === 'toolResult' ? 'tool' : role);
  editor.querySelectorAll('[data-for-role]').forEach((part) => {
    const shown = part.dataset.forRole === role && !(part.matches('[data-msg-images]') && part.querySelectorAll('[data-image]').length === 0);
    part.hidden = !shown;
  });
  const label = editor.querySelector('[data-msg-label]');
  if (label) label.textContent = LABELS[role] ?? 'Message';
  editor.querySelector('[data-msg-text]')?.classList.toggle('font-monospace', role === 'toolResult');
  const select = editor.querySelector('[data-msg-role]');
  if (select && select.value !== role) select.value = role;
  if (role === 'toolResult') refreshCalls(editor);
}

/** The tool calls of the assistant editors before `editor`: [{ id, name }]. */
export function callsBefore(editor) {
  const calls = [];
  for (let previous = editor.previousElementSibling; previous; previous = previous.previousElementSibling) {
    if (!previous.matches('[data-msg]') || previous.dataset.role !== 'assistant') continue;
    const found = [...previous.querySelectorAll('[data-msg-calls] [data-tool-call]')].map((call) => ({
      id: call.querySelector('[data-call-id]').textContent.trim(),
      name: call.querySelector('[data-call-name]').value.trim(),
    }));
    calls.unshift(...found);
  }
  return calls;
}

/** Refreshes a tool result's call list from the assistant messages before it, keeping its choice. */
export function refreshCalls(editor) {
  const select = editor.querySelector('[data-result-call]');
  if (!select) return;
  const current = select.value;
  const currentName = select.selectedOptions[0]?.dataset.toolName ?? '';
  const calls = callsBefore(editor);
  select.replaceChildren();
  if (calls.length === 0 && !current) select.append(new Option('no tool call yet', ''));
  for (const call of calls) {
    const option = new Option(call.id, call.id);
    option.dataset.toolName = call.name;
    select.append(option);
  }
  if (current && !calls.some((call) => call.id === current)) {
    const option = new Option(current, current);
    option.dataset.toolName = currentName;
    select.append(option);
  }
  select.value = current || (calls.at(-1)?.id ?? '');
  syncResultName(editor);
}

function syncResultName(editor) {
  const select = editor.querySelector('[data-result-call]');
  const name = editor.querySelector('[data-result-name]');
  const chosen = select?.selectedOptions[0];
  if (name && chosen && chosen.value) name.value = chosen.dataset.toolName ?? name.value;
}

function newEditor(list, role) {
  const template = list.querySelector('[data-msg-template]');
  const editor = template.content.firstElementChild.cloneNode(true);
  rekey(editor, list);
  setRole(editor, role);
  return editor;
}

/** Adds a tool call to an assistant editor (name and arguments optional); returns its panel. */
export function addCall(editor, name = '', args = {}) {
  const list = editor.closest('[data-message-list]');
  const panel = list.querySelector('[data-call-template]').content.firstElementChild.cloneNode(true);
  panel.querySelector('[data-call-id]').textContent = newCallId();
  panel.querySelector('[data-call-name]').value = name;
  panel.querySelector('[data-call-args]').value = JSON.stringify(args ?? {});
  editor.querySelector('[data-msg-calls]').append(panel);
  return panel;
}

/** Adds an editor with `role` ('assistant-call' adds an assistant message with an empty tool call) after `after`, or at the end. */
export function addMessage(list, role, { after = null, text = '', focus = true } = {}) {
  const container = list.querySelector('[data-msg-list]');
  const kind = role === 'assistant-call' ? 'assistant' : role;
  const editor = newEditor(list, kind);
  editor.querySelector('[data-msg-text]').value = text;
  if (after && after.parentElement === container) after.after(editor);
  else container.append(editor);
  if (role === 'assistant-call') addCall(editor);
  if (kind === 'toolResult') refreshCalls(editor);
  renumber(list);
  if (focus) editor.querySelector(role === 'assistant-call' ? '[data-call-name]' : '[data-msg-text]')?.focus();
  changed(list);
  return editor;
}

/** Replaces the messages (and the system prompt) with `systemPrompt` and RunMessages `messages`. */
export function setMessages(list, systemPrompt, messages) {
  const container = list.querySelector('[data-msg-list]');
  editors(list).forEach((editor) => { if (editor.dataset.msgPrompt !== 'true') editor.remove(); });
  const prompt = container.querySelector('[data-msg-prompt="true"] [data-msg-text]');
  if (prompt) prompt.value = systemPrompt ?? '';
  for (const message of messages ?? []) appendMessage(list, message);
  renumber(list);
  changed(list);
}

/** Appends one RunMessage ({ role, text, images, toolCalls, toolCallId, toolName, isError }) as an editor; returns it. */
export function appendMessage(list, message) {
  const container = list.querySelector('[data-msg-list]');
  const role = ['system', 'user', 'assistant', 'toolResult'].includes(message?.role) ? message.role : 'user';
  const editor = newEditor(list, role);
  editor.querySelector('[data-msg-text]').value = message?.text ?? '';
  container.append(editor);
  if (role === 'assistant') {
    for (const call of message.toolCalls ?? []) {
      const panel = addCall(editor, call.name ?? '', call.arguments ?? {});
      if (call.id) panel.querySelector('[data-call-id]').textContent = call.id;
    }
  } else if (role === 'user') {
    for (const image of message.images ?? []) addImageChip(editor, image.mimeType, image.data, 'image');
  } else if (role === 'toolResult') {
    const select = editor.querySelector('[data-result-call]');
    select.replaceChildren();
    const option = new Option(message.toolCallId ?? '', message.toolCallId ?? '');
    option.dataset.toolName = message.toolName ?? '';
    select.append(option);
    refreshCalls(editor);
    editor.querySelector('[data-result-name]').value = message.toolName ?? '';
    editor.querySelector('[data-result-error]').checked = message.isError === true;
  }
  setRole(editor, role);
  renumber(list);
  return editor;
}

function addImageChip(editor, mimeType, data, name, note) {
  const images = editor.querySelector('[data-msg-images]');
  const chip = document.createElement('span');
  chip.className = 'ps-chip';
  chip.dataset.tone = 'info';
  chip.dataset.image = '';
  chip.dataset.imageMime = mimeType;
  chip.dataset.imageData = data;
  chip.dataset.imageName = name;
  const icon = document.createElement('i');
  icon.className = 'bi bi-image';
  const label = document.createElement('span');
  label.dataset.imageLabel = '';
  const kb = Math.round((data.length * 3) / 4 / 1024);
  label.textContent = `${name} · ${kb.toLocaleString()} KB${note ? ' · ' + note : ''}`;
  const remove = document.createElement('button');
  remove.className = 'btn btn-icon p-0 ms-1';
  remove.type = 'button';
  remove.title = 'Remove image';
  remove.setAttribute('aria-label', `Remove ${name}`);
  remove.dataset.imageRemove = '';
  const x = document.createElement('i');
  x.className = 'bi bi-x fs-8';
  remove.append(x);
  chip.append(icon, label, remove);
  images.append(chip);
  images.hidden = editor.dataset.role !== 'user';
}

function readAsDataUrl(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result));
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

function loadImage(url) {
  return new Promise((resolve, reject) => {
    const image = new Image();
    image.onload = () => resolve(image);
    image.onerror = () => reject(new Error('The image could not be read.'));
    image.src = url;
  });
}

/** Reads an image file, resized so its longer side is at most 2,000 px: { mimeType, data (base64), note }. */
export async function readImage(file) {
  if (!IMAGE_TYPES.includes(file.type)) throw new Error(`${file.name}: PNG, JPEG, GIF or WebP only.`);
  const url = await readAsDataUrl(file);
  const image = await loadImage(url);
  const side = Math.max(image.naturalWidth, image.naturalHeight);
  if (side <= MAX_SIDE) return { mimeType: file.type, data: url.slice(url.indexOf(',') + 1), note: `${image.naturalWidth}×${image.naturalHeight}` };
  const scale = MAX_SIDE / side;
  const canvas = document.createElement('canvas');
  canvas.width = Math.round(image.naturalWidth * scale);
  canvas.height = Math.round(image.naturalHeight * scale);
  canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);
  const type = file.type === 'image/jpeg' ? 'image/jpeg' : 'image/png';
  const resized = canvas.toDataURL(type, 0.9);
  return { mimeType: type, data: resized.slice(resized.indexOf(',') + 1), note: `resized to ${canvas.width}×${canvas.height}` };
}

async function attachFiles(list, editor, files) {
  for (const file of files) {
    try {
      const image = await readImage(file);
      addImageChip(editor, image.mimeType, image.data, file.name, image.note);
    } catch (error) {
      list.dispatchEvent(new CustomEvent('request-error', { bubbles: true, detail: { title: 'Image not attached', message: error.message } }));
    }
  }
  changed(list);
}

/** One message editor ([data-msg]) as a RunMessage, or null when it is empty. */
export function editorMessage(editor) {
  const role = editor.dataset.role;
  const text = editor.querySelector('[data-msg-text]').value;
  const message = { role, text };
  if (role === 'user') {
    const images = [...editor.querySelectorAll('[data-msg-images] [data-image]')].map((chip) => ({ mimeType: chip.dataset.imageMime, data: chip.dataset.imageData }));
    if (images.length > 0) message.images = images;
  } else if (role === 'assistant') {
    const calls = [...editor.querySelectorAll('[data-msg-calls] [data-tool-call]')].map((call) => {
      const argsText = call.querySelector('[data-call-args]').value.trim();
      let args = {};
      try { args = argsText ? JSON.parse(argsText) : {}; } catch { throw new Error(`Tool call ${call.querySelector('[data-call-id]').textContent}: the arguments are not valid JSON.`); }
      return { id: call.querySelector('[data-call-id]').textContent.trim(), name: call.querySelector('[data-call-name]').value.trim(), arguments: args };
    });
    if (calls.length > 0) message.toolCalls = calls;
  } else if (role === 'toolResult') {
    const select = editor.querySelector('[data-result-call]');
    message.toolCallId = select.value || null;
    message.toolName = editor.querySelector('[data-result-name]').value.trim() || null;
    if (editor.querySelector('[data-result-error]').checked) message.isError = true;
    return message;
  }
  if (text.trim() === '' && !message.images && !message.toolCalls) return null;
  return message;
}

/** Sets each editor's token label from the estimate's parts ({ system, messages: [per sent message] }), in the sent messages' order. */
export function setTokens(list, parts, total, contextWindow) {
  const format = (value) => (value === null || value === undefined ? '—' : Math.round(value).toLocaleString());
  let index = 0;
  editors(list).forEach((editor) => {
    const label = editor.querySelector('[data-msg-tokens]');
    if (editor.dataset.msgPrompt === 'true') {
      if (label) label.textContent = `${format(parts?.system)} tok`;
      return;
    }
    let empty;
    try { empty = editorMessage(editor) === null; } catch { empty = false; }
    if (label) label.textContent = empty ? 'empty · not sent' : `${format(parts?.messages?.[index])} tok`;
    if (!empty) index++;
  });
  const totalElement = list.querySelector('[data-tokens-total]');
  if (totalElement) totalElement.textContent = total === null || total === undefined ? '≈ — input tokens' : `≈ ${format(total)} input tokens`;
  const partsElement = list.querySelector('[data-tokens-parts]');
  if (partsElement) {
    const sum = (parts?.messages ?? []).reduce((a, b) => a + b, 0);
    partsElement.textContent = parts ? `system ${format(parts.system)} · messages ${format(sum)} · tools ${format(parts.tools)}` : '';
  }
  const share = list.querySelector('[data-tokens-share]');
  const window = Number(contextWindow ?? list.dataset.contextWindow);
  if (share) {
    share.hidden = !(window > 0 && total);
    if (window > 0 && total) share.textContent = `${((total / window) * 100).toFixed(1)}% of ${window >= 1000 ? Math.round(window / 1000).toLocaleString() + 'K' : window}`;
  }
}

/** Sets whether the model takes images (Attach image). */
export function setImagesAllowed(list, allowed) {
  list.dataset.images = allowed ? 'true' : 'false';
  list.querySelectorAll('[data-msg-attach]').forEach((button) => { button.disabled = !allowed; });
  const last = list.querySelector('[data-msg-attach-last]');
  if (last) {
    last.disabled = !allowed;
    last.title = allowed ? 'Attach an image to the last user message' : 'This model takes text only';
  }
  const note = list.querySelector('[data-images-note]');
  if (note) note.textContent = allowed ? 'PNG, JPEG, GIF or WebP · resized to 2,000 px' : 'This model takes text only';
}

/** Parses pasted JSON: an array of messages, or { systemPrompt, messages }. Accepts `content` for `text`. Throws an Error. */
export function parseImport(text) {
  let value;
  try { value = JSON.parse(text); } catch { throw new Error('That is not valid JSON.'); }
  const messages = Array.isArray(value) ? value : Array.isArray(value?.messages) ? value.messages : null;
  if (!messages) throw new Error('Paste an array of messages, or an object with a messages array.');
  let systemPrompt = Array.isArray(value) ? null : (typeof value.systemPrompt === 'string' ? value.systemPrompt : null);
  const result = [];
  for (const raw of messages) {
    if (!raw || typeof raw !== 'object') throw new Error('Each message is an object with a role.');
    const role = raw.role === 'tool' ? 'toolResult' : raw.role;
    if (!['system', 'user', 'assistant', 'toolResult'].includes(role)) throw new Error(`"${raw.role}" is not a role (system, user, assistant, toolResult).`);
    const text = typeof raw.text === 'string' ? raw.text : typeof raw.content === 'string' ? raw.content
      : Array.isArray(raw.content) ? raw.content.filter((part) => part?.type === 'text').map((part) => part.text ?? '').join('') : '';
    if (role === 'system' && systemPrompt === null && result.length === 0) {
      systemPrompt = text;
      continue;
    }
    result.push({
      role,
      text,
      toolCalls: Array.isArray(raw.toolCalls) ? raw.toolCalls.map((call) => ({ id: call.id, name: call.name, arguments: call.arguments ?? {} })) : undefined,
      toolCallId: raw.toolCallId ?? raw.tool_call_id,
      toolName: raw.toolName ?? raw.name,
      isError: raw.isError === true,
    });
  }
  return { systemPrompt, messages: result };
}

function move(list, editor, delta) {
  const all = editors(list).filter((item) => item.dataset.msgPrompt !== 'true');
  const index = all.indexOf(editor);
  const target = all[index + delta];
  if (!target) return;
  if (delta < 0) target.before(editor);
  else target.after(editor);
  renumber(list);
  editor.querySelector('[data-msg-text]')?.focus();
  changed(list);
}

/** Wires one message list. */
export function initMessages(list) {
  if (!list || list.dataset.wired === 'true') return;
  list.dataset.wired = 'true';
  const fileInput = list.querySelector('[data-image-input]');
  let attachTarget = null;
  let dragged = null;

  list.addEventListener('click', (event) => {
    const target = event.target.closest('button');
    if (!target || target.disabled) return;
    const editor = target.closest('[data-msg]');
    if (target.matches('[data-msg-add]')) {
      const kind = target.dataset.msgAdd;
      const focused = document.activeElement?.closest?.('[data-msg-list] > [data-msg]');
      const last = editors(list).at(-1);
      const role = kind === 'next' ? (last && last.dataset.role === 'user' ? 'assistant' : 'user') : kind;
      addMessage(list, role, { after: focused ?? null });
    } else if (target.matches('[data-msg-duplicate]') && editor) {
      const copy = editor.cloneNode(true);
      rekey(copy, list);
      copy.querySelectorAll('[data-call-id]').forEach((id) => { id.textContent = newCallId(); });
      copy.querySelector('[data-msg-text]').value = editor.querySelector('[data-msg-text]').value;
      copy.querySelector('[data-result-call]').value = editor.querySelector('[data-result-call]').value;
      editor.after(copy);
      renumber(list);
      changed(list);
    } else if (target.matches('[data-msg-delete]') && editor) {
      if (editor.dataset.msgPrompt === 'true') editor.querySelector('[data-msg-text]').value = '';
      else {
        const next = editor.nextElementSibling ?? editor.previousElementSibling;
        editor.remove();
        next?.querySelector('[data-msg-text]')?.focus();
      }
      renumber(list);
      changed(list);
    } else if (target.matches('[data-msg-attach]') && editor) {
      attachTarget = editor;
      fileInput.click();
    } else if (target.matches('[data-msg-attach-last]')) {
      attachTarget = editors(list).filter((item) => item.dataset.role === 'user').at(-1) ?? addMessage(list, 'user', { focus: false });
      fileInput.click();
    } else if (target.matches('[data-image-remove]')) {
      const images = target.closest('[data-msg-images]');
      target.closest('[data-image]').remove();
      if (images && images.querySelectorAll('[data-image]').length === 0) images.hidden = true;
      changed(list);
    } else if (target.matches('[data-call-add]') && editor) {
      addCall(editor).querySelector('[data-call-name]').focus();
      changed(list);
    } else if (target.matches('[data-call-remove]')) {
      target.closest('[data-tool-call]').remove();
      changed(list);
    } else if (target.matches('[data-import-toggle]')) {
      const panel = list.querySelector('[data-import-panel]');
      panel.hidden = !panel.hidden;
      list.querySelector('[data-import-error]').textContent = '';
      if (!panel.hidden) panel.querySelector('[data-import-json]').focus();
    } else if (target.matches('[data-import-apply]')) {
      const panel = list.querySelector('[data-import-panel]');
      try {
        const parsed = parseImport(panel.querySelector('[data-import-json]').value);
        setMessages(list, parsed.systemPrompt ?? list.querySelector('[data-msg-prompt="true"] [data-msg-text]')?.value ?? '', parsed.messages);
        panel.hidden = true;
        panel.querySelector('[data-import-json]').value = '';
      } catch (error) {
        list.querySelector('[data-import-error]').textContent = error.message;
      }
    }
  });

  list.addEventListener('change', (event) => {
    const target = event.target;
    const editor = target.closest('[data-msg]');
    if (target.matches('[data-msg-role]') && editor) setRole(editor, target.value);
    else if (target.matches('[data-result-call]') && editor) syncResultName(editor);
    else if (target.matches('[data-image-input]')) {
      const files = [...target.files];
      target.value = '';
      if (attachTarget && files.length > 0) attachFiles(list, attachTarget, files);
      return;
    }
    changed(list);
  });

  list.addEventListener('input', (event) => {
    if (event.target.matches('[data-call-name]')) {
      list.querySelectorAll('[data-msg][data-role="toolResult"]').forEach(refreshCalls);
    }
    if (!event.target.matches('[data-import-json]')) changed(list);
  });

  list.addEventListener('focusin', (event) => {
    if (event.target.matches('[data-result-call]')) refreshCalls(event.target.closest('[data-msg]'));
  });

  list.addEventListener('keydown', (event) => {
    if (!event.altKey || (event.key !== 'ArrowUp' && event.key !== 'ArrowDown')) return;
    const editor = event.target.closest?.('[data-msg-list] > [data-msg]');
    if (!editor || editor.dataset.msgPrompt === 'true') return;
    event.preventDefault();
    move(list, editor, event.key === 'ArrowUp' ? -1 : 1);
  });

  list.addEventListener('dragstart', (event) => {
    const handle = event.target.closest?.('[data-msg-drag]');
    if (!handle) return;
    dragged = handle.closest('[data-msg]');
    dragged.classList.add('is-dragging');
    event.dataTransfer.effectAllowed = 'move';
    event.dataTransfer.setData('text/plain', dragged.dataset.key);
  });
  list.addEventListener('dragover', (event) => {
    if (!dragged) return;
    const over = event.target.closest?.('[data-msg-list] > [data-msg]');
    if (!over || over === dragged || over.dataset.msgPrompt === 'true') return;
    event.preventDefault();
    const box = over.getBoundingClientRect();
    if (event.clientY < box.top + box.height / 2) over.before(dragged);
    else over.after(dragged);
  });
  list.addEventListener('dragend', () => {
    if (!dragged) return;
    dragged.classList.remove('is-dragging');
    dragged = null;
    renumber(list);
    changed(list);
  });

  editors(list).forEach((editor) => setRole(editor, editor.dataset.role));
  renumber(list);
}
