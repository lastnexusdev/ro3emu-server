'use strict';

registerAdminPage({ id: 'config', label: 'Server configs', icon: 'grid', path: '/admin/config', pattern: /^\/admin\/config$/, page: adminConfigPage, role: 'admin' });

function configLabel(path) {
  return path.split('.').map(part => part.replace(/([a-z0-9])([A-Z])/g, '$1 $2')).join(' / ');
}

function fieldExpEditor(value, changed) {
  let rows = structuredClone(value || []);
  const list = h('div');
  const draw = () => list.replaceChildren(...rows.map((row, i) => h('div', { class: 'config-rate-row' },
    ...[['MinLevel', 'From level'], ['MaxLevel', 'To level'], ['Percent', 'EXP %']].map(([key, label]) =>
      h('label', { class: 'field' }, h('span', { class: 'label' }, label), h('input', { class: 'input', type: 'number', min: '1', step: '1',
        value: String(row[key]), oninput: e => { row[key] = Number(e.target.value); changed(rows); } }))),
    h('button', { type: 'button', class: 'btn secondary sm', onclick: () => { rows.splice(i, 1); draw(); changed(rows); } }, 'Remove'))));
  draw();
  return h('div', null, list, h('button', { type: 'button', class: 'btn secondary sm', onclick: () => {
    rows.push({ MinLevel: 1, MaxLevel: 99, Percent: 100 }); draw(); changed(rows);
  } }, 'Add level range'));
}

function configInput(setting, changed) {
  const value = setting.saved;
  if (setting.path === 'FieldExpRates') return fieldExpEditor(value, changed);
  if (setting.type === 'boolean') return h('select', { class: 'input', 'aria-label': configLabel(setting.path), onchange: e =>
    changed(e.target.value === 'null' ? null : e.target.value === 'true') },
    ...[['true', 'Enabled'], ['false', 'Disabled'], ...(setting.nullable ? [['null', 'Automatic / default']] : [])].map(([key, label]) =>
      h('option', { value: key, selected: String(value) === key }, label)));
  if (setting.type === 'json') return h('textarea', { class: 'input mono', rows: '5', 'aria-label': configLabel(setting.path),
    oninput: e => changed(e.target.value, true) }, JSON.stringify(value, null, 2));
  return h('input', { class: 'input', type: setting.secret ? 'password' : setting.type === 'number' ? 'number' : 'text',
    step: setting.type === 'number' ? 'any' : null, autocomplete: setting.secret ? 'new-password' : 'off',
    'aria-label': configLabel(setting.path), value: value == null ? '' : String(value),
    placeholder: setting.secret ? 'Unchanged (hidden)' : value == null ? 'Default / automatic' : '',
    oninput: e => changed(setting.type === 'number' ? (e.target.value.trim() === '' ? null : Number(e.target.value)) : e.target.value) });
}

async function adminConfigPage() {
  requireRole('admin');
  let config = await api('GET', '/api/admin/server/config');
  const dirty = new Map();
  const errors = new Map();
  const rows = [];
  const list = h('div', { class: 'config-list' });
  const status = h('p', { class: 'hint', role: 'status', 'aria-live': 'polite' });
  const save = h('button', { type: 'button', class: 'btn primary', disabled: true }, 'Save and apply');
  const discard = h('button', { type: 'button', class: 'btn secondary', disabled: true }, 'Discard edits');
  const search = h('input', { class: 'input', type: 'search', placeholder: 'Search settings, e.g. EXP, port, backup', 'aria-label': 'Search server settings' });
  const mode = h('select', { class: 'input', 'aria-label': 'Filter settings' },
    h('option', { value: 'all' }, 'All settings'), h('option', { value: 'live' }, 'Can apply live'), h('option', { value: 'restart' }, 'Require restart'));
  const updateStatus = () => {
    save.disabled = dirty.size === 0 || errors.size > 0;
    discard.disabled = dirty.size === 0 && errors.size === 0;
    status.textContent = errors.size ? [...errors.values()].join(' ') : dirty.size ? `${dirty.size} unsaved change(s).` :
      `${config.settings.length} settings. ${config.settings.filter(s => s.pendingRestart).length} saved change(s) waiting for restart.`;
  };
  const filter = () => {
    const query = search.value.toLowerCase();
    let count = 0;
    for (const { setting, node } of rows) {
      const visible = `${setting.path} ${setting.description} ${configLabel(setting.path)}`.toLowerCase().includes(query)
        && (mode.value === 'all' || (mode.value === 'live') === setting.live);
      node.hidden = !visible;
      if (visible) count++;
    }
    list.setAttribute('aria-label', `${count} matching settings`);
  };
  const render = () => {
    rows.length = 0;
    list.replaceChildren(...config.settings.map(setting => {
      const feedback = h('span', { class: 'hint', role: 'status' });
      const changed = (value, parse = false) => {
        try {
          if (setting.secret && value === '') { dirty.delete(setting.path); errors.delete(setting.path); feedback.textContent = ''; updateStatus(); return; }
          if (parse) value = JSON.parse(value);
          if (typeof value === 'number' && !Number.isFinite(value)) throw new Error('Enter a finite number.');
          errors.delete(setting.path);
          if (!setting.secret && JSON.stringify(value) === JSON.stringify(setting.saved)) dirty.delete(setting.path);
          else dirty.set(setting.path, value);
          feedback.textContent = dirty.has(setting.path) ? 'Unsaved change' : '';
        } catch (e) {
          errors.set(setting.path, `${setting.path}: ${e.message}`);
          feedback.textContent = 'Invalid JSON or value';
        }
        updateStatus();
      };
      const input = configInput(setting, changed);
      const active = setting.secret ? 'Hidden' : setting.active == null ? 'Automatic / default' :
        typeof setting.active === 'object' ? JSON.stringify(setting.active) : String(setting.active);
      const node = h('section', { class: 'config-setting' },
        h('div', { class: 'row' }, h('h3', null, configLabel(setting.path)),
          chip(setting.live ? 'active' : 'event', setting.live ? 'Applies live' : 'Restart required'),
          setting.pendingRestart && chip('banned', 'Restart pending')),
        mono(setting.path), h('p', { class: 'hint' }, setting.description), input,
        h('p', { class: 'hint config-current' }, 'Active: ', h('span', { class: 'mono break' }, active)), feedback);
      rows.push({ setting, node });
      return node;
    }));
    filter(); updateStatus();
  };
  save.addEventListener('click', async () => {
    list.inert = true;
    discard.disabled = true;
    await busy(save, async () => {
    try {
      const result = await api('POST', '/api/admin/server/config', { revision: config.revision, changes: Object.fromEntries(dirty) });
      config = result.configuration; dirty.clear(); errors.clear(); render(); toast(result.message);
    } catch (e) { toast(e.message); }
    });
    list.inert = false;
    updateStatus();
  });
  discard.addEventListener('click', () => { dirty.clear(); errors.clear(); render(); });
  search.addEventListener('input', filter); mode.addEventListener('change', filter);
  render();
  return adminLayout('config',
    pageHead({ crumbs: adminCrumbs({ label: 'Server configs' }), title: 'Server configs', subtitle: 'Edit server settings. Live settings take effect after saving; other settings wait for a restart.' }),
    h('div', { class: 'notice' }, icon('clock'), h('div', null, 'Saving never restarts the server. EXP changes affect future rewards. Collections use JSON; field EXP ranges have their own editor.')),
    card('Configuration', null, body(h('div', { class: 'config-toolbar' }, search, mode),
      h('div', { class: 'row' }, save, discard), status)), list);
}
