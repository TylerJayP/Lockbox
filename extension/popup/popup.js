// The Lockbox toolbar popup.
"use strict";
const api = globalThis.browser ?? globalThis.chrome;
const app = document.getElementById("app");
const vaultPill = document.getElementById("vault");
const settingsBtn = document.getElementById("settingsBtn");

const RELEASES = "https://github.com/TylerJayP/Lockbox/releases/latest";
const GUIDE = "https://github.com/TylerJayP/Lockbox/blob/main/docs/BROWSER.md";

const bg = (cmd, extra = {}) => api.runtime.sendMessage({ cmd, ...extra });
const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
const button = (text, cls, onClick) => { const b = el("button", "btn " + (cls || ""), text); b.addEventListener("click", onClick); return b; };
const actions = (...items) => { const d = el("div", "row-actions"); d.append(...items); return d; };
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

let tab = null;
let polling = 0;

// ───────────── states ─────────────

function show(...nodes) { app.replaceChildren(...nodes); }
function spinner() { show(el("div", "spinner")); }

function errorLine(text) { return el("p", "error", text); }

// Firefox lets people switch off an add-on's access to websites; without it Lockbox can't see or fill sign-in forms.
const SITES = { origins: ["http://*/*", "https://*/*"] };
async function hasSiteAccess() {
  try { return !api.permissions?.contains || await api.permissions.contains(SITES); } catch { return true; }
}

function showSiteAccess() {
  vaultPill.hidden = true;
  show(el("h2", null, "Allow Lockbox on websites"),
    el("p", null, "Lockbox looks for sign-in forms on the pages you visit so it can fill and save logins. Your passwords stay in the Lockbox app until you pick one to fill."),
    actions(button("Allow", "primary", async () => {
      try { await api.permissions.request(SITES); } catch { /* the browser said no */ }
      await loadTab();   // the page's address was hidden until now
      refresh();
    })));
}

async function refresh() {
  const token = ++polling;
  if (!(await hasSiteAccess())) {
    if (token === polling) showSiteAccess();
    return;
  }
  const s = await bg("status");
  if (token !== polling) return;
  vaultPill.hidden = !(s.ok && s.vault);
  vaultPill.textContent = s.vault || "";
  settingsBtn.hidden = false;
  switch (s.ok ? s.state : s.error) {
    case "unlocked": return showLogins();
    case "locked": return showLocked();
    case "not-paired": return showPairing();
    case "disabled": return showDisabled();
    case "not-running": return showNotRunning();
    case "no-host": return showNoHost();
    default: return show(el("h2", null, "Couldn't reach Lockbox"), el("p", null, s.message || "Something went wrong."),
      actions(button("Try again", "primary", refresh)));
  }
}

function showNoHost() {
  const li = (...parts) => { const item = el("li"); item.append(...parts); return item; };
  const steps = el("ol");
  steps.append(
    li("Install ", el("b", null, "Lockbox"), " on this computer."),
    li("In Lockbox, open ", el("b", null, "Settings"), " and turn on ", el("b", null, "Browser extension"), "."),
    li("Close and reopen this browser."));
  show(el("h2", null, "Set up Lockbox for this browser"),
    el("p", null, "This extension works together with the Lockbox app on your computer."),
    steps,
    actions(button("Get Lockbox", "primary", () => api.tabs.create({ url: RELEASES })), button("Try again", "", refresh)));
}

function showDisabled() {
  show(el("h2", null, "Turn on the browser extension"),
    el("p", null, "In Lockbox, open Settings and turn on “Let the Lockbox extension fill and save logins”."),
    actions(button("Show Lockbox", "primary", () => bg("show")), button("Try again", "", refresh)));
}

function showNotRunning() {
  const open = button("Open Lockbox", "primary", async () => {
    open.disabled = true;
    const r = await bg("launch");
    if (!r.ok) { app.append(errorLine(r.message || "Couldn't open Lockbox.")); open.disabled = false; return; }
    for (let i = 0; i < 20; i++) {          // wait for it to start
      await sleep(1000);
      const s = await bg("status");
      if (s.ok || s.error !== "not-running") return refresh();
    }
    open.disabled = false;
  });
  show(el("h2", null, "Lockbox isn't open"), el("p", null, "Open Lockbox and unlock your vault to fill logins here."), actions(open));
}

async function pairingCode() {
  const { clientId } = await api.storage.local.get("clientId");
  const hash = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(clientId || ""));
  const n = new DataView(hash).getUint32(0) % 1000000;
  return `${String(Math.floor(n / 1000)).padStart(3, "0")} ${String(n % 1000).padStart(3, "0")}`;
}

function browserName() {
  const ua = navigator.userAgent;
  const brands = (navigator.userAgentData?.brands || []).map((b) => b.brand).join(" ");
  if (navigator.brave) return "brave";
  if (/Edg\//.test(ua) || /Edge/.test(brands)) return "edge";
  if (/OPR\//.test(ua) || /Opera/.test(brands)) return "opera";
  if (/Vivaldi/.test(ua)) return "vivaldi";
  if (/Firefox\//.test(ua)) return "firefox";
  return "chrome";
}

async function showPairing() {
  const code = await pairingCode();
  const connect = button("Connect", "primary", async () => {
    connect.disabled = true;
    show(el("h2", null, "Approve in Lockbox"),
      el("p", null, "Switch to the Lockbox window and click Allow. Check that it shows this code:"),
      el("div", "code", code), el("div", "spinner"));
    const r = await bg("pair", { browser: browserName() });
    if (r.ok) return refresh();
    show(el("h2", null, r.error === "denied" ? "Not connected" : "Couldn't connect"),
      el("p", null, r.message || "Lockbox didn't allow the connection."),
      actions(button("Try again", "primary", refresh)));
  });
  show(el("h2", null, "Connect this browser to Lockbox"),
    el("p", null, "Lockbox will ask you to allow it. You only do this once per browser."),
    el("div", "code", code),
    actions(connect));
}

function showLocked() {
  show(el("h2", null, "Lockbox is locked"),
    el("p", null, "Unlock your vault in Lockbox, then come back here."),
    actions(button("Unlock Lockbox", "primary", () => bg("show"))));
  // Notice when you unlock.
  const token = polling;
  (async () => {
    for (let i = 0; i < 120 && token === polling; i++) {
      await sleep(1500);
      if (token !== polling) return;
      const s = await bg("status");
      if (s.ok && s.state === "unlocked") return refresh();
    }
  })();
}

// ───────────── logins ─────────────

function itemRow(login, onClick) {
  const b = el("button", "item");
  const text = el("div", "text");
  text.append(el("div", "title", login.title || "(untitled)"), el("div", "sub", login.username || (login.site ? login.site : "(no username)")));
  b.append(el("div", "avatar", (login.title || "?").trim().charAt(0).toUpperCase()), text);
  if (login.protected) b.append(el("span", "lock", "protected"));
  b.append(el("span", "fill", "Fill"));
  b.addEventListener("click", () => onClick(login));
  return b;
}

async function showLogins() {
  const page = tab?.url && /^https?:/i.test(tab.url);
  if (!page) {
    show(el("h2", null, "Nothing to fill here"), el("p", null, "Open a website's sign-in page, then click Lockbox."), generatorButton(false));
    return;
  }

  const logins = await bg("tabLogins", { tabId: tab.id });
  if (!logins.ok) return refresh();

  const siteLine = el("div", "site");
  siteLine.append("Logins for ", el("b", null, logins.site || new URL(tab.url).hostname));
  if (!logins.secure) siteLine.append(el("span", "badge", "Not secure"));

  const search = el("input");
  search.type = "search";
  search.placeholder = "Search all logins";
  search.setAttribute("aria-label", "Search all logins");
  const list = el("div", "list");
  const status = el("div");

  const renderMatches = () => {
    list.replaceChildren(...logins.matches.map((m) => itemRow(m, fill)));
    if (!logins.matches.length)
      list.replaceChildren(el("div", "empty", `No logins saved for ${logins.site}. Sign in on the page and Lockbox will offer to save it, or search below.`));
  };

  let searchToken = 0;
  search.addEventListener("input", async () => {
    const q = search.value.trim();
    const token = ++searchToken;
    if (!q) return renderMatches();
    const r = await bg("search", { query: q });
    if (token !== searchToken || !r.ok) return;
    list.replaceChildren(...r.results.map((m) => itemRow(m, fill)));
    if (!r.results.length) list.replaceChildren(el("div", "empty", "No logins match."));
    else if (r.total > r.results.length) list.append(el("div", "empty", `${r.total - r.results.length} more. Type more to narrow it down.`));
  });

  async function fill(login, opts = {}) {
    status.replaceChildren(el("p", null, login.protected ? "Enter your master password in Lockbox…" : "Filling…"));
    const r = await bg("fillTab", { tabId: tab.id, entry: login.id, ...opts });
    if (r.ok) return window.close();
    if (r.error === "other-site") return confirmOtherSite(login, r);
    status.replaceChildren(errorLine(r.message || "Couldn't fill."));
  }

  function confirmOtherSite(login, r) {
    const panel = el("div", "panel warn");
    const where = r.savedSite ? `saved for ${r.savedSite}` : "saved without a website";
    panel.append(el("h3", null, r.savedSite ? "This is a different site" : "Fill on this site?"),
      el("p", null, r.savedSite
        ? `${login.title} is ${where}, but this page is ${logins.site}. Fake sites copy real ones to steal passwords. Only continue if you're sure.`
        : `${login.title} is ${where}. Only fill it if ${logins.site} is the right site.`));
    let remember = null;
    if (!r.savedSite) {
      const label = el("label", "check");
      remember = el("input");
      remember.type = "checkbox";
      remember.checked = true;
      const t = el("span");
      t.append(`Remember ${logins.site} for this login`, el("small", null, "Next time it shows up here by itself."));
      label.append(remember, t);
      panel.append(label);
    }
    panel.append(actions(
      button("Cancel", "primary", () => status.replaceChildren()),
      button(`Fill on ${logins.site} anyway`, "", () => fill(login, { anySite: true, rememberSite: !!remember?.checked }))));
    status.replaceChildren(panel);
  }

  renderMatches();
  show(siteLine, search, list, status, generatorButton(true));
  (logins.matches.length ? list.querySelector("button") : search)?.focus();
}

function generatorButton(onPage) {
  const wrap = el("div", "note");
  const gen = el("button", "link", "Generate a strong password");
  gen.addEventListener("click", async () => {
    const r = await bg("generate");
    if (!r.ok) return wrap.append(errorLine(r.message || "Couldn't generate one."));
    const panel = el("div", "panel");
    panel.append(el("div", "generated", r.password));
    if (onPage) {
      const put = button("Put it in the new-password boxes", "primary", async () => {
        const res = await api.scripting.executeScript({
          target: { tabId: tab.id, allFrames: true },
          func: (pw) => globalThis.__lockbox?.fillNewPassword(pw) ?? { filled: false },
          args: [r.password],
        }).catch(() => []);
        if (res.some((x) => x?.result?.filled)) window.close();
        else panel.append(errorLine("No password boxes on this page."));
      });
      panel.append(actions(put), el("p", null, "After you submit the form, Lockbox offers to save it."));
    }
    wrap.replaceChildren(panel);
  });
  wrap.append(gen);
  return wrap;
}

// ───────────── settings ─────────────

async function showSettings() {
  polling++;
  const { settings: stored } = await api.storage.local.get("settings");
  const settings = { inlineIcons: true, offerToSave: true, neverSave: [], ...(stored || {}) };
  const save = () => api.storage.local.set({ settings });

  const toggle = (key, title, detail) => {
    const label = el("label", "check");
    const box = el("input");
    box.type = "checkbox";
    box.checked = !!settings[key];
    box.addEventListener("change", () => { settings[key] = box.checked; save(); });
    const t = el("span");
    t.append(title, el("small", null, detail));
    label.append(box, t);
    return label;
  };

  const never = el("div");
  const renderNever = () => {
    never.replaceChildren();
    if (!settings.neverSave.length) return;
    never.append(el("p", null, "Never offer to save on:"));
    for (const site of settings.neverSave) {
      const row = el("div", "row-actions");
      const remove = el("button", "link", "remove");
      remove.addEventListener("click", () => { settings.neverSave = settings.neverSave.filter((s) => s !== site); save(); renderNever(); });
      row.append(el("span", null, site), remove);
      never.append(row);
    }
  };
  renderNever();

  const guide = el("button", "link", "How the extension works");
  guide.addEventListener("click", () => api.tabs.create({ url: GUIDE }));

  show(el("h2", null, "Settings"),
    toggle("inlineIcons", "Show the Lockbox button in sign-in fields", "Click it to pick a login, or generate a password on sign-up forms."),
    toggle("offerToSave", "Offer to save new and changed logins", "After you sign in or sign up, Lockbox asks before saving anything."),
    never,
    el("p", "note", "To disconnect this browser, use Settings → Browser extension in the Lockbox app."),
    actions(button("Back", "primary", refresh), guide),
    el("p", null, `Shortcut: ${navigator.platform.includes("Mac") ? "⌘" : "Ctrl"}+Shift+L fills the login for the site you're on.`));
}

settingsBtn.addEventListener("click", showSettings);

// ───────────── start ─────────────

async function loadTab() {
  const forced = Number(new URLSearchParams(location.search).get("tab"));   // lets tests open the popup as a page
  [tab] = forced ? [await api.tabs.get(forced)] : await api.tabs.query({ active: true, currentWindow: true });
}

(async () => {
  spinner();
  await loadTab();
  refresh();
})();
