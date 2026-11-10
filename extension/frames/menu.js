// The menu under a sign-in field. An extension page in a frame, so the website can't read it.
"use strict";
const api = globalThis.browser ?? globalThis.chrome;
const token = location.hash.slice(1);
const app = document.getElementById("app");

const send = (cmd, extra = {}) => api.runtime.sendMessage({ cmd, token, ...extra });
const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };

function fitHeight() {
  // Measure the content, not the frame (which can't shrink below its current height otherwise).
  parent.postMessage({ lockbox: "height", value: Math.ceil(document.body.getBoundingClientRect().height) }, "*");
}

function header(text) {
  const h = el("div", "head");
  const img = el("img"); img.src = "../icons/lockbox-32.png"; img.alt = "";
  h.append(img, text);
  return h;
}

function message(text, buttonText, onClick) {
  app.replaceChildren(header("Lockbox"), el("div", "note", text));
  if (buttonText) {
    const actions = el("div", "actions");
    const b = el("button", "btn primary", buttonText);
    b.addEventListener("click", onClick);
    actions.append(b);
    app.append(actions);
    b.focus();
  }
  fitHeight();
}

function row(initial, title, sub, onClick, extra) {
  const r = el("button", "row");
  r.setAttribute("role", "menuitem");
  r.append(el("div", "avatar", initial), (() => { const t = el("div", "text"); t.append(el("div", "title", title), el("div", "sub", sub)); return t; })());
  if (extra) r.append(extra);
  r.addEventListener("click", onClick);
  return r;
}

async function act(promise) {
  app.querySelectorAll("button").forEach((b) => (b.disabled = true));
  const r = await promise;
  if (!r?.ok) {
    app.querySelector(".error")?.remove();
    app.append(el("div", "error", r?.message || "That didn't work."));
    app.querySelectorAll("button").forEach((b) => (b.disabled = false));
    fitHeight();
  }
}

async function load() {
  const d = await send("menuData");
  if (!d?.ok) return message("This menu has expired. Click the Lockbox button in the field again.");
  if (d.state === "locked") return message("Lockbox is locked.", "Unlock Lockbox", () => act(send("menuShowLockbox")));
  if (d.state === "not-paired") return message("Connect this browser first: click the Lockbox button in the toolbar.");
  if (d.state !== "unlocked") return message("Lockbox isn't open. Open it, then try again.");

  const site = el("span", "site", d.site || "this site");
  const head = header("");
  head.append("Lockbox for ", site);
  app.replaceChildren(head);

  if (d.mode === "new") {
    app.append(row("✦", "Generate a strong password", "Fills the new-password boxes", () => act(send("menuGenerate"))));
  }
  for (const m of d.matches) {
    const extra = m.protected ? el("span", "lock", "asks for master password") : null;
    app.append(row((m.title || "?").trim().charAt(0).toUpperCase(), m.title, m.username || "(no username)", () => act(send("menuFill", { entry: m.id })), extra));
  }
  if (!d.matches.length && d.mode !== "new") app.append(el("div", "note", `No logins saved for ${d.site || "this site"}. Sign in once and Lockbox will offer to save it.`));
  app.querySelector("button")?.focus();
  fitHeight();
}

document.addEventListener("keydown", (e) => {
  if (e.key === "Escape") send("menuClose");
  if (e.key === "ArrowDown" || e.key === "ArrowUp") {
    const items = [...app.querySelectorAll("button")];
    const i = items.indexOf(document.activeElement);
    items[(i + (e.key === "ArrowDown" ? 1 : -1) + items.length) % items.length]?.focus();
    e.preventDefault();
  }
});
load();
