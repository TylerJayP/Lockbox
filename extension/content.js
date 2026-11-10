// Lockbox extension: runs inside web pages (every frame).
//
// - Finds sign-in fields and fills them when the background asks (it checks the frame's site first).
// - Shows a small Lockbox button in sign-in fields; clicking it opens a menu that's an extension page in a
//   frame, so the page can't read the list of logins in it.
// - Notices when you submit a login, so Lockbox can offer to save or update it.
// It never asks for or receives passwords through messages; the background fills directly.

(() => {
  "use strict";
  if (globalThis.__lockbox) return;   // injected twice (declared + on demand)
  const api = globalThis.browser ?? globalThis.chrome;
  const TOP = window === window.top;

  // ───────────── finding fields ─────────────

  function allInputs(scope) {
    const out = [];
    let budget = 4000;   // keep scans cheap on huge pages
    const walk = (root) => {
      for (const el of root.querySelectorAll("input")) out.push(el);
      const tw = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
      for (let n = tw.nextNode(); n && budget-- > 0; n = tw.nextNode()) if (n.shadowRoot) walk(n.shadowRoot);
    };
    walk(scope || document);
    return out;
  }

  function visible(el) {
    if (!el.isConnected || el.disabled || el.readOnly || el.type === "hidden") return false;
    const r = el.getBoundingClientRect();
    if (r.width < 8 || r.height < 8) return false;
    const s = getComputedStyle(el);
    return s.visibility !== "hidden" && s.display !== "none" && Number(s.opacity) > 0.05;
  }

  const TEXT_TYPES = new Set(["text", "email", "tel", ""]);
  const isTexty = (el) => TEXT_TYPES.has((el.getAttribute("type") || "text").toLowerCase());
  const describe = (el) =>
    [el.name, el.id, el.getAttribute("placeholder"), el.getAttribute("aria-label"), el.getAttribute("autocomplete"), el.className]
      .join(" ").toLowerCase();

  function isNewPassword(el) {
    const ac = (el.getAttribute("autocomplete") || "").toLowerCase();
    if (ac.includes("new-password")) return true;
    if (ac.includes("current-password")) return false;
    return /\b(new|confirm|repeat|retype|verify|again)|signup|sign-up|register|create/.test(describe(el));
  }

  function usernameScore(el) {
    const ac = (el.getAttribute("autocomplete") || "").toLowerCase();
    const d = describe(el);
    let s = 0;
    if (ac.includes("username")) s += 10;
    if (ac.includes("email") || el.type === "email") s += 6;
    if (/user|e-?mail|login|account|identifier|phone|mobile|handle/.test(d)) s += 4;
    if (/search|captcha|code|otp|coupon|promo|zip|postal|city|street|address|first|last|card|cvc|amount/.test(d)) s -= 8;
    return s;
  }

  function best(list) {
    let bestEl = null, bestScore = -Infinity;
    list.forEach((el, i) => {
      const s = usernameScore(el) + i * 0.01;   // ties go to the field closest to the password
      if (s > bestScore) { bestEl = el; bestScore = s; }
    });
    return bestScore > -5 ? bestEl : null;
  }

  /** The sign-in fields in a form (or the page): username, current password, and any new-password boxes. */
  function findFields(scope) {
    const inputs = allInputs(scope);
    const passwords = inputs.filter((i) => i.type === "password" && visible(i));
    if (passwords.length) {
      const current = passwords.find((p) => !isNewPassword(p)) ?? passwords[0];
      const pool = inputs.filter((i) => i !== current && isTexty(i) && visible(i) && (!current.form || i.form === current.form || !i.form));
      const before = pool.filter((i) => i.compareDocumentPosition(current) & Node.DOCUMENT_POSITION_FOLLOWING);
      return {
        user: best(before.length ? before : pool),
        password: current,
        passwords,
        newPasswords: passwords.filter(isNewPassword),
      };
    }
    // Username first, password on the next page (Google, Microsoft…)
    const users = inputs.filter((i) => isTexty(i) && visible(i) && usernameScore(i) >= 4);
    return { user: best(users), password: null, passwords: [], newPasswords: [] };
  }

  // ───────────── filling ─────────────

  function onSite(site) {
    const host = location.hostname.toLowerCase();
    return typeof site === "string" && site.length > 0 && (host === site || host.endsWith("." + site));
  }

  function setValue(el, value) {
    el.focus({ preventScroll: true });
    el.value = value;
    el.dispatchEvent(new Event("input", { bubbles: true, composed: true }));
    el.dispatchEvent(new Event("change", { bubbles: true, composed: true }));
  }

  /** Called by the background. `site` is the site Lockbox approved; anything else is refused. */
  function fill(creds, site) {
    if (!onSite(site)) return { filled: false, reason: "site" };
    const f = findFields();
    let filled = false;
    if (f.user && creds.username) { setValue(f.user, creds.username); filled = true; }
    if (f.password && creds.password) { setValue(f.password, creds.password); filled = true; }
    if (filled) (f.password || f.user).focus({ preventScroll: true });
    return { filled };
  }

  /** Puts a generated password into the new-password boxes (both "new" and "confirm"). */
  function fillNewPassword(password) {
    const f = findFields();
    const targets = f.newPasswords.length ? f.newPasswords : f.passwords;
    targets.forEach((t) => setValue(t, password));
    return { filled: targets.length > 0 };
  }

  globalThis.__lockbox = { fill, fillNewPassword };

  // ───────────── noticing submitted logins (to offer saving) ─────────────

  let lastCapture = { key: "", at: 0 };

  function capture(scope) {
    const f = findFields(scope || undefined);
    const newOnes = f.newPasswords.filter((p) => p.value);
    if (newOnes.length >= 2 && newOnes.some((p) => p.value !== newOnes[0].value)) return;   // "confirm" doesn't match yet
    const pw = newOnes[0] ?? f.password;
    const password = pw?.value || "";
    const username = (f.user?.value || "").trim();
    if (!password) {
      if (username && !f.passwords.length) send({ cmd: "capturedUsername", username });   // first page of a two-step sign-in
      return;
    }
    const key = username + "\u0000" + password;
    if (key === lastCapture.key && Date.now() - lastCapture.at < 3000) return;
    lastCapture = { key, at: Date.now() };
    send({ cmd: "captured", username, password });
  }

  const formOf = (el) => el?.form || el?.closest?.("form") || null;

  document.addEventListener("submit", (e) => { if (e.isTrusted) capture(e.target); }, true);
  document.addEventListener("click", (e) => {
    if (!e.isTrusted) return;
    const target = e.composedPath()[0];
    const button = target?.closest?.('button, input[type="submit"], input[type="button"], [role="button"]');
    if (!button || button.closest?.("lockbox-ui")) return;
    const form = formOf(button);
    const fields = allInputs(form || document);
    if (fields.some((i) => i.type === "password" && i.value) || (!fields.some((i) => i.type === "password") && fields.some((i) => isTexty(i) && i.value)))
      capture(form);
  }, true);
  document.addEventListener("keydown", (e) => {
    if (!e.isTrusted || e.key !== "Enter") return;
    const target = e.composedPath()[0];
    if (target instanceof HTMLInputElement && (target.type === "password" || isTexty(target))) capture(formOf(target));
  }, true);

  // ───────────── Lockbox button in sign-in fields ─────────────

  const LOCK_PATH = "M12,17A2,2 0 0,0 14,15C14,13.89 13.1,13 12,13A2,2 0 0,0 10,15A2,2 0 0,0 12,17M18,8A2,2 0 0,1 20,10V20A2,2 0 0,1 18,22H6A2,2 0 0,1 4,20V10C4,8.89 4.9,8 6,8H7V6A5,5 0 0,1 12,1A5,5 0 0,1 17,6V8H18M12,3A3,3 0 0,0 9,6V8H15V6A3,3 0 0,0 12,3Z";
  const UI_CSS = `
      button.lb{all:initial;position:fixed;width:22px;height:22px;border-radius:6px;display:flex;align-items:center;justify-content:center;
        cursor:pointer;background:rgba(79,140,255,.10);box-shadow:0 0 0 1px rgba(79,140,255,.35)}
      button.lb:hover,button.lb:focus-visible{background:rgba(79,140,255,.22)}
      button.lb.locked svg{opacity:.55}
      iframe{all:initial;position:fixed;border:0;border-radius:12px;box-shadow:0 12px 40px rgba(0,0,0,.35);background:#161A20;color-scheme:dark}`;

  // Built with DOM calls rather than HTML strings, so pages that enforce Trusted Types don't break it.
  function lockIcon() {
    const ns = "http://www.w3.org/2000/svg";
    const svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", "0 0 24 24");
    svg.setAttribute("width", "16");
    svg.setAttribute("height", "16");
    svg.setAttribute("aria-hidden", "true");
    const path = document.createElementNS(ns, "path");
    path.setAttribute("fill", "#4F8CFF");
    path.setAttribute("d", LOCK_PATH);
    svg.appendChild(path);
    return svg;
  }

  const ui = { host: null, root: null, buttons: new Map(), menu: null, menuFor: null, save: null };
  let info = { state: "unknown", count: 0, at: 0 };

  function ensureHost() {
    if (ui.host?.isConnected) return;
    ui.host = document.createElement("lockbox-ui");
    ui.host.style.cssText = "all:initial;position:fixed;top:0;left:0;width:0;height:0;z-index:2147483647;";
    ui.root = ui.host.attachShadow({ mode: "closed" });
    const style = document.createElement("style");
    style.textContent = UI_CSS;
    ui.root.appendChild(style);
    document.documentElement.appendChild(ui.host);
  }

  function place(button, field) {
    const r = field.getBoundingClientRect();
    const show = visible(field) && r.bottom > 0 && r.top < innerHeight && r.width >= 80;
    button.style.display = show ? "flex" : "none";
    if (!show) return;
    button.style.left = `${Math.round(r.right - 22 - 8)}px`;
    button.style.top = `${Math.round(r.top + (r.height - 22) / 2)}px`;
  }

  function placeMenu() {
    if (!ui.menu || !ui.menuFor) return;
    const r = ui.menuFor.getBoundingClientRect();
    const width = 320;
    ui.menu.style.width = `${width}px`;
    ui.menu.style.left = `${Math.max(8, Math.min(r.left, innerWidth - width - 8))}px`;
    ui.menu.style.top = `${Math.min(r.bottom + 6, innerHeight - 60)}px`;
  }

  let scheduled = false;
  function reposition() {
    if (scheduled) return;
    scheduled = true;
    requestAnimationFrame(() => {
      scheduled = false;
      for (const [field, button] of ui.buttons) {
        if (!field.isConnected) { button.remove(); ui.buttons.delete(field); continue; }
        place(button, field);
      }
      placeMenu();
    });
  }

  function decorate(field, mode) {
    if (ui.buttons.has(field)) return;
    ensureHost();
    const b = document.createElement("button");
    b.className = "lb" + (info.state === "locked" ? " locked" : "");
    b.title = info.state === "locked" ? "Lockbox is locked" : mode === "new" ? "Lockbox: generate a password" : "Fill with Lockbox";
    b.setAttribute("aria-label", b.title);
    b.appendChild(lockIcon());
    b.addEventListener("mousedown", (e) => e.preventDefault());   // keep focus in the field
    b.addEventListener("click", (e) => {
      if (!e.isTrusted) return;
      e.preventDefault();
      e.stopPropagation();
      openMenu(field, mode);
    });
    ui.root.appendChild(b);
    ui.buttons.set(field, b);
    place(b, field);
  }

  async function openMenu(field, mode) {
    closeMenu();
    const r = await send({ cmd: "openMenu", mode });
    if (!r?.ok) return;
    ensureHost();
    const frame = document.createElement("iframe");
    frame.src = api.runtime.getURL("frames/menu.html") + "#" + r.token;
    frame.style.height = "64px";
    frame.setAttribute("title", "Lockbox");
    ui.root.appendChild(frame);
    ui.menu = frame;
    ui.menuFor = field;
    placeMenu();
  }

  function closeMenu() {
    ui.menu?.remove();
    ui.menu = null;
    ui.menuFor = null;
  }

  function showSave(token) {
    if (!TOP) return;
    ensureHost();
    ui.save?.remove();
    const frame = document.createElement("iframe");
    frame.src = api.runtime.getURL("frames/save.html") + "#" + token;
    frame.setAttribute("title", "Lockbox");
    frame.style.cssText += ";top:16px;right:16px;width:380px;height:150px";
    ui.root.appendChild(frame);
    ui.save = frame;
  }

  // The menu and save bar tell us their height (only their height).
  window.addEventListener("message", (e) => {
    const frame = [ui.menu, ui.save].find((f) => f && e.source === f.contentWindow);
    if (!frame || e.data?.lockbox !== "height") return;
    frame.style.height = `${Math.max(40, Math.min(420, Number(e.data.value) || 0))}px`;
  });

  document.addEventListener("mousedown", (e) => {
    if (ui.menu && !e.composedPath().includes(ui.host)) closeMenu();
  }, true);
  document.addEventListener("keydown", (e) => { if (e.key === "Escape") closeMenu(); }, true);
  addEventListener("scroll", reposition, { capture: true, passive: true });
  addEventListener("resize", reposition, { passive: true });

  async function refreshInfo() {
    if (Date.now() - info.at < 30000) return info;
    const r = await send({ cmd: "pageInfo" });
    info = { state: r?.state || "error", count: r?.count || 0, at: Date.now() };
    return info;
  }

  async function scan() {
    const f = findFields();
    if (!f.user && !f.password) return;
    const i = await refreshInfo();
    const usable = i.state === "unlocked" || i.state === "locked";
    if (!usable) return;
    for (const p of f.newPasswords) decorate(p, "new");
    if (i.state === "locked" || i.count > 0) {
      if (f.user) decorate(f.user, "login");
      if (f.password && !f.newPasswords.includes(f.password)) decorate(f.password, "login");
    }
  }

  let scanTimer = null;
  const scheduleScan = () => {
    clearTimeout(scanTimer);
    scanTimer = setTimeout(() => { scan().catch(() => {}); reposition(); }, 400);
  };
  new MutationObserver(scheduleScan).observe(document.documentElement, { childList: true, subtree: true });
  document.addEventListener("focusin", (e) => { if (e.composedPath()[0] instanceof HTMLInputElement) scheduleScan(); }, true);
  setInterval(reposition, 1000);
  scheduleScan();

  // ───────────── messages ─────────────

  function send(msg) {
    try { return api.runtime.sendMessage(msg).catch(() => null); } catch { return Promise.resolve(null); }
  }

  api.runtime.onMessage.addListener((msg, sender) => {
    if (sender.id !== api.runtime.id) return;
    if (msg?.cmd === "lockbox-close") {
      if (msg.what === "menu") closeMenu();
      if (msg.what === "save") { ui.save?.remove(); ui.save = null; }
    }
    if (msg?.cmd === "lockbox-show-save" && typeof msg.token === "string") showSave(msg.token);
  });

  // A login submitted on the previous page (before it navigated here) may be waiting for an answer.
  if (TOP) send({ cmd: "pendingSave" }).then((r) => { if (r?.token) showSave(r.token); });
})();
