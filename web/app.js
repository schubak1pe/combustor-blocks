/* Combustor Blocks — editor web
 * Traduce bloques al lenguaje real de Combustor (GS) y los ejecuta a través del mod.
 */
'use strict';

const C = {
  events: '#e6b23a',
  control: '#f08a3c',
  logic: '#4fb96a',
  compare: '#2fa8c7',
  math: '#6bc24a',
  variables: '#e2683c',
  lists: '#d9534f',
  functions: '#c05cd9',
  data: '#7c6cf0'
};

const RESERVED = new Set([
  'if','elif','else','while','for','in','def','return','break','continue','pass',
  'and','or','not','True','False','None','print','wait','stop','time','range','len',
  'abs','floor','ceil','round','clamp','min','max','int','float','str','sqrt','sin',
  'cos','pi','ship','shop'
]);

const FALLBACK_MEMBERS = [
  { name: 'on', method: true, read: false, write: false, range: '', doc: 'Enciende la parte.' },
  { name: 'off', method: true, read: false, write: false, range: '', doc: 'Apaga la parte.' },
  { name: 'toggle', method: true, read: false, write: false, range: '', doc: 'Cambia el estado.' },
  { name: 'is_on', method: false, read: true, write: false, range: '', doc: 'Verdadero mientras está encendida.' },
  { name: 'is_broken', method: false, read: true, write: false, range: '', doc: 'Verdadero si ha sido destruida.' },
  { name: 'open', method: true, read: false, write: false, range: '', doc: 'Abre la válvula.' },
  { name: 'close', method: true, read: false, write: false, range: '', doc: 'Cierra la válvula.' },
  { name: 'is_open', method: false, read: true, write: false, range: '', doc: 'Verdadero mientras está abierta.' },
  { name: 'pressure', method: false, read: true, write: false, range: '', doc: 'Presión junto a la parte.' },
  { name: 'temperature', method: false, read: true, write: false, range: 'K', doc: 'Temperatura junto a la parte.' },
  { name: 'rate', method: false, read: true, write: true, range: '0.1–1000', doc: 'Gas que añade, en mol/s.' },
  { name: 'max_pressure', method: false, read: true, write: true, range: '', doc: 'Deja de añadir por encima de esta presión.' },
  { name: 'gas', method: false, read: true, write: true, range: '', doc: 'Gas que añade.' },
  { name: 'power', method: false, read: true, write: true, range: '0–5000', doc: 'Fuerza del ventilador.' },
  { name: 'top_speed', method: false, read: true, write: true, range: '0–200', doc: 'Velocidad máxima del gas.' },
  { name: 'reverse', method: false, read: true, write: true, range: '', doc: 'Invierte el sentido.' },
  { name: 'min_size', method: false, read: true, write: true, range: '0–10', doc: 'Molécula más pequeña.' },
  { name: 'max_size', method: false, read: true, write: true, range: '0–10', doc: 'Molécula más grande.' },
  { name: 'flow_rate', method: false, read: true, write: true, range: '0–60', doc: 'Velocidad de filtrado.' },
  { name: 'limit', method: false, read: true, write: true, range: '', doc: 'Gas máximo por segundo.' },
  { name: 'flow', method: false, read: true, write: false, range: '', doc: 'Gas que pasó en el último tick.' },
  { name: 'position', method: false, read: true, write: false, range: '', doc: 'Posición del pistón.' },
  { name: 'speed', method: false, read: true, write: false, range: '', doc: 'Velocidad del pistón.' },
  { name: 'density', method: false, read: true, write: false, range: '', doc: 'Gas total de la celda.' },
  { name: 'speed_x', method: false, read: true, write: false, range: '', doc: 'Velocidad en x.' },
  { name: 'speed_y', method: false, read: true, write: false, range: '', doc: 'Velocidad en y.' }
];

const FALLBACK_SHIP = [
  { name: 'altitude', range: 'km' }, { name: 'speed', range: 'km/s' },
  { name: 'propellant', range: '' }, { name: 'thrust', range: '' },
  { name: 'mass', range: '' }, { name: 'launched', range: '' }
];
const FALLBACK_SHOP = [
  { name: 'cash', range: '$' }, { name: 'stock', range: 'mol' },
  { name: 'price', range: '$' }, { name: 'buy', range: 'mol' }
];

let state = { parts: [], gases: [], ship: false, shop: false, inGame: false, connected: false };
let partOptions = [['valve_1', 'valve_1'], ['sensor_1', 'sensor_1'], ['injector_1', 'injector_1']];
let gasOptions = [['Fuel', 'Fuel'], ['Oxidizer', 'Oxidizer'], ['Air', 'Air']];

let workspace = null;
let lineMap = [];
let lastCode = '';
let lastErrorLine = 0;
let lastErrorText = '';
let aiSettings = { provider: 'builtin', url: 'http://127.0.0.1:11434', model: 'llama3.1', apiKey: '' };
let aiBusy = false;
let validateTimer = null;
let generateTimer = null;
let autosaveTimer = null;
let statusTimer = null;
let running = false;
let connected = false;
let booting = true;
let projectName = 'Mi programa';

const $ = (id) => document.getElementById(id);

/* ------------------------------------------------------------------ */
/* Utilities                                                          */
/* ------------------------------------------------------------------ */

function safe(name) {
  if (!name) return 'valor';
  let s = String(name).normalize('NFD').replace(/[\u0300-\u036f]/g, '')
    .replace(/[^A-Za-z0-9_]/g, '_');
  if (/^[0-9]/.test(s)) s = '_' + s;
  if (!s) s = 'valor';
  if (RESERVED.has(s)) s += '_';
  if (s.indexOf('__cb_') === 0) s = 'u' + s;
  return s;
}

function quote(text) {
  return '"' + String(text == null ? '' : text)
    .replace(/\\/g, '\\\\')
    .replace(/"/g, '\\"')
    .replace(/[\r\n]/g, ' ') + '"';
}

function num(v) {
  const n = Number(v);
  if (!isFinite(n)) return '0';
  return String(n);
}

function esc(s) {
  return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
}

async function api(path, opts) {
  const options = Object.assign({ headers: { 'Content-Type': 'application/json' } }, opts || {});
  const res = await fetch(path, options);
  const text = await res.text();
  let data;
  try { data = JSON.parse(text); } catch (e) { data = { ok: false, error: text || ('HTTP ' + res.status) }; }
  if (!res.ok) throw new Error(data.error || ('HTTP ' + res.status));
  return data;
}

function toast(msg, ms) {
  const el = $('toast');
  el.textContent = msg;
  el.classList.remove('hidden');
  clearTimeout(toast._t);
  toast._t = setTimeout(() => el.classList.add('hidden'), ms || 2600);
}

function setChip(text, cls) {
  const chip = $('chip');
  chip.textContent = text;
  chip.className = 'chip' + (cls ? ' ' + cls : '');
}

function varName(block, fieldName) {
  if (!block) return 'variable';
  const field = block.getField(fieldName || 'VAR');
  if (field && typeof field.getVariable === 'function') {
    const v = field.getVariable();
    if (v && v.name) return v.name;
    const id = field.getValue();
    const byId = workspace && workspace.getVariableById(id);
    if (byId) return byId.name;
    return id;
  }
  return block.getFieldValue(fieldName || 'VAR');
}

function membersFor(partName) {
  const p = (state.parts || []).find(x => x.name === partName);
  if (p && p.members && p.members.length) return p.members;
  return FALLBACK_MEMBERS;
}

function memberInfo(partName, memberName) {
  return membersFor(partName).find(m => m.name === memberName) || null;
}

/* ------------------------------------------------------------------ */
/* Block definitions                                                  */
/* ------------------------------------------------------------------ */

function partField() {
  return new Blockly.FieldDropdown(function () {
    const opts = partOptions.length ? partOptions : [['valve_1', 'valve_1']];
    return opts;
  });
}

function memberField(mode) {
  return new Blockly.FieldDropdown(function () {
    const block = this.sourceBlock_;
    const part = block ? block.getFieldValue('PART') : '';
    const members = membersFor(part);
    const filtered = members.filter(m =>
      mode === 'method' ? m.method :
        mode === 'write' ? (m.write && !m.method) :
          (m.read && !m.method));
    const list = filtered.length ? filtered : members;
    const out = [];
    const seen = new Set();
    for (const m of list) {
      if (!m || !m.name || seen.has(m.name)) continue;
      seen.add(m.name);
      out.push([m.name, m.name]);
    }
    return out.length ? out : [['is_on', 'is_on']];
  });
}

function gasField() {
  return new Blockly.FieldDropdown(function () {
    return gasOptions.length ? gasOptions : [['Fuel', 'Fuel']];
  });
}

function hatBlock(type, text, tooltip, colour) {
  Blockly.Blocks[type] = {
    init: function () {
      this.appendDummyInput().appendField(text);
      this.setNextStatement(true, null);
      this.setColour(colour || C.events);
      this.hat = 'cap';
      this.setTooltip(tooltip || '');
    }
  };
}

function defineBlocks() {
  /* ---------------------------- events ---------------------------- */
  hatBlock('cb_when_start', t('block.when_start'),
    t('tip.when_start'), C.events);
  hatBlock('cb_when_update', t('block.when_update'),
    t('tip.when_update'), C.events);
  hatBlock('cb_when_stop', t('block.when_stop'),
    t('tip.when_stop'), C.events);
  hatBlock('cb_when_sim_start', t('block.when_sim_start'),
    t('tip.when_sim_start'), C.events);

  Blockly.Blocks['cb_when_receive'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.when_receive'))
        .appendField(new Blockly.FieldTextInput(t('block.defaultEvent')), 'EVENT');
      this.setNextStatement(true, null);
      this.setColour(C.events);
      this.hat = 'cap';
      this.setTooltip(t('tip.when_receive'));
    }
  };

  Blockly.Blocks['cb_broadcast'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.broadcast'))
        .appendField(new Blockly.FieldTextInput(t('block.defaultEvent')), 'EVENT');
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.events);
      this.setTooltip(t('tip.broadcast'));
    }
  };

  /* ---------------------------- control --------------------------- */
  Blockly.Blocks['cb_wait'] = {
    init: function () {
      this.appendValueInput('SECONDS').setCheck('Number').appendField(t('block.wait'));
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.control);
      this.setTooltip(t('tip.wait'));
    }
  };

  Blockly.Blocks['cb_stop'] = {
    init: function () {
      this.appendDummyInput().appendField(t('block.stop'));
      this.setPreviousStatement(true, null);
      this.setColour(C.control);
      this.setTooltip(t('tip.stop'));
    }
  };

  /* --------------------------- combustor -------------------------- */
  Blockly.Blocks['cb_print'] = {
    init: function () {
      this.appendValueInput('VALUE').appendField(t('block.print'));
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.data);
      this.setTooltip(t('tip.print'));
    }
  };

  Blockly.Blocks['cb_action'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(partField(), 'PART')
        .appendField('.')
        .appendField(memberField('method'), 'MEMBER')
        .appendField('()');
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.data);
      this.setTooltip(t('tip.action'));
    }
  };

  Blockly.Blocks['cb_set'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.set'))
        .appendField(partField(), 'PART')
        .appendField('.')
        .appendField(memberField('write'), 'MEMBER');
      this.appendValueInput('VALUE').appendField(t('block.to'));
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.data);
      this.setTooltip(t('tip.set'));
    }
  };

  Blockly.Blocks['cb_get'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(partField(), 'PART')
        .appendField('.')
        .appendField(memberField('read'), 'MEMBER');
      this.setOutput(true, 'Number');
      this.setColour(C.data);
      this.setTooltip(t('tip.get'));
    }
  };

  Blockly.Blocks['cb_sensor_gas'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.sensor_gas'))
        .appendField(gasField(), 'GAS')
        .appendField(t('block.in'))
        .appendField(partField(), 'PART');
      this.setOutput(true, 'Number');
      this.setColour(C.data);
      this.setTooltip(t('tip.sensor_gas'));
    }
  };

  Blockly.Blocks['cb_sensor_moles'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.sensor_moles'))
        .appendField(gasField(), 'GAS')
        .appendField(t('block.in'))
        .appendField(partField(), 'PART');
      this.setOutput(true, 'Number');
      this.setColour(C.data);
      this.setTooltip(t('tip.sensor_moles'));
    }
  };

  Blockly.Blocks['cb_ship_get'] = {
    init: function () {
      const opts = () => (state.shipMembers && state.shipMembers.length ? state.shipMembers : FALLBACK_SHIP)
        .map(m => [m.name, m.name]);
      this.appendDummyInput().appendField(t('block.ship')).appendField(new Blockly.FieldDropdown(opts), 'MEMBER');
      this.setOutput(true, 'Number');
      this.setColour(C.data);
      this.setTooltip(t('tip.ship'));
    }
  };

  Blockly.Blocks['cb_shop_get'] = {
    init: function () {
      this.appendDummyInput().appendField(t('block.shop')).appendField(
        new Blockly.FieldDropdown([['cash', 'cash']]), 'MEMBER');
      this.setOutput(true, 'Number');
      this.setColour(C.data);
      this.setTooltip(t('tip.shop'));
    }
  };

  Blockly.Blocks['cb_shop_gas'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.shop'))
        .appendField(new Blockly.FieldDropdown([['stock', 'stock'], ['price', 'price']]), 'MEMBER')
        .appendField(t('block.from'))
        .appendField(gasField(), 'GAS');
      this.setOutput(true, 'Number');
      this.setColour(C.data);
      this.setTooltip(t('tip.shop'));
    }
  };

  Blockly.Blocks['cb_shop_buy'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.buy'))
        .appendField(gasField(), 'GAS');
      this.appendValueInput('MOLES').setCheck('Number').appendField('mol');
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.data);
      this.setTooltip(t('tip.buy'));
    }
  };

  /* ----------------------------- lists ---------------------------- */
  Blockly.Blocks['cb_list_create'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.list_create'))
        .appendField(new Blockly.FieldVariable(t('block.defaultList')), 'VAR');
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.lists);
      this.setTooltip(t('tip.list_create'));
    }
  };

  Blockly.Blocks['cb_list_append'] = {
    init: function () {
      this.appendValueInput('VALUE').appendField(t('block.list_append'));
      this.appendDummyInput()
        .appendField(t('block.toList'))
        .appendField(new Blockly.FieldVariable(t('block.defaultList')), 'LIST');
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.lists);
      this.setTooltip(t('tip.list_append'));
    }
  };

  Blockly.Blocks['cb_list_remove'] = {
    init: function () {
      this.appendValueInput('INDEX').setCheck('Number').appendField(t('block.list_remove'));
      this.appendDummyInput()
        .appendField(t('block.from'))
        .appendField(new Blockly.FieldVariable(t('block.defaultList')), 'LIST');
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.lists);
      this.setTooltip(t('tip.list_remove'));
    }
  };

  Blockly.Blocks['cb_list_contains'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.list_contains'))
        .appendField(new Blockly.FieldVariable(t('block.defaultList')), 'LIST')
        .appendField(t('block.contains'));
      this.appendValueInput('VALUE');
      this.setOutput(true, 'Boolean');
      this.setColour(C.lists);
      this.setTooltip(t('tip.list_contains'));
    }
  };

  /* --------------------------- variables -------------------------- */
  Blockly.Blocks['cb_var_create'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.var_create'))
        .appendField(new Blockly.FieldVariable('variable'), 'VAR')
        .appendField('=');
      this.appendValueInput('VALUE');
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.variables);
      this.setTooltip(t('tip.var_create'));
    }
  };

  Blockly.Blocks['cb_var_delete'] = {
    init: function () {
      this.appendDummyInput()
        .appendField(t('block.var_delete'))
        .appendField(new Blockly.FieldVariable('variable'), 'VAR');
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.variables);
      this.setTooltip(t('tip.var_delete'));
    }
  };

  Blockly.Blocks['cb_gs_code'] = {
    init: function () {
      this.appendDummyInput().appendField(t('block.gs_code'));
      this.appendDummyInput().appendField(
        new (Blockly.FieldMultilineInput || Blockly.FieldTextInput)(t('block.gsDefault')), 'CODE');
      this.setPreviousStatement(true, null);
      this.setNextStatement(true, null);
      this.setColour(C.data);
      this.setTooltip(t('tip.gs_code'));
    }
  };
}

/* ------------------------------------------------------------------ */
/* Toolbox                                                            */
/* ------------------------------------------------------------------ */

function firstPart(kind) {
  const p = (state.parts || []).find(x => x.kind === kind);
  return p ? p.name : null;
}

function buildToolbox() {
  const sensor = firstPart('Sensor');
  const valve = firstPart('Valve');
  const injector = firstPart('Injector');
  const anyPart = (state.parts && state.parts.length) ? state.parts[0].name : 'valve_1';
  const readPart = sensor || valve || anyPart;
  const readMember = sensor ? 'temperature' : 'temperature';
  const actionPart = valve || anyPart;
  const actionMember = valve ? 'open' : 'on';
  const setPart = injector || anyPart;
  const setMember = injector ? 'rate' : 'power';

  const cats = [
    {
      kind: 'category', name: t('cat.events'), colour: C.events, contents: [
        { kind: 'block', type: 'cb_when_start' },
        { kind: 'block', type: 'cb_when_update' },
        { kind: 'block', type: 'cb_when_receive', fields: { EVENT: 'evento' } },
        { kind: 'block', type: 'cb_when_stop' },
        { kind: 'block', type: 'cb_when_sim_start' },
        { kind: 'block', type: 'cb_broadcast', fields: { EVENT: 'evento' } }
      ]
    },
    {
      kind: 'category', name: t('cat.control'), colour: C.control, contents: [
        { kind: 'block', type: 'controls_if' },
        { kind: 'block', type: 'controls_if', extraState: { hasElse: true } },
        { kind: 'block', type: 'controls_repeat_ext', inputs: { TIMES: { shadow: { type: 'math_number', fields: { NUM: 10 } } } } },
        { kind: 'block', type: 'controls_whileUntil', fields: { MODE: 'WHILE' } },
        { kind: 'block', type: 'controls_whileUntil', fields: { MODE: 'UNTIL' } },
        { kind: 'block', type: 'controls_for' },
        { kind: 'block', type: 'controls_forEach' },
        { kind: 'block', type: 'controls_flow_statements', fields: { FLOW: 'BREAK' } },
        { kind: 'block', type: 'cb_wait', inputs: { SECONDS: { shadow: { type: 'math_number', fields: { NUM: 1 } } } } },
        { kind: 'block', type: 'cb_stop' }
      ]
    },
    {
      kind: 'category', name: t('cat.logic'), colour: C.logic, contents: [
        { kind: 'block', type: 'logic_operation', fields: { OP: 'AND' } },
        { kind: 'block', type: 'logic_operation', fields: { OP: 'OR' } },
        { kind: 'block', type: 'logic_negate' },
        { kind: 'block', type: 'logic_boolean', fields: { BOOL: 'TRUE' } },
        { kind: 'block', type: 'logic_boolean', fields: { BOOL: 'FALSE' } },
        { kind: 'block', type: 'logic_ternary' }
      ]
    },
    {
      kind: 'category', name: t('cat.compare'), colour: C.compare, contents: [
        { kind: 'block', type: 'logic_compare', fields: { OP: 'EQ' } },
        { kind: 'block', type: 'logic_compare', fields: { OP: 'NEQ' } },
        { kind: 'block', type: 'logic_compare', fields: { OP: 'GT' } },
        { kind: 'block', type: 'logic_compare', fields: { OP: 'LT' } },
        { kind: 'block', type: 'logic_compare', fields: { OP: 'GTE' } },
        { kind: 'block', type: 'logic_compare', fields: { OP: 'LTE' } }
      ]
    },
    {
      kind: 'category', name: t('cat.math'), colour: C.math, contents: [
        { kind: 'block', type: 'math_number', fields: { NUM: 1 } },
        { kind: 'block', type: 'math_arithmetic', fields: { OP: 'ADD' } },
        { kind: 'block', type: 'math_arithmetic', fields: { OP: 'MINUS' } },
        { kind: 'block', type: 'math_arithmetic', fields: { OP: 'MULTIPLY' } },
        { kind: 'block', type: 'math_arithmetic', fields: { OP: 'DIVIDE' } },
        { kind: 'block', type: 'math_arithmetic', fields: { OP: 'POWER' } },
        { kind: 'block', type: 'math_modulo' },
        { kind: 'block', type: 'math_single', fields: { OP: 'ROOT' } },
        { kind: 'block', type: 'math_single', fields: { OP: 'ABS' } },
        { kind: 'block', type: 'math_round', fields: { OP: 'ROUND' } },
        { kind: 'block', type: 'math_random_int' },
        { kind: 'block', type: 'math_random_float' },
        { kind: 'block', type: 'math_constant', fields: { CONSTANT: 'PI' } }
      ]
    },
    {
      kind: 'category', name: t('cat.variables'), colour: C.variables, custom: 'VARIABLE', contents: [
        { kind: 'block', type: 'cb_var_create', inputs: { VALUE: { shadow: { type: 'math_number', fields: { NUM: 0 } } } } },
        { kind: 'block', type: 'variables_get' },
        { kind: 'block', type: 'variables_set', inputs: { VALUE: { shadow: { type: 'math_number', fields: { NUM: 0 } } } } },
        { kind: 'block', type: 'math_change', inputs: { DELTA: { shadow: { type: 'math_number', fields: { NUM: 1 } } } } },
        { kind: 'block', type: 'cb_var_delete' }
      ]
    },
    {
      kind: 'category', name: t('cat.lists'), colour: C.lists, contents: [
        { kind: 'block', type: 'cb_list_create' },
        { kind: 'block', type: 'cb_list_append' },
        { kind: 'block', type: 'cb_list_remove' },
        { kind: 'block', type: 'cb_list_contains' },
        { kind: 'block', type: 'lists_length' },
        { kind: 'block', type: 'lists_getIndex', fields: { MODE: 'GET', WHERE: 'FROM_START' } },
        { kind: 'block', type: 'lists_create_with' }
      ]
    },
    {
      kind: 'category', name: t('cat.functions'), colour: C.functions, custom: 'PROCEDURE', contents: [
        { kind: 'block', type: 'procedures_defnoreturn' },
        { kind: 'block', type: 'procedures_defreturn' },
        { kind: 'block', type: 'procedures_callnoreturn' },
        { kind: 'block', type: 'procedures_callreturn' },
        { kind: 'block', type: 'procedures_ifreturn' }
      ]
    },
    {
      kind: 'category', name: t('cat.data'), colour: C.data, contents: [
        { kind: 'block', type: 'cb_print', inputs: { VALUE: { shadow: { type: 'text', fields: { TEXT: t('block.hello') } } } } },
        { kind: 'block', type: 'cb_get', fields: { PART: readPart, MEMBER: readMember } },
        { kind: 'block', type: 'cb_action', fields: { PART: actionPart, MEMBER: actionMember } },
        { kind: 'block', type: 'cb_set', fields: { PART: setPart, MEMBER: setMember }, inputs: { VALUE: { shadow: { type: 'math_number', fields: { NUM: 10 } } } } },
        { kind: 'block', type: 'cb_sensor_gas', fields: { PART: sensor || readPart, GAS: (gasOptions[0] || ['Fuel'])[1] } },
        { kind: 'block', type: 'cb_sensor_moles', fields: { PART: sensor || readPart, GAS: (gasOptions[0] || ['Fuel'])[1] } },
        { kind: 'block', type: 'cb_gs_code' }
      ]
    }
  ];

  if (state.ship) {
    cats[cats.length - 1].contents.push({ kind: 'block', type: 'cb_ship_get' });
  }
  if (state.shop) {
    cats[cats.length - 1].contents.push(
      { kind: 'block', type: 'cb_shop_get' },
      { kind: 'block', type: 'cb_shop_gas' },
      { kind: 'block', type: 'cb_shop_buy', inputs: { MOLES: { shadow: { type: 'math_number', fields: { NUM: 10 } } } } }
    );
  }

  return { kind: 'categoryToolbox', contents: cats };
}

/* ------------------------------------------------------------------ */
/* Generator: Blockly blocks -> Combustor GS                          */
/* ------------------------------------------------------------------ */

function generate(root) {
  const lines = [];   // {text, id}
  const lineToBlock = [];
  let indent = 0;
  const warnings = [];
  const used = { random: false, pow: false, forRange: false, lastIndex: false, prime: false, ternary: false };

  function push(text, block) {
    lines.push({ text: '    '.repeat(indent) + text, id: block ? block.id : null });
    lineToBlock.push(block ? block.id : null);
  }

  function body(first, owner, allowEmpty) {
    const before = lines.length;
    for (let b = first; b; b = b.getNextBlock()) stmt(b);
    if (lines.length === before && allowEmpty !== false) push('pass', owner);
  }

  function value(block, fallback) {
    if (!block) return fallback == null ? '0' : fallback;
    const v = expr(block);
    return v == null ? (fallback == null ? '0' : fallback) : v;
  }

  function expr(b) {
    switch (b.type) {
      case 'math_number': return num(b.getFieldValue('NUM'));
      case 'text': return quote(b.getFieldValue('TEXT'));
      case 'logic_boolean': return b.getFieldValue('BOOL') === 'TRUE' ? 'True' : 'False';
      case 'logic_null': return 'None';

      case 'logic_compare': {
        const a = value(b.getInputTargetBlock('A'));
        const c = value(b.getInputTargetBlock('B'));
        switch (b.getFieldValue('OP')) {
          case 'EQ': return '(' + a + ' == ' + c + ')';
          case 'NEQ': return '(not (' + a + ' == ' + c + '))';
          case 'GT': return '(' + a + ' > ' + c + ')';
          case 'LT': return '(' + a + ' < ' + c + ')';
          case 'GTE': return '(not (' + a + ' < ' + c + '))';
          case 'LTE': return '(not (' + a + ' > ' + c + '))';
        }
        return '(' + a + ' == ' + c + ')';
      }
      case 'logic_operation': {
        const a = value(b.getInputTargetBlock('A'));
        const c = value(b.getInputTargetBlock('B'));
        return '(' + a + (b.getFieldValue('OP') === 'AND' ? ' and ' : ' or ') + c + ')';
      }
      case 'logic_negate': return '(not ' + value(b.getInputTargetBlock('BOOL')) + ')';
      case 'logic_ternary': {
        used.ternary = true;
        return '__cb_ternary(' + value(b.getInputTargetBlock('IF')) + ', ' +
          value(b.getInputTargetBlock('THEN')) + ', ' + value(b.getInputTargetBlock('ELSE')) + ')';
      }

      case 'math_arithmetic': {
        const a = value(b.getInputTargetBlock('A'));
        const c = value(b.getInputTargetBlock('B'));
        switch (b.getFieldValue('OP')) {
          case 'ADD': return '(' + a + ' + ' + c + ')';
          case 'MINUS': return '(' + a + ' - ' + c + ')';
          case 'MULTIPLY': return '(' + a + ' * ' + c + ')';
          case 'DIVIDE': return '(' + a + ' / ' + c + ')';
          case 'POWER': used.pow = true; return '__cb_pow(' + a + ', ' + c + ')';
        }
        return '(' + a + ' + ' + c + ')';
      }
      case 'math_modulo':
        return '(' + value(b.getInputTargetBlock('DIVIDEND')) + ' % ' + value(b.getInputTargetBlock('DIVISOR')) + ')';
      case 'math_single': {
        const a = value(b.getInputTargetBlock('NUM'));
        switch (b.getFieldValue('OP')) {
          case 'ROOT': return 'sqrt(' + a + ')';
          case 'ABS': return 'abs(' + a + ')';
          case 'NEG': return '(-' + a + ')';
          case 'LN': return '0';
          case 'LOG10': return '0';
          case 'EXP': return '0';
          case 'POW10': used.pow = true; return '__cb_pow(10, ' + a + ')';
        }
        return a;
      }
      case 'math_round': {
        const a = value(b.getInputTargetBlock('NUM'));
        switch (b.getFieldValue('OP')) {
          case 'ROUNDUP': return 'ceil(' + a + ')';
          case 'ROUNDDOWN': return 'floor(' + a + ')';
          default: return 'round(' + a + ')';
        }
      }
      case 'math_random_int': {
        used.random = true;
        const a = value(b.getInputTargetBlock('FROM'));
        const c = value(b.getInputTargetBlock('TO'));
        return 'int(__cb_random(' + a + ', ' + c + ' + 1))';
      }
      case 'math_random_float': used.random = true; return '__cb_rand()';
      case 'math_number_property': {
        const a = value(b.getInputTargetBlock('NUMBER'));
        switch (b.getFieldValue('PROPERTY')) {
          case 'EVEN': return '((' + a + ' % 2) == 0)';
          case 'ODD': return '((' + a + ' % 2) != 0)';
          case 'POSITIVE': return '(' + a + ' > 0)';
          case 'NEGATIVE': return '(' + a + ' < 0)';
          case 'DIVISIBLE_BY':
            return '((' + a + ' % ' + value(b.getInputTargetBlock('DIVISOR'), '1') + ') == 0)';
          case 'PRIME': used.prime = true; return '__cb_prime(int(' + a + '))';
        }
        return 'False';
      }
      case 'math_constant': {
        switch (b.getFieldValue('CONSTANT')) {
          case 'PI': return 'pi';
          case 'E': return '2.718281828459045';
          case 'GOLDEN_RATIO': return '1.618033988749895';
          case 'SQRT2': return '1.4142135623730951';
          case 'SQRT1_2': return '0.7071067811865476';
          default: return '0';
        }
      }

      case 'text_join': {
        const n = typeof b.itemCount_ === 'number' ? b.itemCount_ : countInputs(b, 'ADD');
        if (!n) return '""';
        const parts = [];
        for (let i = 0; i < n; i++) parts.push('str(' + value(b.getInputTargetBlock('ADD' + i)) + ')');
        return '(' + parts.join(' + ') + ')';
      }
      case 'text_length': return 'len(' + value(b.getInputTargetBlock('VALUE')) + ')';
      case 'text_isEmpty': return '(len(' + value(b.getInputTargetBlock('VALUE')) + ') == 0)';
      case 'text_charAt': {
        const t = value(b.getInputTargetBlock('VALUE'));
        const at = textIndex(b, 'FROM_START', 'FROM_END', 'FIRST', 'LAST', t);
        return t + '[' + at + ']';
      }
      case 'text_changeCase': {
        const t = value(b.getInputTargetBlock('TEXT'));
        return t;
      }

      case 'variables_get': return safe(varName(b, 'VAR'));

      case 'lists_create_with': {
        const n = typeof b.itemCount_ === 'number' ? b.itemCount_ : countInputs(b, 'ADD');
        const parts = [];
        for (let i = 0; i < n; i++) parts.push(value(b.getInputTargetBlock('ADD' + i)));
        return '[' + parts.join(', ') + ']';
      }
      case 'lists_getIndex': {
        const list = value(b.getInputTargetBlock('VALUE'), '[]');
        const idx = listIndex(b, list);
        if (b.getFieldValue('MODE') === 'GET_REMOVE') return list + '.pop(' + idx + ')';
        return list + '[' + idx + ']';
      }
      case 'lists_length': return 'len(' + value(b.getInputTargetBlock('VALUE'), '[]') + ')';
      case 'lists_isEmpty': return '(len(' + value(b.getInputTargetBlock('VALUE'), '[]') + ') == 0)';
      case 'lists_indexOf': {
        const list = value(b.getInputTargetBlock('VALUE'), '[]');
        const item = value(b.getInputTargetBlock('FIND'));
        if (b.getFieldValue('END') === 'LAST') {
          used.lastIndex = true;
          return '__cb_last_index(' + list + ', ' + item + ')';
        }
        return list + '.index(' + item + ')';
      }

      case 'procedures_callreturn': {
        const name = procedureName(b);
        const args = procedureArgs(b);
        return name + '(' + args.join(', ') + ')';
      }

      case 'cb_get': {
        const part = safe(b.getFieldValue('PART'));
        const member = b.getFieldValue('MEMBER');
        return part + '.' + safe(member);
      }
      case 'cb_sensor_gas': {
        const part = safe(b.getFieldValue('PART'));
        const gas = b.getFieldValue('GAS');
        return part + '.gas(' + quote(gas) + ')';
      }
      case 'cb_sensor_moles': {
        const part = safe(b.getFieldValue('PART'));
        const gas = b.getFieldValue('GAS');
        return part + '.moles(' + quote(gas) + ')';
      }
      case 'cb_ship_get': return 'ship.' + safe(b.getFieldValue('MEMBER'));
      case 'cb_shop_get': return 'shop.cash';
      case 'cb_shop_gas': return 'shop.' + safe(b.getFieldValue('MEMBER')) + '(' + quote(b.getFieldValue('GAS')) + ')';

      default:
        warnings.push('Bloque sin traducir: ' + b.type);
        return '0';
    }
  }

  function stmt(b) {
    switch (b.type) {
      case 'controls_if': {
        let n = 0;
        while (b.getInput('IF' + n)) n++;
        if (n === 0) n = 1;
        for (let i = 0; i < n; i++) {
          push((i === 0 ? 'if ' : 'elif ') + value(b.getInputTargetBlock('IF' + i), 'False') + ':', b);
          indent++;
          body(b.getInputTargetBlock('DO' + i), b);
          indent--;
        }
        if (b.getInput('ELSE')) {
          push('else:', b);
          indent++;
          body(b.getInputTargetBlock('ELSE'), b);
          indent--;
        }
        break;
      }
      case 'controls_repeat_ext':
      case 'controls_repeat': {
        push('for __cb_i in range(int(' + value(b.getInputTargetBlock('TIMES')) + ')):', b);
        indent++;
        body(b.getInputTargetBlock('DO'), b);
        indent--;
        break;
      }
      case 'controls_whileUntil': {
        const mode = b.getFieldValue('MODE') === 'UNTIL' ? 'while not ' : 'while ';
        push(mode + value(b.getInputTargetBlock('BOOL'), 'False') + ':', b);
        indent++;
        body(b.getInputTargetBlock('DO'), b);
        indent--;
        break;
      }
      case 'controls_for': {
        used.forRange = true;
        const v = safe(varName(b, 'VAR'));
        const from = value(b.getInputTargetBlock('FROM'), '0');
        const to = value(b.getInputTargetBlock('TO'), '0');
        const by = value(b.getInputTargetBlock('BY'), '1');
        push('for ' + v + ' in __cb_for_range(int(' + from + '), int(' + to + '), int(' + by + ')):', b);
        indent++;
        body(b.getInputTargetBlock('DO'), b);
        indent--;
        break;
      }
      case 'controls_forEach': {
        const v = safe(varName(b, 'VAR'));
        push('for ' + v + ' in ' + value(b.getInputTargetBlock('LIST'), '[]') + ':', b);
        indent++;
        body(b.getInputTargetBlock('DO'), b);
        indent--;
        break;
      }
      case 'controls_flow_statements':
        push(b.getFieldValue('FLOW') === 'CONTINUE' ? 'continue' : 'break', b);
        break;

      case 'variables_set':
        push(safe(varName(b, 'VAR')) + ' = ' + value(b.getInputTargetBlock('VALUE')), b);
        break;
      case 'math_change':
        push(safe(varName(b, 'VAR')) + ' = ' + safe(varName(b, 'VAR')) + ' + (' +
          value(b.getInputTargetBlock('DELTA'), '1') + ')', b);
        break;
      case 'cb_var_create':
        push(safe(varName(b, 'VAR')) + ' = ' + value(b.getInputTargetBlock('VALUE')), b);
        break;
      case 'cb_var_delete':
        push(safe(varName(b, 'VAR')) + ' = None', b);
        break;

      case 'lists_setIndex': {
        const list = value(b.getInputTargetBlock('LIST'), '[]');
        const item = value(b.getInputTargetBlock('TO') || b.getInputTargetBlock('VALUE'));
        if (b.getFieldValue('MODE') === 'INSERT') {
          push(list + '.insert(' + listIndex(b, list) + ', ' + item + ')', b);
        } else {
          push(list + '[' + listIndex(b, list) + '] = ' + item, b);
        }
        break;
      }
      case 'cb_list_create':
        push(safe(varName(b, 'VAR')) + ' = []', b);
        break;
      case 'cb_list_append': {
        const list = safe(varName(b, 'LIST'));
        push(list + '.append(' + value(b.getInputTargetBlock('VALUE')) + ')', b);
        break;
      }
      case 'cb_list_remove': {
        const list = safe(varName(b, 'LIST'));
        push(list + '.pop(' + value(b.getInputTargetBlock('INDEX'), '0') + ')', b);
        break;
      }

      case 'text_print':
        push('print(' + value(b.getInputTargetBlock('TEXT')) + ')', b);
        break;
      case 'text_append': {
        const v = safe(varName(b, 'VAR'));
        push(v + ' = str(' + v + ') + str(' + value(b.getInputTargetBlock('TEXT')) + ')', b);
        break;
      }
      case 'procedures_callnoreturn':
        push(procedureName(b) + '(' + procedureArgs(b).join(', ') + ')', b);
        break;
      case 'procedures_ifreturn': {
        push('if ' + value(b.getInputTargetBlock('CONDITION'), 'False') + ':', b);
        indent++;
        const val = b.getInputTargetBlock('VALUE');
        push('return' + (val ? ' ' + value(val) : ''), b);
        indent--;
        break;
      }

      case 'cb_wait':
        push('wait(' + value(b.getInputTargetBlock('SECONDS'), '0') + ')', b);
        break;
      case 'cb_stop':
        if (hats.stop.length) push('__cb_stop()', b);
        push('stop()', b);
        break;
      case 'cb_print':
        push('print(' + value(b.getInputTargetBlock('VALUE')) + ')', b);
        break;
      case 'cb_broadcast':
        push(eventVar(b.getFieldValue('EVENT')) + ' = True', b);
        break;
      case 'cb_action': {
        const part = safe(b.getFieldValue('PART'));
        const member = safe(b.getFieldValue('MEMBER'));
        push(part + '.' + member + '()', b);
        break;
      }
      case 'cb_set': {
        const part = safe(b.getFieldValue('PART'));
        const member = safe(b.getFieldValue('MEMBER'));
        push(part + '.' + member + ' = ' + value(b.getInputTargetBlock('VALUE')), b);
        break;
      }
      case 'cb_shop_buy':
        push('shop.buy(' + quote(b.getFieldValue('GAS')) + ', ' + value(b.getInputTargetBlock('MOLES'), '0') + ')', b);
        break;

      case 'cb_gs_code': {
        const raw = (b.getFieldValue('CODE') || '').replace(/\r/g, '');
        const rows = raw.split('\n');
        while (rows.length && rows[0].trim() === '') rows.shift();
        while (rows.length && rows[rows.length - 1].trim() === '') rows.pop();
        if (!rows.length) push('pass', b);
        else for (const row of rows) push(row, b);
        break;
      }

      case 'procedures_defnoreturn':
      case 'procedures_defreturn':
        // emitted at top level by generate()
        break;
      case 'cb_when_start':
      case 'cb_when_update':
      case 'cb_when_receive':
      case 'cb_when_stop':
      case 'cb_when_sim_start':
        break;

      default:
        warnings.push('Bloque de acción sin traducir: ' + b.type);
        break;
    }
  }

  /* ------------------------- helpers ------------------------- */

  function countInputs(block, prefix) {
    let n = 0;
    while (block.getInput(prefix + n)) n++;
    return n;
  }

  function textIndex(block, fromStart, fromEnd, first, last, textExpr) {
    const where = block.getFieldValue('WHERE');
    if (where === first) return '0';
    if (where === last) return '(len(' + textExpr + ') - 1)';
    if (where === fromEnd) return '(len(' + textExpr + ') - int(' + value(block.getInputTargetBlock('AT')) + ') - 1)';
    return 'int(' + value(block.getInputTargetBlock('AT')) + ')';
  }

  function listIndex(block, listExpr) {
    const where = block.getFieldValue('WHERE');
    if (where === 'FIRST') return '0';
    if (where === 'LAST') return '(len(' + listExpr + ') - 1)';
    if (where === 'FROM_END') return '(len(' + listExpr + ') - int(' + value(block.getInputTargetBlock('AT')) + ') - 1)';
    return 'int(' + value(block.getInputTargetBlock('AT')) + ')';
  }

  function procedureName(block) {
    let n = '';
    try { n = block.getProcedureCall ? block.getProcedureCall() : ''; } catch (e) { n = ''; }
    if (!n) n = block.getFieldValue('NAME') || 'funcion';
    return safe(n);
  }

  function procedureArgs(block) {
    const args = [];
    let n = 0;
    while (block.getInput('ARG' + n)) n++;
    for (let i = 0; i < n; i++) {
      args.push(value(block.getInputTargetBlock('ARG' + i)));
    }
    return args;
  }

  function procedureParams(defBlock) {
    try {
      const models = defBlock.argumentVarModels_ ||
        (defBlock.getVarModels ? defBlock.getVarModels() : []) || [];
      return models.map(v => safe(v && v.name ? v.name : String(v)));
    } catch (e) {
      return [];
    }
  }

  function eventVar(name) {
    return '__cb_evt_' + safe(name || 'evento');
  }


  /* ------------------------- top level ----------------------- */

  const tops = root.getTopBlocks(true);
  const hats = { start: [], update: [], receive: [], stop: [], simStart: [] };
  const receives = new Map();
  const broadcasts = new Set();

  for (const b of tops) {
    switch (b.type) {
      case 'cb_when_start': hats.start.push(b); break;
      case 'cb_when_sim_start': hats.simStart.push(b); break;
      case 'cb_when_update': hats.update.push(b); break;
      case 'cb_when_stop': hats.stop.push(b); break;
      case 'cb_when_receive':
        hats.receive.push(b);
        break;
      default: break;
    }
  }
  for (const b of root.getAllBlocks(false)) {
    if (b.type === 'cb_broadcast') broadcasts.add(eventVar(b.getFieldValue('EVENT')));
  }
  for (const b of hats.receive) {
    const ev = eventVar(b.getFieldValue('EVENT'));
    if (!receives.has(ev)) receives.set(ev, []);
    receives.get(ev).push(b);
  }

  // functions
  const defs = root.getAllBlocks(false).filter(b =>
    b.type === 'procedures_defnoreturn' || b.type === 'procedures_defreturn');

  // variables / lists
  const variables = (root.getVariableMap().getAllVariables() || []).filter(v => v && v.name);
  const listVars = new Set();
  for (const b of root.getAllBlocks(false)) {
    if (b.type === 'cb_list_create') listVars.add(varName(b, 'VAR'));
    if (b.type === 'cb_list_append' || b.type === 'cb_list_remove' || b.type === 'cb_list_contains')
      listVars.add(varName(b, 'LIST'));
    if (b.type.indexOf('lists_') === 0) {
      for (const input of ['LIST', 'VALUE']) {
        const target = b.getInputTargetBlock(input);
        if (target && target.type === 'variables_get') listVars.add(varName(target, 'VAR'));
      }
    }
    if (b.type === 'lists_setIndex') {
      const target = b.getInputTargetBlock('LIST');
      if (target && target.type === 'variables_get') listVars.add(varName(target, 'VAR'));
    }
  }

  // pre-scan helpers
  if (root.getAllBlocks(false).some(b => b.type === 'math_random_int' || b.type === 'math_random_float')) used.random = true;
  if (root.getAllBlocks(false).some(b => b.type === 'math_arithmetic' && b.getFieldValue('OP') === 'POWER')) used.pow = true;
  if (root.getAllBlocks(false).some(b => b.type === 'controls_for')) used.forRange = true;
  if (root.getAllBlocks(false).some(b => b.type === 'lists_indexOf' && b.getFieldValue('END') === 'LAST')) used.lastIndex = true;
  if (root.getAllBlocks(false).some(b => b.type === 'logic_ternary')) used.ternary = true;
  if (root.getAllBlocks(false).some(b => b.type === 'math_number_property' && b.getFieldValue('PROPERTY') === 'PRIME')) used.prime = true;

  for (const b of tops) {
    if (b.type.indexOf('procedures_') === 0 || b.type.indexOf('cb_when_') === 0) continue;
    warnings.push('Bloque suelto sin evento: ' + b.type);
  }

  // ---------------- emit ----------------
  push(t('code.commentTitle'), null);
  push('# ' + (projectName || 'Mi programa'), null);
  push(t('code.commentNoEdit'), null);
  push('', null);

  if (used.random) {
    push('__cb_rng = [12345]', null);
    push('def __cb_rand():', null);
    push('    __cb_rng[0] = (__cb_rng[0] * 48271) % 2147483647', null);
    push('    return __cb_rng[0] / 2147483647', null);
    push('def __cb_random(a, b):', null);
    push('    return a + __cb_rand() * (b - a)', null);
    push('', null);
  }
  if (used.pow) {
    push('def __cb_pow(a, b):', null);
    push('    r = 1', null);
    push('    n = int(b)', null);
    push('    if n < 0:', null);
    push('        return 0', null);
    push('    while n > 0:', null);
    push('        r = r * a', null);
    push('        n = n - 1', null);
    push('    return r', null);
    push('', null);
  }
  if (used.forRange) {
    push('def __cb_for_range(a, b, step):', null);
    push('    if step == 0:', null);
    push('        step = 1', null);
    push('    if b >= a:', null);
    push('        if step < 0:', null);
    push('            step = -step', null);
    push('    else:', null);
    push('        if step > 0:', null);
    push('            step = -step', null);
    push('    end = b + 1', null);
    push('    if step < 0:', null);
    push('        end = b - 1', null);
    push('    return range(a, end, step)', null);
    push('', null);
  }
  if (used.lastIndex) {
    push('def __cb_last_index(items, value):', null);
    push('    i = len(items) - 1', null);
    push('    while i >= 0:', null);
    push('        if items[i] == value:', null);
    push('            return i', null);
    push('        i = i - 1', null);
    push('    return -1', null);
    push('', null);
  }
  if (used.ternary) {
    push('def __cb_ternary(cond, a, b):', null);
    push('    if cond:', null);
    push('        return a', null);
    push('    return b', null);
    push('', null);
  }
  if (used.prime) {
    push('def __cb_prime(n):', null);
    push('    if n < 2:', null);
    push('        return False', null);
    push('    i = 2', null);
    push('    while i * i <= n:', null);
    push('        if (n % i) == 0:', null);
    push('            return False', null);
    push('        i = i + 1', null);
    push('    return True', null);
    push('', null);
  }

  if (variables.length) {
    for (const v of variables) {
      const n = safe(v.name);
      push(n + ' = ' + (listVars.has(v.name) ? '[]' : '0'), null);
    }
    push('', null);
  }

  const evs = new Set([...receives.keys(), ...broadcasts]);
  for (const ev of evs) push(ev + ' = False', null);
  if (evs.size) push('', null);

  for (const d of defs) {
    const name = safe(d.getFieldValue('NAME') || 'funcion');
    const params = procedureParams(d);
    push('def ' + name + '(' + params.join(', ') + '):', d);
    indent++;
    body(d.getInputTargetBlock('STACK'), d);
    if (d.type === 'procedures_defreturn') {
      const ret = d.getInputTargetBlock('RETURN');
      if (ret) push('return ' + value(ret), d);
    }
    indent--;
    push('', d);
  }

  if (hats.stop.length) {
    push('def __cb_stop():', null);
    indent++;
    const before = lines.length;
    for (const h of hats.stop) for (let b = h.getNextBlock(); b; b = b.getNextBlock()) stmt(b);
    if (lines.length === before) push('pass', null);
    indent--;
    push('', null);
  }

  for (const h of hats.start.concat(hats.simStart)) {
    push('# --- Al iniciar ---', h);
    body(h.getNextBlock(), h);
    push('', null);
  }

  if (hats.update.length || receives.size) {
    push('# --- Bucle del programa (un tick por pasada) ---', hats.update[0] || null);
    push('while True:', hats.update[0] || null);
    indent++;
    const before = lines.length;
    for (const h of hats.update) for (let b = h.getNextBlock(); b; b = b.getNextBlock()) stmt(b);
    for (const [ev, handlers] of receives) {
      push('if ' + ev + ':', handlers[0]);
      indent++;
      push(ev + ' = False', handlers[0]);
      for (const h of handlers) for (let b = h.getNextBlock(); b; b = b.getNextBlock()) stmt(b);
      indent--;
    }
    if (lines.length === before) push('pass', null);
    indent--;
    push('', null);
  }

  const code = lines.map(l => l.text).join('\n') + '\n';
  return { code, lineMap, warnings, used };
}

/* ------------------------------------------------------------------ */
/* Blockly setup                                                      */
/* ------------------------------------------------------------------ */

function shade(hex, f) {
  const n = parseInt(hex.replace('#', ''), 16);
  const r = Math.max(0, Math.min(255, Math.round(((n >> 16) & 255) * f)));
  const g = Math.max(0, Math.min(255, Math.round(((n >> 8) & 255) * f)));
  const b = Math.max(0, Math.min(255, Math.round((n & 255) * f)));
  return '#' + ((1 << 24) + (r << 16) + (g << 8) + b).toString(16).slice(1);
}

function blockStyle(hex) {
  return { colourPrimary: hex, colourSecondary: shade(hex, 0.82), colourTertiary: shade(hex, 0.66) };
}

function makeTheme() {
  const styles = {
    workspaceBackgroundColour: '#0f1116',
    toolboxBackgroundColour: '#1a1f29',
    toolboxForegroundColour: '#e9edf5',
    flyoutBackgroundColour: '#222836',
    flyoutForegroundColour: '#e9edf5',
    flyoutOpacity: 1,
    scrollbarColour: '#3a4356',
    scrollbarOpacity: 0.7,
    insertionMarkerColour: '#5b8cff',
    insertionMarkerOpacity: 0.5,
    cursorColour: '#5b8cff',
    markerColour: '#5b8cff',
    blackBackground: '#0f1116'
  };
  const base = (Blockly.Themes && Blockly.Themes.Classic) ? Blockly.Themes.Classic : undefined;
  const config = {
    componentStyles: styles,
    blockStyles: {
      logic_blocks: blockStyle(C.logic),
      loop_blocks: blockStyle(C.control),
      math_blocks: blockStyle(C.math),
      text_blocks: blockStyle(C.compare),
      list_blocks: blockStyle(C.lists),
      variable_blocks: blockStyle(C.variables),
      procedure_blocks: blockStyle(C.functions),
      colour_blocks: blockStyle(C.data)
    },
    fontStyle: { family: 'Segoe UI, Tahoma, sans-serif', size: 11, weight: 'normal' }
  };
  if (base) config.base = base;
  try { return Blockly.Theme.defineTheme('combustor', config); }
  catch (e) { console.warn('theme', e); return undefined; }
}

function initBlockly() {
  if (workspace) return;
  defineBlocks();

  const options = {
    toolbox: buildToolbox(),
    renderer: 'zelos',
    theme: makeTheme(),
    grid: { spacing: 24, length: 2, colour: '#232937', snap: true },
    zoom: { controls: true, wheel: true, startScale: 0.8, maxScale: 1.7, minScale: 0.3, scaleSpeed: 1.1, pinch: true },
    move: { scrollbars: true, drag: true, wheel: true },
    trashcan: true,
    sounds: false,
    media: 'blockly/media/'
  };

  workspace = Blockly.inject('blocklyDiv', options);
  workspace.addChangeListener(onWorkspaceChange);

  window.addEventListener('resize', () => {
    if (workspace) Blockly.svgResize(workspace);
  });

  setTimeout(() => Blockly.svgResize(workspace), 100);
}

function onWorkspaceChange(ev) {
  if (booting) return;
  if (!ev || ev.isUiEvent) return;
  if (ev.type === Blockly.Events.VIEWPORT_CHANGE) return;

  clearTimeout(generateTimer);
  generateTimer = setTimeout(() => {
    generateNow();
    scheduleValidate();
    scheduleAutosave();
  }, 180);
}

/* ------------------------------------------------------------------ */
/* Generate / validate / run                                          */
/* ------------------------------------------------------------------ */

function generateNow() {
  if (!workspace) return null;
  try {
    const result = generate(workspace);
    lastCode = result.code;
    lineMap = result.lineMap;
    renderCode(result.code, lastErrorLine);
    $('codeStatus').textContent = t('code.generated', result.code.split('\n').length);
    $('codeStatus').style.color = 'var(--muted)';
    if (result.warnings.length) {
      $('statusLeft').textContent = result.warnings[0];
      $('statusLeft').className = 'warn';
    }
    return result;
  } catch (e) {
    console.error(e);
    lastCode = '';
    lineMap = [];
    $('code').innerHTML = '<div class="cl"><span class="ln">!</span><span class="lc">' + t('code.generateError', esc(e.message)) + '</span></div>';
    $('codeStatus').textContent = t('code.generationFailed');
    $('codeStatus').style.color = 'var(--red)';
    return null;
  }
}

function renderCode(code, errorLine) {
  const el = $('code');
  const lines = (code || '').split('\n');
  let html = '';
  for (let i = 0; i < lines.length; i++) {
    const n = i + 1;
    const cls = 'cl' + (errorLine === n ? ' error' : '');
    html += '<div class="' + cls + '" data-line="' + n + '"><span class="ln">' + n +
      '</span><span class="lc">' + esc(lines[i]) + '</span></div>';
  }
  el.innerHTML = html;
  if (errorLine) {
    const target = el.querySelector('.cl.error');
    if (target) target.scrollIntoView({ block: 'center' });
  }
}

function setErrorLine(n) {
  lastErrorLine = n || 0;
  const el = $('code');
  if (!el) return;
  el.querySelectorAll('.cl.error').forEach(x => x.classList.remove('error'));
  if (n > 0) {
    const target = el.querySelector('.cl[data-line="' + n + '"]');
    if (target) {
      target.classList.add('error');
      target.scrollIntoView({ block: 'center' });
    }
  }
}

function scheduleValidate() {
  clearTimeout(validateTimer);
  validateTimer = setTimeout(validateNow, 650);
}

async function validateNow() {
  if (!lastCode) return;
  if (!connected) {
    $('codeStatus').textContent = t('code.offlineGenerated');
    return;
  }
  try {
    const res = await api('/api/validate', { method: 'POST', body: JSON.stringify({ code: lastCode }) });
    if (res.ok) {
      $('codeStatus').textContent = t('code.valid');
      $('codeStatus').style.color = 'var(--green)';
      setErrorLine(0);
    } else {
      $('codeStatus').textContent = res.line ? t('code.line', res.line) : t('code.syntaxError');
      $('codeStatus').style.color = 'var(--red)';
      setErrorLine(res.line || 0);
    }
  } catch (e) {
    $('codeStatus').textContent = t('code.cantValidate', e.message);
    $('codeStatus').style.color = 'var(--amber)';
  }
}

async function runProgram() {
  const result = generateNow();
  if (!result) return;
  setErrorLine(0);
  $('statusLeft').textContent = t('status.running');
  $('statusLeft').className = '';
  if (!connected) {
    toast(t('run.connectFirst'));
    showOverlay(t('status.disconnected'), t('run.connectFirst'), true);
    return;
  }
  $('btnRun').disabled = true;
  try {
    const res = await api('/api/run', { method: 'POST', body: JSON.stringify({ code: lastCode }) });
    if (res.ok) {
      running = true;
      setChip(t('status.running'), 'run');
      $('statusLeft').textContent = 'Programa en marcha';
      $('statusLeft').className = '';
      toast('Programa ejecutándose');
      switchTab('log');
      startPolling();
    } else {
      running = false;
      setChip(t('status.error'), 'err');
      $('statusLeft').textContent = res.error || t('run.failed');
      $('statusLeft').className = 'error';
      setErrorLine(res.line || 0);
      if (res.line) highlightLineBlock(res.line);
      toast(res.error || t('run.failed'), 5000);
    }
  } catch (e) {
    setChip(t('status.error'), 'err');
    $('statusLeft').textContent = e.message;
    $('statusLeft').className = 'error';
    toast(e.message, 5000);
  } finally {
    $('btnRun').disabled = false;
  }
}

async function stopProgram() {
  try {
    await api('/api/stop', { method: 'POST', body: '{}' });
    running = false;
    setChip(connected ? t('status.connected') : t('status.disconnected'), connected ? 'ok' : '');
    $('statusLeft').textContent = t('run.stopped');
    $('statusLeft').className = '';
    stopPolling();
    pollStatusOnce();
  } catch (e) {
    toast(t('run.couldNotStop', e.message));
  }
}

function highlightLineBlock(line) {
  if (!workspace || !lineMap || !lineMap[line - 1]) return;
  const id = lineMap[line - 1];
  try {
    workspace.highlightBlock(id);
    workspace.centerOnBlock(id);
  } catch (e) { }
}

/* ------------------------------------------------------------------ */
/* Status polling                                                     */
/* ------------------------------------------------------------------ */

function startPolling() {
  stopPolling();
  statusTimer = setInterval(pollStatusOnce, 900);
  pollStatusOnce();
}

function stopPolling() {
  if (statusTimer) { clearInterval(statusTimer); statusTimer = null; }
}

async function pollStatusOnce() {
  try {
    const s = await api('/api/status');
    updateStatus(s);
  } catch (e) {
    // The game may be loading; do not spam.
  }
}

function updateStatus(s) {
  running = !!s.running;
  lastErrorText = s.error || '';
  lastErrorLine = s.errorLine || 0;
  if (s.error) {
    setChip(t('status.error'), 'err');
  } else if (s.paused) {
    setChip(t('status.paused'), '');
  } else if (running) {
    setChip(s.sleeping ? t('status.sleeping') : t('status.running'), 'run');
  } else {
    setChip(connected ? t('status.connected') : t('status.disconnected'), connected ? 'ok' : '');
  }
  $('btnResume').classList.toggle('hidden', !s.paused);

  if (s.error) {
    $('statusLeft').textContent = t('status.errorLine', s.errorLine) + ': ' + s.error;
    $('statusLeft').className = 'error';
    setErrorLine(s.errorLine || 0);
    if (s.errorLine) highlightLineBlock(s.errorLine);
  } else if (!running) {
    $('statusLeft').textContent = connected ? t('status.ready') : t('status.disconnected');
    $('statusLeft').className = '';
  }

  if (s.log && s.log.length) {
    const logEl = $('log');
    let html = '';
    for (const line of s.log) {
      html += '<div class="line' + (/error|Error|línea/i.test(line) ? ' err' : '') + '">' + esc(line) + '</div>';
    }
    logEl.innerHTML = html;
    logEl.scrollTop = logEl.scrollHeight;
  }

  if (!running && s.finished) {
    setChip(t('status.finished'), 'ok');
    stopPolling();
  }
}

/* ------------------------------------------------------------------ */
/* Connect / state                                                    */
/* ------------------------------------------------------------------ */

function refreshOptions() {
  const parts = state.parts || [];
  partOptions = parts.length
    ? parts.map(p => [p.name, p.name])
    : [['valve_1', 'valve_1'], ['sensor_1', 'sensor_1'], ['injector_1', 'injector_1']];

  const gases = state.gases || [];
  gasOptions = gases.length
    ? gases.map(g => [g.name, g.display || g.name])
    : [['Fuel', 'Fuel'], ['Oxidizer', 'Oxidizer'], ['Air', 'Air']];
}

async function connect(silent) {
  if (!silent) showOverlay(t('overlay.connectingTitle'), t('overlay.connectingText'), false);
  try {
    const res = await api('/api/connect', { method: 'POST', body: '{}' });
    if (!res.ok) throw new Error(res.error || 'Sin respuesta');
    state = res;
    connected = !!state.connected;
    refreshOptions();

    if (!workspace) initBlockly();
    else workspace.updateToolbox(buildToolbox());

    renderParts();
    updateConnectionChip();
    hideOverlay();
    generateNow();
    scheduleValidate();

    if (!state.inGame) {
      toast(t('connect.noEngine'), 4800);
    } else {
      toast(t('connect.toast', (state.parts || []).length, (state.gases || []).length));
    }
  } catch (e) {
    connected = false;
    updateConnectionChip();
    hideOverlay();
    if (!silent) {
      showOverlay(t('overlay.failedTitle'), t('overlay.failedText', e.message), true);
    }
  }
}

function updateConnectionChip() {
  if (running) { setChip(t('status.running'), 'run'); return; }
  if (!connected) { setChip(t('status.disconnected'), 'err'); return; }
  if (!state.inGame) { setChip(t('status.noEngine'), ''); return; }
  setChip(t('status.connected'), 'ok');
  $('statusRight').textContent = t('status.partsGases', (state.parts || []).length, (state.gases || []).length) +
    (state.ship ? ' · ' + t('status.ship') : '') + (state.shop ? ' · ' + t('status.shop') : '');
}

/* ------------------------------------------------------------------ */
/* Panels                                                             */
/* ------------------------------------------------------------------ */

function renderParts() {
  const el = $('parts');
  const parts = state.parts || [];
  $('partsTitle').textContent = t('parts.title', parts.length);
  if (!parts.length) {
    el.innerHTML = '<div class="empty">' + esc(t('parts.empty')) + '</div>';
    return;
  }
  let html = '';
  for (const p of parts) {
    const kindMap = {
      Valve: 'kind.Valve', Injector: 'kind.Injector', Igniter: 'kind.Igniter',
      Hole: 'kind.Hole', Fan: 'kind.Fan', Filter: 'kind.Filter',
      Gate: 'kind.Gate', Piston: 'kind.Piston', Sensor: 'kind.Sensor'
    };
    const kind = kindMap[p.kind] ? t(kindMap[p.kind]) : (p.kindLabel || p.kind);
    html += '<div class="part"><div class="pname"><span>' + esc(p.name) + '</span><span class="pkind">' +
      esc(kind) + '</span></div><div class="members">';
    const seen = new Set();
    for (const m of (p.members || [])) {
      if (seen.has(m.name)) continue;
      seen.add(m.name);
      const cls = m.method ? 'method' : (m.write ? 'write' : '');
      html += '<span class="mchip ' + cls + '" title="' + esc(LANG === 'es' ? (m.doc || '') : '') + '">' + esc(m.name) + '</span>';
    }
    html += '</div></div>';
  }
  el.innerHTML = html;
}

function switchTab(name) {
  document.querySelectorAll('.tab').forEach(t => t.classList.toggle('active', t.dataset.tab === name));
  document.querySelectorAll('.tabpane').forEach(p => p.classList.toggle('active', p.id === 'tab-' + name));
}

function showOverlay(title, text, showConnect) {
  $('ovTitle').textContent = title;
  $('ovText').textContent = text || '';
  $('ovConnect').style.display = showConnect ? '' : 'none';
  $('ovClose').style.display = showConnect ? '' : 'none';
  $('overlay').classList.remove('hidden');
}

function hideOverlay() {
  $('overlay').classList.add('hidden');
}

/* ------------------------------------------------------------------ */
/* Persistence                                                        */
/* ------------------------------------------------------------------ */

function projectJson() {
  return {
    version: 1,
    app: 'CombustorBlocks',
    name: projectName,
    savedAt: new Date().toISOString(),
    workspace: Blockly.serialization.workspaces.save(workspace)
  };
}

async function saveProject() {
  if (!workspace) return;
  const project = projectJson();
  try {
    await api('/api/project', { method: 'POST', body: JSON.stringify(project) });
    localStorage.setItem('cb.project', JSON.stringify(project));
    toast(t('save.saved', 'UserData/CombustorBlocks/project.json'));
  } catch (e) {
    localStorage.setItem('cb.project', JSON.stringify(project));
    toast(t('save.savedBrowser', e.message), 4200);
  }
}

function scheduleAutosave() {
  clearTimeout(autosaveTimer);
  autosaveTimer = setTimeout(() => {
    try {
      if (workspace) localStorage.setItem('cb.project', JSON.stringify(projectJson()));
    } catch (e) { }
  }, 1200);
}

async function loadProject(silent) {
  let project = null;
  try {
    const res = await api('/api/project');
    if (res && res.workspace) project = res;
  } catch (e) { }

  if (!project) {
    try {
      const raw = localStorage.getItem('cb.project');
      if (raw) project = JSON.parse(raw);
    } catch (e) { }
  }

  if (project && project.workspace) {
    try {
      workspace.clear();
      Blockly.serialization.workspaces.load(project.workspace, workspace);
      projectName = project.name || 'Mi programa';
      $('projectName').textContent = projectName;
      generateNow();
      scheduleValidate();
      if (!silent) toast(t('load.opened'));
      return true;
    } catch (e) {
      console.warn('load project', e);
    }
  }

  loadStarter();
  return false;
}

function loadStarter() {
  const es = LANG === 'es';
  const target = es ? 'temperatura_objetivo' : 'target_temperature';
  projectName = es ? 'Temperatura del motor' : 'Engine temperature';
  $('projectName').textContent = projectName;

  const sensor = firstPart('Sensor');
  const valve = firstPart('Valve');
  const dataPart = sensor || valve;
  const part = dataPart || 'sensor_1';
  const started = es ? 'Programa iniciado' : 'Program started';
  const place = es ? 'Coloca una válvula o un sensor y pulsa Conectar' : 'Place a valve or sensor and click Connect';
  const high = es ? 'Temperatura demasiado alta' : 'Temperature too high';
  const ok = es ? 'Temperatura dentro del límite' : 'Temperature within limit';

  const startBlocks = `
      <block type="variables_set">
        <field name="VAR">${target}</field>
        <value name="VALUE"><block type="math_number"><field name="NUM">800</field></block></value>
        <next>
          <block type="cb_print">
            <value name="VALUE"><block type="text"><field name="TEXT">${dataPart ? started : place}</field></block></value>
          </block>
        </next>
      </block>`;

  const updateBlock = dataPart ? `
  <block type="cb_when_update" x="40" y="290">
    <next>
      <block type="controls_if">
        <value name="IF0">
          <block type="logic_compare">
            <field name="OP">GT</field>
            <value name="A"><block type="cb_get"><field name="PART">${part}</field><field name="MEMBER">temperature</field></block></value>
            <value name="B"><block type="variables_get"><field name="VAR">${target}</field></block></value>
          </block>
        </value>
        <statement name="DO0">
          <block type="cb_print">
            <value name="VALUE"><block type="text"><field name="TEXT">${high}</field></block></value>
          </block>
        </statement>
        <statement name="ELSE">
          <block type="cb_print">
            <value name="VALUE"><block type="text"><field name="TEXT">${ok}</field></block></value>
          </block>
        </statement>
      </block>
    </next>
  </block>` : '';

  const xml = `
<xml xmlns="https://developers.google.com/blockly/xml">
  <variables>
    <variable>${target}</variable>
  </variables>
  <block type="cb_when_start" x="40" y="40">
    <next>${startBlocks}
    </next>
  </block>${updateBlock}
</xml>`;

  try {
    workspace.clear();
    const dom = new DOMParser().parseFromString(xml, 'text/xml').documentElement;
    Blockly.Xml.domToWorkspace(dom, workspace);
    generateNow();
    scheduleValidate();
  } catch (e) {
    console.error('starter', e);
  }
}

/* ------------------------------------------------------------------ */
/* Language                                                           */
/* ------------------------------------------------------------------ */

async function detectLanguage() {
  const saved = localStorage.getItem('cb.lang') || 'auto';
  if (saved === 'es' || saved === 'en') return saved;

  // Auto: the browser language wins; the game language is the fallback for
  // browsers in a language the editor does not speak.
  const nav = String(navigator.language || 'en').toLowerCase();
  if (nav.indexOf('es') === 0) return 'es';
  if (nav.indexOf('en') === 0) return 'en';

  let game = '';
  try {
    const res = await fetch('/api/lang').then(function (r) { return r.json(); });
    if (res && res.ok) game = String(res.language || '').toLowerCase();
  } catch (e) { }

  if (game.indexOf('es') === 0) return 'es';
  if (game.indexOf('en') === 0) return 'en';
  return 'en';
}

function loadBlocklyMessages(lang) {
  if (lang !== 'es') return Promise.resolve();
  return new Promise(function (resolve) {
    const script = document.createElement('script');
    script.src = 'blockly/es.js';
    script.onload = function () { resolve(); };
    script.onerror = function () { resolve(); };
    document.head.appendChild(script);
  });
}

function applyStaticTranslations() {
  document.documentElement.lang = LANG;

  const map = {
    '#btnConnect': 'btn.connect',
    '#btnRun': 'btn.run',
    '#btnStop': 'btn.stop',
    '#btnSave': 'btn.save',
    '#btnLoad': 'btn.open',
    '#btnNew': 'btn.new',
    '#btnCode': 'btn.code',
    '#btnHelp': 'btn.help',
    '#btnCopy': 'btn.copy',
    '#btnClearLog': 'btn.clear',
    '#btnRefreshParts': 'btn.refresh',
    '#btnResume': 'btn.resume',
    '#btnAiSettings': 'btn.settings',
    '#btnAiSave': 'btn.save',
    '#btnAiSend': 'btn.ask',
    '#tab-log .panehead span': 'pane.gameOutput',
    '#tab-ai .panehead span': 'pane.ai'
  };
  Object.keys(map).forEach(function (sel) {
    const el = document.querySelector(sel);
    if (el) el.textContent = t(map[sel]);
  });

  document.querySelectorAll('.tab').forEach(function (b) {
    const key = { code: 'tab.code', log: 'tab.log', parts: 'tab.parts', ai: 'tab.ai' }[b.dataset.tab];
    if (key) b.textContent = t(key);
  });

  document.querySelectorAll('[data-i18n]').forEach(function (el) {
    el.textContent = t(el.getAttribute('data-i18n'));
  });

  const quick = {
    temperatura: 'ai.quickTemp',
    valvula: 'ai.quickValve',
    contador: 'ai.quickCounter',
    aleatorio: 'ai.quickRandom',
    error: 'ai.quickError'
  };
  document.querySelectorAll('.ai-quick button').forEach(function (b) {
    if (quick[b.dataset.ai]) b.textContent = t(quick[b.dataset.ai]);
  });

  const providerText = { builtin: 'ai.builtin', ollama: 'ai.ollama', openai: 'ai.openai' };
  document.querySelectorAll('#aiProvider option').forEach(function (o) {
    if (providerText[o.value]) o.textContent = t(providerText[o.value]);
  });

  if ($('aiNote')) $('aiNote').textContent = t('ai.note');
  if ($('aiPrompt')) $('aiPrompt').placeholder = t('ai.placeholder');
  if ($('log')) $('log').innerHTML = '<div class="empty">' + esc(t('log.empty')) + '</div>';
  if ($('parts')) $('parts').innerHTML = '<div class="empty">' + esc(t('parts.disconnected')) + '</div>';
  if ($('partsTitle')) $('partsTitle').textContent = t('pane.engineParts');
  if ($('statusLeft')) $('statusLeft').textContent = t('status.ready');
  if ($('statusRight')) $('statusRight').textContent = t('status.offline');
  if ($('chip')) setChip(t('status.offline'), '');
  if ($('btnConnect')) $('btnConnect').title = t('btn.connectTitle');
  if ($('btnCode')) $('btnCode').title = t('btn.codeTitle');
  if ($('codeStatus')) $('codeStatus').textContent = t('code.notGenerated');
  if ($('btnUndo')) $('btnUndo').title = t('btn.undoTitle');
  if ($('btnRedo')) $('btnRedo').title = t('btn.redoTitle');
  if ($('ovConnect')) $('ovConnect').textContent = t('overlay.connect');
  if ($('ovClose')) $('ovClose').textContent = t('overlay.offline');

  if ($('help')) {
    const title = $('help').querySelector('h2');
    if (title) title.textContent = t('help.title');
    const body = $('help').querySelector('.helpbody');
    if (body) body.innerHTML = t('help.body');
  }
  if ($('helpClose')) $('helpClose').textContent = t('btn.close');

  const langSelect = $('langSelect');
  if (langSelect) langSelect.value = localStorage.getItem('cb.lang') || 'auto';
}

async function changeLanguage(value) {
  localStorage.setItem('cb.lang', value);
  if (workspace) {
    try { localStorage.setItem('cb.project', JSON.stringify(projectJson())); } catch (e) { }
    try { await api('/api/project', { method: 'POST', body: JSON.stringify(projectJson()) }); } catch (e) { }
  }
  location.reload();
}

/* ------------------------------------------------------------------ */
/* AI assistant                                                       */
/* ------------------------------------------------------------------ */

function initAi() {
  try {
    const raw = localStorage.getItem('cb.ai');
    if (raw) aiSettings = Object.assign(aiSettings, JSON.parse(raw));
  } catch (e) { }

  $('aiProvider').value = aiSettings.provider || 'builtin';
  $('aiUrl').value = aiSettings.url || 'http://127.0.0.1:11434';
  $('aiModel').value = aiSettings.model || 'llama3.1';
  $('aiKey').value = aiSettings.apiKey || '';
  $('aiStatus').textContent = aiSettings.provider === 'builtin'
    ? t('ai.statusBuiltin')
    : aiSettings.provider + ' · ' + aiSettings.model;

  $('btnAiSettings').addEventListener('click', function () {
    $('aiSettings').classList.toggle('hidden');
  });
  $('btnAiSave').addEventListener('click', saveAiSettings);
  $('btnAiSend').addEventListener('click', function () { sendAi($('aiPrompt').value); });
  $('aiPrompt').addEventListener('keydown', function (e) {
    if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      sendAi($('aiPrompt').value);
    }
  });
  $('aiProvider').addEventListener('change', function () {
    const p = $('aiProvider').value;
    if (p === 'ollama') {
      $('aiUrl').value = 'http://127.0.0.1:11434';
      $('aiModel').value = 'llama3.1';
    } else if (p === 'openai') {
      $('aiUrl').value = 'https://api.openai.com';
      $('aiModel').value = 'gpt-4o-mini';
    }
  });
  document.querySelectorAll('.ai-quick button').forEach(function (b) {
    b.addEventListener('click', function () {
      const kind = b.dataset.ai;
      if (kind === 'error') sendAi(LANG === 'es' ? 'Explica el error actual' : 'Explain the current error');
      else sendAi(b.textContent);
    });
  });

  const mode = aiSettings.provider === 'builtin'
    ? t('ai.builtin')
    : aiSettings.provider + ' · ' + aiSettings.model;
  appendAi('system', t('ai.welcomeMode', mode));
  appendAi('system', t('ai.welcome'));
  appendAi('system', t('ai.welcomeReplace'));
}

function saveAiSettings() {
  aiSettings.provider = $('aiProvider').value;
  aiSettings.url = $('aiUrl').value.trim() || 'http://127.0.0.1:11434';
  aiSettings.model = $('aiModel').value.trim() || 'llama3.1';
  aiSettings.apiKey = $('aiKey').value.trim();
  try { localStorage.setItem('cb.ai', JSON.stringify(aiSettings)); } catch (e) { }
  $('aiStatus').textContent = t('ai.statusSaved', aiSettings.provider === 'builtin'
    ? t('ai.statusBuiltin')
    : aiSettings.model);
  toast(t('ai.saved'));
}

function aiSystemPrompt() {
  const parts = (state.parts || []).map(function (p) {
    const ms = (p.members || []).map(function (m) { return m.name + (m.method ? '()' : ''); }).join(', ');
    return '- ' + p.name + ' (' + (p.kindLabel || p.kind) + '): ' + ms;
  }).join('\n') || '- (no parts connected; use sensor_1 / valve_1 as placeholders and say so)';

  return 'You are an expert programmer for Combustor, a 2D engine and chemistry sandbox.\n' +
    'You write programs in the real GS language of the game. Return ONLY raw GS code, with no explanations and no markdown fences.\n' +
    'GS syntax: 4-space indentation, # comments, if/elif/else, while, for i in range(n), def/return, break, continue, pass, lists, indexing, and/or/not, == != < > <= >=, + - * / %, print(), wait(seconds), time(), stop(), len(), abs(), floor(), ceil(), round(), sqrt(), clamp(), min(), max(), int(), float(), str(), pi.\n' +
    'One pass of a while loop is one simulation tick. Use wait(seconds) to slow down.\n' +
    'There is no random() and no pow(). Do not invent functions or parts.\n' +
    'Only use these real parts and members:\n' + parts + '\n' +
    'The code is inserted at the top level of a program. Include your own while True loop if it must run continuously.\n' +
    'Current program (context, may be empty):\n' + (lastCode || '(empty)').slice(0, 3000);
}

function explainErrorText() {
  const err = lastErrorText || '';
  const line = lastErrorLine || 0;
  if (!err) {
    return t('ai.noError');
  }
  let hint = '';
  const missing = err.match(/Nothing is called '([^']+)'/i);
  if (missing) {
    hint = t('ai.errorMissing', missing[1]);
  } else if (/not allowed/i.test(err)) {
    hint = t('ai.errorAllowed');
  } else if (/has nothing called|no_attr/i.test(err)) {
    hint = t('ai.errorMember');
  } else if (/Division by zero/i.test(err)) {
    hint = t('ai.errorDiv');
  } else if (/Wrong number of inputs/i.test(err)) {
    hint = t('ai.errorArgs');
  } else {
    hint = t('ai.errorGeneric');
  }
  const prefix = line ? t('ai.errorPrefix', line) : 'Error';
  return prefix + ': ' + err + '\n\n' + hint;
}

function localAssistant(prompt) {
  const q = String(prompt || '').toLowerCase();
  const es = LANG === 'es';
  const sensor = firstPart('Sensor') || 'sensor_1';
  const valve = firstPart('Valve') || 'valve_1';
  const noParts = !(state.parts && state.parts.length);
  const note = noParts ? t('ai.noParts') + '\n\n' : '';

  if (/error|fallo|falla/.test(q)) {
    return { text: explainErrorText(), code: '' };
  }
  if (/explica|explain|que hace|qué hace|what does/.test(q)) {
    const sel = (typeof Blockly.getSelected === 'function') ? Blockly.getSelected() : null;
    if (sel) {
      return { text: t('ai.selectedBlock', sel.type) + '\n' + (sel.getTooltip ? sel.getTooltip() : ''), code: '' };
    }
    return { text: t('ai.selectBlock'), code: '' };
  }
  if (/temperatura|temperature|sensor|calor|alarma|heat/.test(q)) {
    const target = es ? 'objetivo' : 'target';
    const high = es ? 'Temperatura alta:' : 'Temperature high:';
    const ok = es ? 'Temperatura OK:' : 'Temperature OK:';
    return {
      text: note + t('ai.tempText'),
      code: target + ' = 800\nwhile True:\n    t = ' + sensor + '.temperature\n    if t > ' + target + ':\n        print("' + high + '", t)\n    else:\n        print("' + ok + '", t)\n    wait(0.25)\n'
    };
  }
  if (/v[aá]lvula|valve|presi[oó]n|pressure/.test(q)) {
    return {
      text: note + t('ai.valveText'),
      code: 'while True:\n    if ' + valve + '.pressure > 5:\n        ' + valve + '.open()\n    else:\n        ' + valve + '.close()\n    wait(0.25)\n'
    };
  }
  if (/contador|counter|cuenta|count/.test(q)) {
    const v = es ? 'contador' : 'counter';
    return {
      text: t('ai.counterText'),
      code: v + ' = 0\nwhile True:\n    ' + v + ' = ' + v + ' + 1\n    print("' + v + '", ' + v + ')\n    wait(1)\n'
    };
  }
  if (/aleatorio|random|azar/.test(q)) {
    const seed = es ? 'semilla' : 'seed';
    const label = es ? 'aleatorio' : 'random';
    return {
      text: t('ai.randomText'),
      code: seed + ' = 12345\nwhile True:\n    ' + seed + ' = (' + seed + ' * 48271) % 2147483647\n    print("' + label + '", ' + seed + ' % 100)\n    wait(1)\n'
    };
  }
  return { text: t('ai.helpText'), code: '' };
}

function extractAiCode(text) {
  if (!text) return '';
  const s = String(text);
  const m = s.match(/```(?:gs|python)?\s*\n?([\s\S]*?)```/i);
  if (m) return m[1].trim();
  if (/(^|\n)\s*(while|if|for|def|print|wait|stop)\b/.test(s)) return s.trim();
  return '';
}

function aiMarkup(text) {
  let html = esc(String(text || ''));
  html = html.replace(/```(?:gs|python)?\s*\n?([\s\S]*?)```/g, function (_, code) {
    return '<pre>' + code.replace(/^\n+|\n+$/g, '') + '</pre>';
  });
  html = html.replace(/\*\*(.+?)\*\*/g, '<b>$1</b>');
  return html;
}

function appendAi(role, text, code) {
  const chat = $('aiChat');
  if (!chat) return;
  const div = document.createElement('div');
  div.className = 'ai-msg ' + role;
  div.innerHTML = aiMarkup(text);
  if (code) {
    const btn = document.createElement('button');
    btn.className = 'mini ai-insert';
    btn.textContent = t('ai.insert');
    btn.title = t('ai.insertTitle');
    btn.addEventListener('click', function () { insertAiCode(code); });
    div.appendChild(btn);
  }
  chat.appendChild(div);
  chat.scrollTop = chat.scrollHeight;
}

function insertAiCode(code) {
  const clean = String(code || '').replace(/\r/g, '').replace(/\t/g, '    ').trim();
  if (!clean) { toast(t('ai.noCode')); return; }
  if (!workspace) return;
  if (workspace.getAllBlocks(false).length > 0 &&
      !confirm(t('ai.replaceConfirm'))) {
    return;
  }
  try {
    workspace.clear();
    const hat = workspace.newBlock('cb_when_start');
    hat.initSvg(); hat.render(); hat.moveBy(40, 40);
    const raw = workspace.newBlock('cb_gs_code');
    raw.initSvg(); raw.render();
    raw.setFieldValue(clean, 'CODE');
    hat.nextConnection.connect(raw.previousConnection);
    workspace.centerOnBlock(raw.id);
    generateNow();
    scheduleValidate();
    switchTab('code');
    toast(t('ai.inserted'));
  } catch (e) {
    toast(t('ai.couldNotInsert', e.message));
  }
}

async function sendAi(prompt) {
  const p = String(prompt || '').trim();
  if (!p || aiBusy) return;
  aiBusy = true;
  $('btnAiSend').disabled = true;
  $('aiPrompt').value = '';
  appendAi('user', p);
  switchTab('ai');
  try {
    if (aiSettings.provider === 'builtin') {
      const r = localAssistant(p);
      appendAi('ai', r.text, r.code);
      return;
    }
    appendAi('system', t('ai.consulting', aiSettings.provider, aiSettings.model));
    const res = await api('/api/ai', {
      method: 'POST',
      body: JSON.stringify({
        provider: aiSettings.provider,
        url: aiSettings.url,
        model: aiSettings.model,
        apiKey: aiSettings.apiKey,
        messages: [
          { role: 'system', content: aiSystemPrompt() },
          { role: 'user', content: p }
        ]
      })
    });
    if (!res.ok) throw new Error(res.error || 'AI error');
    const text = res.text || '';
    if (!text.trim()) throw new Error('respuesta vacía');
    const code = extractAiCode(text);
    appendAi('ai', text, code);
  } catch (e) {
    const r = localAssistant(p);
    appendAi('system', t('ai.unavailable', e.message));
    appendAi('ai', r.text, r.code);
  } finally {
    aiBusy = false;
    $('btnAiSend').disabled = false;
  }
}

/* ------------------------------------------------------------------ */
/* UI wiring                                                          */
/* ------------------------------------------------------------------ */

function wireUi() {
  $('btnConnect').addEventListener('click', () => connect(false));
  $('btnRun').addEventListener('click', runProgram);
  $('btnStop').addEventListener('click', stopProgram);
  $('btnSave').addEventListener('click', saveProject);
  $('btnLoad').addEventListener('click', () => loadProject(false));
  $('btnNew').addEventListener('click', () => {
    if (!confirm(t('project.newConfirm'))) return;
    try { localStorage.setItem('cb.project.backup', JSON.stringify(projectJson())); } catch (e) { }
    localStorage.removeItem('cb.project');
    loadStarter();
    toast(t('project.newToast'));
  });
  $('btnUndo').addEventListener('click', () => workspace && workspace.undo(false));
  $('btnRedo').addEventListener('click', () => workspace && workspace.undo(true));
  $('btnCode').addEventListener('click', () => {
    $('side').classList.toggle('hidden');
    setTimeout(() => workspace && Blockly.svgResize(workspace), 60);
  });
  $('btnHelp').addEventListener('click', () => $('help').classList.remove('hidden'));
  $('helpClose').addEventListener('click', () => $('help').classList.add('hidden'));
  $('ovConnect').addEventListener('click', () => connect(false));
  $('ovClose').addEventListener('click', hideOverlay);
  $('btnCopy').addEventListener('click', async () => {
    try {
      await navigator.clipboard.writeText(lastCode);
      toast(t('code.copied'));
    } catch (e) { toast(t('code.copyFail')); }
  });
  $('btnResume').addEventListener('click', async () => {
    try {
      await api('/api/resume', { method: 'POST', body: '{}' });
      toast(t('status.resumed'));
    } catch (e) { toast(t('status.resumeFailed', e.message)); }
  });
  $('btnClearLog').addEventListener('click', () => {
    $('log').innerHTML = '<div class="empty">' + esc(t('log.empty')) + '</div>';
  });
  $('btnRefreshParts').addEventListener('click', () => connect(false));

  document.querySelectorAll('.tab').forEach(t => {
    t.addEventListener('click', () => switchTab(t.dataset.tab));
  });

  if ($('langSelect')) {
    $('langSelect').addEventListener('change', function () { changeLanguage(this.value); });
  }

  document.addEventListener('keydown', (e) => {
    if (e.ctrlKey && e.key === 'Enter') { e.preventDefault(); runProgram(); }
    if (e.key === 'F7') { e.preventDefault(); connect(false); }
  });

  // Clicking a code line selects its block.
  $('code').addEventListener('click', (e) => {
    const line = e.target.closest('.cl');
    if (!line) return;
    const n = Number(line.dataset.line);
    if (n && lineMap[n - 1] && workspace) {
      workspace.highlightBlock(lineMap[n - 1]);
      workspace.centerOnBlock(lineMap[n - 1]);
    }
  });
}

/* ------------------------------------------------------------------ */
/* Boot                                                               */
/* ------------------------------------------------------------------ */

async function boot() {
  wireUi();
  LANG = await detectLanguage();
  await loadBlocklyMessages(LANG);
  applyStaticTranslations();
  initBlockly();
  initAi();
  await setTimeout(0);
  await connect(true);
  await loadProject(true);
  booting = false;
  generateNow();
  scheduleValidate();
  pollStatusOnce();
  setInterval(pollStatusOnce, 3000);
}

window.addEventListener('DOMContentLoaded', boot);
