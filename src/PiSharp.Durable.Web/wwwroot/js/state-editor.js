// PiSharp Durable Web: the State tab (PLAN.md §7; after durable-pi's StateEditor, MIT, plus "Add field"). The device's App State arrives as
// `doc` frames of the device stream. The JSON editor saves the whole object with the revision it started from, so a change made
// elsewhere in the meantime is a conflict ("State changed elsewhere", Load latest). Each field control sends one atomic operation
// (create, update, delete, increment), so agents never overwrite each other's fields; the controls wait while the JSON has unsaved edits.
import { $, escapeHtml, toast, toastError } from './core.js';

const TYPES = ['string', 'number', 'boolean', 'object', 'array', 'null'];
const typeOf = (v) => v === null ? 'null' : Array.isArray(v) ? 'array' : typeof v === 'object' ? 'object' : typeof v;
const defaultFor = (type) => ({ string: '', number: 0, boolean: false, object: {}, array: [], null: null }[type]);
const pointer = (path, key) => path + '/' + String(key).replace(/~/g, '~0').replace(/\//g, '~1');

export class StateEditor {
  constructor(root, { send }) {
    this.root = root;
    this.send = send;
    this.value = null;
    this.revision = 0;
    this.base = 0;
    this.dirty = false;
    this.text = $('#stateJson', root);
    this.text.addEventListener('input', () => this.edited());
    root.addEventListener('click', (e) => this.click(e));
    root.addEventListener('change', (e) => this.change(e));
  }

  /** A doc frame. */
  update(value, revision) {
    this.value = value;
    this.revision = revision ?? 0;
    $('[data-state-revision]', this.root).textContent = `revision ${this.revision}`;
    if (!this.dirty) {
      this.base = this.revision;
      this.text.value = JSON.stringify(value, null, 2);
    }
    this.renderStatus();
    this.renderFields();
  }

  edited() {
    this.dirty = this.text.value !== JSON.stringify(this.value, null, 2);
    this.renderStatus();
    this.renderFields();
  }

  renderStatus() {
    const conflict = this.dirty && this.revision !== this.base;
    let valid = true;
    try { const parsed = JSON.parse(this.text.value); valid = parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed); } catch { valid = false; }
    $('[data-state-dirty]', this.root).hidden = !this.dirty;
    $('[data-state-conflict]', this.root).hidden = !conflict;
    $('[data-state-latest]', this.root).textContent = String(this.revision);
    $('[data-action="state-reset"]', this.root).hidden = !this.dirty || conflict;
    $('[data-action="state-load"]', this.root).hidden = !conflict;
    const save = $('[data-action="state-save"]', this.root);
    save.disabled = !this.dirty || conflict || !valid;
    const error = $('[data-state-error]', this.root);
    error.hidden = valid || !this.dirty;
    error.textContent = valid ? '' : 'The JSON must be one object.';
  }

  async click(e) {
    const action = e.target.closest('[data-action], [data-sf]');
    if (!action) return;
    const name = action.getAttribute('data-action') || action.getAttribute('data-sf');
    const path = action.closest('[data-path]')?.getAttribute('data-path') ?? '';
    try {
      if (name === 'state-reset' || name === 'state-load') {
        this.dirty = false;
        this.update(this.value, this.revision);
        if (name === 'state-load') toast(`Loaded revision ${this.revision}.`);
      } else if (name === 'state-save') {
        await this.send({ replace: JSON.parse(this.text.value), expectedRevision: this.base });
        this.dirty = false;
        toast('Shared App State saved.');
      } else if (name === 'inc' || name === 'dec') {
        await this.send({ operation: 'increment', path, value: name === 'inc' ? 1 : -1 });
      } else if (name === 'delete') {
        await this.send({ operation: 'delete', path });
      } else if (name === 'add-item') {
        await this.send({ operation: 'create', path: path + '/-', value: '' });
      } else if (name === 'add-field') {
        const row = action.closest('.sf-add');
        const key = row.querySelector('input').value.trim();
        if (!key) { toast('Name the field first.', 'error'); return; }
        await this.send({ operation: 'create', path: pointer(path, key), value: defaultFor(row.querySelector('select').value) });
      }
    } catch (error) {
      if (error.code === 'conflict') { this.base = -1; this.renderStatus(); }
      toastError(error);
    }
  }

  async change(e) {
    const control = e.target.closest('[data-sf-value], [data-sf-type]');
    if (!control) return;
    const path = control.closest('[data-path]').getAttribute('data-path');
    let value;
    if (control.hasAttribute('data-sf-type')) value = defaultFor(control.value);
    else if (control.type === 'checkbox') value = control.checked;
    else if (control.type === 'number') value = Number(control.value);
    else value = control.value;
    try { await this.send({ operation: 'update', path, value }); } catch (error) { toastError(error); }
  }

  renderFields() {
    const host = $('[data-state-fields]', this.root);
    if (!host || this.value === null) return;
    const focused = document.activeElement && host.contains(document.activeElement) ? document.activeElement : null;
    if (focused && (focused.tagName === 'TEXTAREA' || focused.tagName === 'INPUT')) return; // never yank a field being edited
    const disabled = this.dirty ? ' disabled' : '';
    const field = (key, value, path) => {
      const type = typeOf(value);
      const options = TYPES.map((t) => `<option${t === type ? ' selected' : ''}>${t}</option>`).join('');
      const head = `<div class="sf-head"><span class="sf-key">${escapeHtml(key)}</span><select class="form-select form-select-sm sf-type" aria-label="Type of ${escapeHtml(path)}" data-sf-type${disabled}>${options}</select>` +
        `<button class="btn btn-icon" type="button" title="Delete ${escapeHtml(path)}" aria-label="Delete ${escapeHtml(path)}" data-sf="delete"${disabled}><i class="bi bi-trash3"></i></button></div>`;
      let body;
      if (type === 'string') body = `<div class="sf-body"><textarea class="form-control form-control-sm" rows="1" aria-label="Value of ${escapeHtml(path)}" data-sf-value${disabled}>${escapeHtml(value)}</textarea></div>`;
      else if (type === 'number') body = `<div class="sf-body num-stepper"><button class="btn btn-sm btn-outline-secondary" type="button" title="Decrement ${escapeHtml(path)}" aria-label="Decrement ${escapeHtml(path)}" data-sf="dec"${disabled}><i class="bi bi-dash"></i></button><input class="form-control form-control-sm" type="number" value="${value}" aria-label="Value of ${escapeHtml(path)}" data-sf-value${disabled}><button class="btn btn-sm btn-outline-secondary" type="button" title="Increment ${escapeHtml(path)}" aria-label="Increment ${escapeHtml(path)}" data-sf="inc"${disabled}><i class="bi bi-plus"></i></button></div>`;
      else if (type === 'boolean') body = `<div class="sf-body form-check form-switch mb-0"><input class="form-check-input" type="checkbox" role="switch" aria-label="Value of ${escapeHtml(path)}" data-sf-value${value ? ' checked' : ''}${disabled}><label class="form-check-label fs-7">${value}</label></div>`;
      else if (type === 'null') body = `<div class="sf-body fs-7 text-ps-faint">null</div>`;
      else body = `<div class="sf-children">${children(value, path)}</div>`;
      return `<div class="sf" data-path="${escapeHtml(path)}">${head}${body}</div>`;
    };
    const children = (value, path) => {
      if (Array.isArray(value)) {
        return value.map((item, i) => field(String(i), item, pointer(path, i))).join('') +
          `<div data-path="${escapeHtml(path)}"><button class="btn btn-sm btn-outline-secondary" type="button" data-sf="add-item"${disabled}><i class="bi bi-plus-lg me-1"></i>Add item</button></div>`;
      }
      return Object.entries(value).map(([key, item]) => field(key, item, pointer(path, key))).join('') +
        `<div class="sf-add" data-path="${escapeHtml(path)}"><input class="form-control form-control-sm" style="width:8rem" placeholder="New field" aria-label="New field name"${disabled}>` +
        `<select class="form-select form-select-sm sf-type ms-0" aria-label="New field type"${disabled}>${TYPES.map((t) => `<option>${t}</option>`).join('')}</select>` +
        `<button class="btn btn-sm btn-outline-secondary" type="button" data-sf="add-field"${disabled}><i class="bi bi-plus-lg me-1"></i>Add field</button></div>`;
    };
    host.innerHTML = children(this.value, '');
  }
}
