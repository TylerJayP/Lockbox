// Lockbox extension: background worker.
//
// It keeps one connection to Lockbox on this computer (native messaging → Lockbox.BrowserHost → Lockbox),
// and does the fills. It never stores passwords. It only holds one briefly: while passing it to a page, or a
// login you just submitted, until you answer "Save to Lockbox?" (at most two minutes).
//
// Who may ask what:
//  - The extension's own pages (popup, the in-page menu and save bar, which are extension pages in frames)
//    may ask for anything below.
//  - Content scripts (running inside web pages) may only ask about their own frame's address, report a
//    submitted login, and open a menu or save bar. They never receive a password through a message:
//    fills go straight into the frame with scripting.executeScript, after checking the frame's site.

"use strict";
const api = globalThis.browser ?? globalThis.chrome;

const HOST = "com.tylerjayp.lockbox";
const QUICK = 8000;          // ordinary requests
const WAIT_FOR_YOU = 180000; // requests that may wait for you in Lockbox (approval, master password)
const SAVE_TTL = 120000;     // a submitted login is forgotten after this, answered or not

// ───────────── connection to Lockbox ─────────────

let port = null;
let nextId = 1;
let hostError = null;
const pending = new Map();

function connect() {
  hostError = null;
  port = api.runtime.connectNative(HOST);
  port.onMessage.addListener((msg) => {
    const done = pending.get(msg?.id);
    if (done) { pending.delete(msg.id); done(msg); }
  });
  port.onDisconnect.addListener((p) => {
    const why = p?.error?.message || api.runtime.lastError?.message || "";
    hostError = /not found|no such native application|not registered|forbidden/i.test(why) ? "no-host" : "disconnected";
    port = null;
    for (const [id, done] of pending) done({ id, ok: false, error: hostError, message: why || "Lost the connection to Lockbox." });
    pending.clear();
  });
}

async function clientId() {
  const { clientId } = await api.storage.local.get("clientId");
  if (clientId) return clientId;
  const id = crypto.randomUUID();
  await api.storage.local.set({ clientId: id });
  return id;
}

/** Sends a request to Lockbox. Always resolves: { ok: true, ... } or { ok: false, error, message }. */
async function ask(type, body = {}, timeout = QUICK) {
  const client = await clientId();
  if (!port) connect();
  const id = nextId++;
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      pending.delete(id);
      resolve({ id, ok: false, error: "timeout", message: "Lockbox didn't answer." });
    }, timeout);
    pending.set(id, (reply) => { clearTimeout(timer); resolve(reply); });
    try {
      port.postMessage({ ...body, id, type, client });
    } catch (e) {
      clearTimeout(timer);
      pending.delete(id);
      resolve({ id, ok: false, error: hostError || "disconnected", message: String(e?.message || e) });
    }
  });
}

// ───────────── settings (this browser only) ─────────────

const DEFAULT_SETTINGS = { inlineIcons: true, offerToSave: true, neverSave: [] };

async function getSettings() {
  const { settings } = await api.storage.local.get("settings");
  return { ...DEFAULT_SETTINGS, ...(settings || {}) };
}

// ───────────── filling ─────────────

/**
 * Fills a login into the tab's frames (or just the given frames). Each frame checks that it really is on
 * `site` before touching anything, so a page that navigated away in the meantime gets nothing.
 */
async function fillFrames(tabId, frameIds, creds, site) {
  const target = frameIds ? { tabId, frameIds } : { tabId, allFrames: true };
  try {
    // Pages opened before the extension was installed don't have the content script yet.
    await api.scripting.executeScript({ target, files: ["content.js"] });
  } catch { /* some frames can't be scripted (browser pages); they're skipped */ }
  let results = [];
  try {
    results = await api.scripting.executeScript({
      target,
      func: (c, s) => globalThis.__lockbox?.fill(c, s) ?? { filled: false },
      args: [creds, site],
    });
  } catch (e) {
    return { ok: false, error: "fill-failed", message: "This page can't be filled." };
  }
  const filled = results.some((r) => r?.result?.filled);
  return filled
    ? { ok: true }
    : { ok: false, error: "no-fields", message: "Couldn't find a sign-in form on this page." };
}

async function fillTab(tabId, entry, { anySite = false, rememberSite = false } = {}) {
  const tab = await api.tabs.get(tabId);
  const r = await ask("fill", { url: tab.url, entry, anySite, rememberSite }, WAIT_FOR_YOU);
  if (!r.ok) return r;
  return fillFrames(tabId, null, { username: r.username, password: r.password }, r.site);
}

// ───────────── in-page menu and save bar ─────────────
// The menu and save bar are extension pages shown in frames. A content script asks for a one-time token,
// tied to its tab and frame; the extension page then asks with that token, and the address used is the one
// the browser reported for that frame, never anything the page could set.

const tokens = new Map(); // token → { kind, tabId, frameId, url, created }

function newToken(record) {
  const token = crypto.randomUUID();
  tokens.set(token, { ...record, created: Date.now() });
  for (const [t, r] of tokens) if (Date.now() - r.created > 10 * 60000) tokens.delete(t);
  return token;
}

function tokenFor(msg, sender, kind) {
  const r = tokens.get(msg.token);
  if (!r || r.kind !== kind || sender.tab?.id !== r.tabId) return null;
  return r;
}

async function closeFrameUi(tabId, frameId, what) {
  try { await api.tabs.sendMessage(tabId, { cmd: "lockbox-close", what }, { frameId }); } catch { }
}

// Usernames typed on the first page of a two-step sign-in (Google, Microsoft…), for the "Save?" on the next page.
const typedUsernames = new Map(); // tabId → { username, host, at }
const hostOf = (u) => { try { return new URL(u).hostname; } catch { return ""; } };

// Logins you just submitted, waiting for your answer. One per tab.
const pendingSaves = new Map(); // tabId → { url, username, password, check, token, created, shown }

function pendingSaveFor(tabId) {
  const p = pendingSaves.get(tabId);
  if (p && Date.now() - p.created > SAVE_TTL) { pendingSaves.delete(tabId); return null; }
  return p;
}

async function captured(sender, msg) {
  const settings = await getSettings();
  if (!settings.offerToSave) return { ok: true };
  const url = sender.url || sender.tab?.url;
  const password = String(msg.password || "");
  let username = String(msg.username || "").trim();
  if (!username) {
    const typed = typedUsernames.get(sender.tab.id);
    if (typed && Date.now() - typed.at < 5 * 60000 && typed.host === hostOf(url)) username = typed.username;
  }
  if (!url || !password || password.length > 1024 || username.length > 512) return { ok: true };

  const check = await ask("checkSave", { url, username, password });
  if (!check.ok || check.action === "none") return { ok: true };
  if (settings.neverSave.includes(check.site)) return { ok: true };

  const tabId = sender.tab.id;
  const token = newToken({ kind: "save", tabId, frameId: 0, url });
  pendingSaves.set(tabId, { url, username, password, check, token, created: Date.now(), shown: false });
  // Single-page sign-ins don't navigate: show the bar after a moment. If the page does navigate,
  // the new page's top frame asks for it when it loads.
  setTimeout(() => offerSave(tabId), 1500);
  return { ok: true };
}

async function offerSave(tabId) {
  const p = pendingSaveFor(tabId);
  if (!p || p.shown) return;
  try { await api.tabs.sendMessage(tabId, { cmd: "lockbox-show-save", token: p.token }, { frameId: 0 }); } catch { }
}

// ───────────── messages ─────────────

const ownPage = (sender) =>
  sender.id === api.runtime.id && typeof sender.url === "string" && sender.url.startsWith(api.runtime.getURL(""));

async function handle(msg, sender) {
  if (!msg || typeof msg.cmd !== "string") return { ok: false, error: "bad-request" };

  // ── from content scripts (inside web pages) ──
  if (!ownPage(sender)) {
    if (!sender.tab) return { ok: false, error: "bad-request" };
    switch (msg.cmd) {
      case "pageInfo": {
        const settings = await getSettings();
        if (!settings.inlineIcons) return { ok: true, state: "off" };
        const status = await ask("status");
        if (!status.ok) return { ok: true, state: status.error };
        if (status.state !== "unlocked") return { ok: true, state: status.state };
        const r = await ask("logins", { url: sender.url });
        return { ok: true, state: "unlocked", count: r.ok ? r.matches.length : 0 };
      }
      case "openMenu":
        return { ok: true, token: newToken({ kind: "menu", tabId: sender.tab.id, frameId: sender.frameId ?? 0, url: sender.url, mode: msg.mode === "new" ? "new" : "login" }) };
      case "captured":
        return captured(sender, msg);
      case "capturedUsername":
        typedUsernames.set(sender.tab.id, { username: String(msg.username || "").trim().slice(0, 512), host: hostOf(sender.url), at: Date.now() });
        return { ok: true };
      case "pendingSave": {
        if (sender.frameId !== 0) return { ok: true };
        const p = pendingSaveFor(sender.tab.id);
        return p && !p.shown ? { ok: true, token: p.token } : { ok: true };
      }
      default:
        return { ok: false, error: "bad-request" };
    }
  }

  // ── from the extension's own pages ──
  switch (msg.cmd) {
    case "status": {
      const r = await ask("status");
      return r.ok ? r : { ...r, state: r.error };
    }
    case "pair":
      return ask("pair", { browser: String(msg.browser || "") }, WAIT_FOR_YOU);
    case "show":
      return ask("show");
    case "launch":
      return ask("launch");
    case "tabLogins": {
      const tab = await api.tabs.get(msg.tabId);
      return ask("logins", { url: tab.url });
    }
    case "search":
      return ask("search", { query: String(msg.query || "") });
    case "fillTab":
      return fillTab(msg.tabId, String(msg.entry), { anySite: !!msg.anySite, rememberSite: !!msg.rememberSite });
    case "generate":
      return ask("generate");

    // the in-page menu
    case "menuData": {
      const r = tokenFor(msg, sender, "menu");
      if (!r) return { ok: false, error: "expired" };
      const status = await ask("status");
      if (!status.ok) return { ok: true, state: status.error };
      if (status.state !== "unlocked") return { ok: true, state: status.state };
      const logins = await ask("logins", { url: r.url });
      return { ok: true, state: "unlocked", mode: r.mode, site: logins.site, matches: logins.ok ? logins.matches : [] };
    }
    case "menuFill": {
      const r = tokenFor(msg, sender, "menu");
      if (!r) return { ok: false, error: "expired" };
      const reply = await ask("fill", { url: r.url, entry: String(msg.entry) }, WAIT_FOR_YOU);
      if (!reply.ok) return reply;
      closeFrameUi(r.tabId, r.frameId, "menu");
      return fillFrames(r.tabId, [r.frameId], { username: reply.username, password: reply.password }, reply.site);
    }
    case "menuGenerate": {
      const r = tokenFor(msg, sender, "menu");
      if (!r) return { ok: false, error: "expired" };
      const gen = await ask("generate");
      if (!gen.ok) return gen;
      closeFrameUi(r.tabId, r.frameId, "menu");
      const res = await api.scripting.executeScript({
        target: { tabId: r.tabId, frameIds: [r.frameId] },
        func: (pw) => globalThis.__lockbox?.fillNewPassword(pw) ?? { filled: false },
        args: [gen.password],
      });
      return res.some((x) => x?.result?.filled) ? { ok: true } : { ok: false, error: "no-fields", message: "Couldn't find a new-password box." };
    }
    case "menuShowLockbox":
      return ask("show");
    case "menuClose": {
      const r = tokenFor(msg, sender, "menu");
      if (r) closeFrameUi(r.tabId, r.frameId, "menu");
      return { ok: true };
    }

    // the save bar
    case "saveData": {
      const r = tokenFor(msg, sender, "save");
      const p = r && pendingSaveFor(r.tabId);
      if (!p || p.token !== msg.token) return { ok: false, error: "expired" };
      p.shown = true;
      return { ok: true, action: p.check.action, title: p.check.title, username: p.username || p.check.username || "", site: p.check.site };
    }
    case "saveAnswer": {
      const r = tokenFor(msg, sender, "save");
      const p = r && pendingSaveFor(r.tabId);
      if (!p || p.token !== msg.token) return { ok: false, error: "expired" };
      pendingSaves.delete(r.tabId);   // the password is forgotten here, whatever the answer
      if (msg.answer !== "save") {
        tokens.delete(msg.token);
        setTimeout(() => closeFrameUi(r.tabId, 0, "save"), 150);
        if (msg.answer === "never") {
          const settings = await getSettings();
          if (p.check.site && !settings.neverSave.includes(p.check.site)) settings.neverSave.push(p.check.site);
          await api.storage.local.set({ settings });
        }
        return { ok: true };
      }
      const body = { url: p.url, username: p.username, password: p.password };
      if (p.check.action === "update") body.entry = p.check.entry;
      return ask("save", body, WAIT_FOR_YOU);   // the bar shows the result, then asks to close
    }
    case "saveClose": {
      const r = tokenFor(msg, sender, "save");
      if (r) { tokens.delete(msg.token); closeFrameUi(r.tabId, 0, "save"); }
      return { ok: true };
    }
    default:
      return { ok: false, error: "bad-request" };
  }
}

api.runtime.onMessage.addListener((msg, sender, sendResponse) => {
  handle(msg, sender).then(sendResponse, (e) => sendResponse({ ok: false, error: "error", message: String(e?.message || e) }));
  return true;
});

api.tabs.onRemoved.addListener((tabId) => {
  pendingSaves.delete(tabId);
  typedUsernames.delete(tabId);
  for (const [t, r] of tokens) if (r.tabId === tabId) tokens.delete(t);
});

// ───────────── keyboard shortcut ─────────────

api.commands?.onCommand.addListener(async (command) => {
  if (command !== "fill-login") return;
  const [tab] = await api.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) return;
  const status = await ask("status");
  if (status.ok && status.state === "unlocked") {
    const logins = await ask("logins", { url: tab.url });
    if (logins.ok && logins.matches.length === 1) {
      await fillTab(tab.id, logins.matches[0].id);
      return;
    }
  }
  // Locked, several logins, none, or not connected: open the popup so you can choose.
  try { await api.action.openPopup(); } catch { }
});
