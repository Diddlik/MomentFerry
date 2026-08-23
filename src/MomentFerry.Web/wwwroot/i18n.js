/* MomentFerry Console — user interface language.
   Keys are the English source strings, so any string without a translation
   renders in English instead of breaking.

   Adding a language: copy wwwroot/i18n/de.js to <code>.js, translate the
   values, and add [code, native name] to LANGUAGES. Nothing else changes.
--------------------------------------------------------------------- */

const LANGUAGES = [
  ['en', 'English'],
  ['de', 'Deutsch'],
  ['ru', 'Русский'],
  ['pl', 'Polski'],
  ['it', 'Italiano'],
  ['fr', 'Français'],
  ['uk', 'Українська']
];

const LANGUAGE_STORAGE_KEY = 'momentferry.language';

window.MF_MESSAGES = {};

function pickLanguage() {
  const codes = LANGUAGES.map(([code]) => code);
  try {
    const stored = localStorage.getItem(LANGUAGE_STORAGE_KEY);
    if (codes.includes(stored)) return stored;
  } catch {}
  for (const tag of navigator.languages || [navigator.language || 'en']) {
    const code = String(tag).slice(0, 2).toLowerCase();
    if (codes.includes(code)) return code;
  }
  return 'en';
}

const MF_LANG = pickLanguage();
document.documentElement.lang = MF_LANG;

// Written into the parser so the dictionary is in place before app.js renders.
if (MF_LANG !== 'en') {
  document.write(`<script src="/i18n/${MF_LANG}.js"><\/script>`);
}

/** Translate a source string. Placeholders are written as {{name}}. */
window.t = function (key, params) {
  const text = window.MF_MESSAGES[key] || key;
  return params
    ? text.replace(/\{\{(\w+)\}\}/g, (match, name) => (name in params ? params[name] : match))
    : text;
};

function normalize(value) {
  return value.replace(/\s+/g, ' ').trim();
}

/** Static markup carries no translation attributes: its own text is the key. */
function translateTextNodes() {
  const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, {
    acceptNode: node => /^(SCRIPT|STYLE|PRE)$/.test(node.parentNode.nodeName)
      ? NodeFilter.FILTER_REJECT
      : NodeFilter.FILTER_ACCEPT
  });
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const key = normalize(node.nodeValue);
    const translated = key && window.MF_MESSAGES[key];
    if (!translated) continue;
    const [, lead] = node.nodeValue.match(/^(\s*)/);
    const [, trail] = node.nodeValue.match(/(\s*)$/);
    node.nodeValue = `${lead}${translated}${trail}`;
  }
}

function translateAttributes() {
  ['placeholder', 'title', 'aria-label'].forEach(attribute => {
    document.querySelectorAll(`[${attribute}]`).forEach(element => {
      const translated = window.MF_MESSAGES[normalize(element.getAttribute(attribute))];
      if (translated) element.setAttribute(attribute, translated);
    });
  });
}

/** Called once from app.js after the document is parsed. */
window.initI18n = function () {
  translateTextNodes();
  translateAttributes();

  const select = document.getElementById('languageSelect');
  select.innerHTML = LANGUAGES
    .map(([code, label]) => `<option value="${code}"${code === MF_LANG ? ' selected' : ''}>${label}</option>`)
    .join('');
  select.addEventListener('change', () => {
    try { localStorage.setItem(LANGUAGE_STORAGE_KEY, select.value); } catch {}
    // Every renderer reads its strings at render time, so a reload is the whole switch.
    window.location.reload();
  });
};
