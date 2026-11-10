// "Save to Lockbox?" after you sign in or sign up. An extension page in a frame, so the website can't press its buttons.
"use strict";
const api = globalThis.browser ?? globalThis.chrome;
const token = location.hash.slice(1);
const app = document.getElementById("app");

const send = (cmd, extra = {}) => api.runtime.sendMessage({ cmd, token, ...extra });
const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
const fitHeight = () => parent.postMessage({ lockbox: "height", value: Math.ceil(document.body.getBoundingClientRect().height) }, "*");

function head(text) {
  const h = el("div", "head");
  const img = el("img"); img.src = "../icons/lockbox-32.png"; img.alt = "";
  h.append(img, text);
  return h;
}

async function answer(choice, buttons) {
  buttons.forEach((b) => (b.disabled = true));
  if (choice === "save") app.append(el("div", "note", "Saving\u2026 (if Lockbox asks for your master password, enter it there)"));
  fitHeight();
  const r = await send("saveAnswer", { answer: choice });
  if (choice !== "save") return;
  const done = r?.ok ? (r.action === "updated" ? "Updated in Lockbox." : "Saved to Lockbox.") : (r?.message || "Lockbox couldn't save it.");
  app.replaceChildren(head("Lockbox"), el("div", "note", done));
  fitHeight();
  setTimeout(() => send("saveClose"), r?.ok ? 1600 : 4000);
}

async function load() {
  const d = await send("saveData");
  if (!d?.ok) { app.replaceChildren(head("Lockbox"), el("div", "note", "This has expired.")); fitHeight(); return; }

  const update = d.action === "update";
  const who = d.username ? ` as ${d.username}` : "";
  const title = el("div", "title", update ? `Update the password for ${d.title}?` : `Save this login${who}?`);
  const sub = el("div", "sub", update ? `Lockbox keeps the old one in the password history.` : `For ${d.site}. It's stored encrypted in your vault.`);
  const text = el("div", "text"); text.style.padding = "0 8px"; text.append(title, sub);

  const save = el("button", "btn primary", update ? "Update" : "Save");
  const later = el("button", "btn", "Not now");
  const never = el("button", "link", "Never for this site");
  const buttons = [save, later, never];
  save.addEventListener("click", () => answer("save", buttons));
  later.addEventListener("click", () => answer("dismiss", buttons));
  never.addEventListener("click", () => answer("never", buttons));
  const actions = el("div", "actions"); actions.append(save, later);
  if (!update) { const spacer = el("span"); spacer.style.flex = "1"; actions.append(spacer, never); }

  app.replaceChildren(head("Lockbox"), text, actions);
  save.focus();
  fitHeight();
}

document.addEventListener("keydown", (e) => { if (e.key === "Escape") send("saveAnswer", { answer: "dismiss" }); });
load();
