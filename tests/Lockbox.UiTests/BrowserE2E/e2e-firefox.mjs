// End-to-end test of the Lockbox browser extension in a real Firefox, driven by Puppeteer over WebDriver BiDi.
//
// The Firefox twin of e2e.py, run by the UI tests when LOCKBOX_BROWSER_E2E=firefox or all (see Smoke.BrowserE2E).
// The C# side runs Lockbox and answers its dialogs; this script plays the person at the browser. Websites are
// served from the fixtures below (no internet needed). Needs `npm ci` in this folder and a Firefox build.
// Results go to --report as JSON: [{"name", "ok", "detail"}].
import { parseArgs } from "node:util";
import { mkdirSync, rmSync, copyFileSync, writeFileSync, existsSync, closeSync, openSync } from "node:fs";
import path from "node:path";
import puppeteer from "puppeteer-core";

const { values: args } = parseArgs({
  options: {
    ext: { type: "string" }, manifest: { type: "string" }, profile: { type: "string" }, report: { type: "string" },
    signal: { type: "string" }, firefox: { type: "string", default: process.env.LOCKBOX_FIREFOX || "firefox" },
    shots: { type: "string", default: process.env.LOCKBOX_BROWSER_SHOTS },
  },
});

const ADDON_ID = "lockbox@tylerjayp.github.io";
const UUID = "4c6b9a52-0d3e-4f1a-9b7c-2e5d8f0a1c3b";   // fixed so the test knows the extension's address
const EXT = `moz-extension://${UUID}`;

const STYLE = "<style>body{font:16px sans-serif;margin:40px}input{display:block;width:320px;padding:8px;margin:8px 0}</style>";
const LOGIN_FORM = (title) => STYLE + `<h1>${title}</h1><form action="/session" method="post">
<label>Username <input type="text" name="login" id="login_field" autocomplete="username"></label>
<label>Password <input type="password" name="password" id="password" autocomplete="current-password"></label>
<button type="submit" id="go">Sign in</button></form>`;
const PAGES = {
  "https://github.com/login": LOGIN_FORM("Sign in to GitHub"),
  "https://github.com/session": STYLE + "<h1>Signed in</h1>",
  "https://github.com/blank": STYLE + "<p>blank</p>",
  "https://github.com/signup": STYLE + `<h1>Join GitHub</h1><form action="/welcome" method="post">
<label>Email <input type="email" name="email" id="email" autocomplete="email"></label>
<label>Password <input type="password" name="password" id="pw1" autocomplete="new-password"></label>
<label>Confirm <input type="password" name="confirm" id="pw2" autocomplete="new-password"></label>
<button type="submit" id="go">Create account</button></form>`,
  "https://github.com/welcome": STYLE + "<h1>Welcome!</h1>",
  "https://github.com.evil.net/login": LOGIN_FORM("Sign in to GitHub"),
  "https://github.com.evil.net/session": STYLE + "<h1>Gotcha</h1>",
  "https://accounts.google.com/identifier": STYLE + `<h1>Sign in</h1><form action="/challenge" method="post">
<input type="email" name="identifier" id="identifier" autocomplete="username"><button type="submit" id="next">Next</button></form>`,
  "https://accounts.google.com/challenge": STYLE + `<h1>Welcome</h1><form action="/done" method="post">
<input type="password" name="Passwd" id="passwd" autocomplete="current-password"><button type="submit" id="next">Next</button></form>`,
  "https://accounts.google.com/done": STYLE + "<h1>Done</h1>",
  "https://bank.example.com/login": LOGIN_FORM("Example Bank"),
};

const results = [];
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
function check(name, ok, detail = "") {
  results.push({ name, ok: !!ok, detail: String(detail).slice(0, 300) });
  console.log((ok ? "PASS " : "FAIL ") + name + (detail && !ok ? `  (${detail})` : ""));
}
async function shot(page, name) {
  if (!args.shots) return;
  mkdirSync(args.shots, { recursive: true });
  try { await page.screenshot({ path: path.join(args.shots, `firefox-${name}.png`) }); } catch (e) { console.log("screenshot failed", name, e.message); }
}

async function main() {
  rmSync(args.profile, { recursive: true, force: true });
  // Firefox on Linux reads native host registrations from ~/.mozilla/native-messaging-hosts; give it its own home.
  const home = path.join(args.profile, "home");
  const hosts = path.join(home, ".mozilla", "native-messaging-hosts");
  mkdirSync(hosts, { recursive: true });
  copyFileSync(args.manifest, path.join(hosts, "com.tylerjayp.lockbox.json"));

  const browser = await puppeteer.launch({
    browser: "firefox", executablePath: args.firefox, headless: true, userDataDir: path.join(args.profile, "ff"),
    defaultViewport: { width: 1100, height: 800 },
    extraPrefsFirefox: {
      "extensions.webextensions.uuids": JSON.stringify({ [ADDON_ID]: UUID }),
      "extensions.webextOptionalPermissionPrompts": false,   // "Allow" in the popup is granted without Firefox's doorhanger
    },
    // System access lets the test read the extension's own pages (the popup and the in-page frames).
    env: { ...process.env, HOME: home, MOZ_REMOTE_ALLOW_SYSTEM_ACCESS: "1" },
  });
  try {
    const id = await browser.installExtension(args.ext);
    check("Firefox accepts the packaged add-on with the ID Lockbox trusts", id === ADDON_ID, id);
    await run(browser);
  } finally {
    await browser.close();
  }
}

async function run(browser) {
  async function newPage() {
    const page = await browser.newPage();
    await page.setRequestInterception(true);
    page.on("request", (r) => {
      const url = r.url().split("?")[0];
      if (!url.startsWith("http")) return r.continue();          // the extension's own files
      if (url in PAGES) return r.respond({ status: 200, contentType: "text/html", body: PAGES[url] });
      return r.abort();
    });
    page.on("pageerror", (e) => console.log("page error:", e.message));
    return page;
  }

  // WebDriver can't open moz-extension:// pages directly, so go through one of the add-on's web-reachable pages.
  async function extensionPage(page, rel) {
    await page.goto("https://github.com/blank");
    await Promise.all([page.waitForNavigation({ timeout: 15000 }).catch(() => {}), page.evaluate((u) => { location.href = u; }, `${EXT}/frames/menu.html`)]);
    await Promise.all([page.waitForNavigation({ timeout: 15000 }).catch(() => {}), page.evaluate((u) => { location.href = u; }, rel)]);
    return page;
  }

  const helper = await extensionPage(await newPage(), "/frames/save.html");
  const tabId = (prefix) => helper.evaluate(async (u) => (await browser.tabs.query({})).find((t) => t.url && t.url.startsWith(u))?.id, prefix);

  async function popupFor(prefix, tid = null) {
    tid ??= await tabId(prefix);
    const pop = await newPage();
    await pop.setViewport({ width: 360, height: 520 });
    return extensionPage(pop, `/popup/popup.html?tab=${tid}`);
  }

  const norm = (s) => (s || "").replace(/\s+/g, " ").trim();
  async function textOf(target, sel = "main") {
    try { return norm(await target.$eval(sel, (e) => e.innerText)); } catch (e) { return `<${e.message.slice(0, 80)}>`; }
  }
  async function waitText(target, pattern, timeout = 20000, sel = "main") {
    const end = Date.now() + timeout;
    while (Date.now() < end) {
      const t = await textOf(target, sel);
      if (pattern.test(t)) return t;
      await sleep(200);
    }
    return textOf(target, sel);
  }
  const values = (page, ...ids) => Promise.all(ids.map((i) => page.$eval(`#${i}`, (e) => e.value)));
  const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
  async function clickFieldButton(page, fieldId) {
    // The Lockbox button sits inside the right edge of the field (in a closed shadow root, so click by position).
    const box = await (await page.$(`#${fieldId}`)).boundingBox();
    await page.mouse.click(box.x + box.width - 19, box.y + box.height / 2);
  }
  // Puppeteer reports the add-on's frames as about:blank in Firefox, so ask each frame where it really is.
  async function framesNamed(page, name) {
    const found = [];
    for (const f of page.frames()) {
      if (f === page.mainFrame()) continue;
      const href = f.url().includes("/frames/") ? f.url() : await f.evaluate(() => location.href).catch(() => "");
      if (href.includes(`/frames/${name}.html`)) found.push(f);
    }
    return found;
  }
  async function frame(page, name, timeout = 8000) {
    const end = Date.now() + timeout;
    while (Date.now() < end) {
      const [f] = await framesNamed(page, name);
      if (f) return f;
      await sleep(100);
    }
    return null;
  }
  async function waitGone(page, name, timeout = 6000) {
    const end = Date.now() + timeout;
    while (Date.now() < end) {
      if ((await framesNamed(page, name)).length === 0) return true;
      await sleep(100);
    }
    return false;
  }
  async function frameHeight(f) {
    // The frame measures itself and the page sizes the iframe to match; its window height shows the result.
    return f.evaluate(() => innerHeight).catch(() => 0);
  }
  async function submit(page, button, url) {
    await Promise.all([page.waitForNavigation({ timeout: 15000 }).catch(() => {}), page.click(button)]);
    const end = Date.now() + 10000;
    while (page.url() !== url && Date.now() < end) await sleep(100);
  }
  async function clickButton(target, text) {
    const handles = await target.$$("button");
    for (const h of handles) {
      if (norm(await h.evaluate((e) => e.innerText)).includes(text)) { await h.click(); return true; }
    }
    return false;
  }

  // ── 0. Firefox lets people switch off the add-on's access to websites; the popup asks for it back ──
  const page = await newPage();
  await page.goto("https://github.com/login");
  const githubTab = await tabId("https://github.com/login");   // tab addresses are hidden once access is off
  await helper.evaluate(() => browser.permissions.remove({ origins: ["http://*/*", "https://*/*"] }));
  let pop = await popupFor(null, githubTab);
  let t = await waitText(pop, /Allow Lockbox on websites/, 8000);
  check("without access to websites, the popup asks for it", t.includes("Allow Lockbox on websites"), t);
  await shot(pop, "popup-0-site-access");
  await clickButton(pop, "Allow");
  t = await waitText(pop, /Connect this browser/);
  check("and 'Allow' gets it back", await helper.evaluate(() => browser.permissions.contains({ origins: ["https://*/*"] })), t);
  await page.reload();

  // ── 1. Pairing and popup fill ──
  const code = t.match(/\d{3} \d{3}/);
  check("popup asks to connect, with a six-digit code", t.includes("Connect this browser") && code, t || "(empty)");
  results.push({ name: "_pairing_code", ok: true, detail: code ? code[0] : "" });
  await shot(pop, "popup-1-connect");
  await clickButton(pop, "Connect");
  t = await waitText(pop, /Logins for github\.com/);
  check("after approving in Lockbox, the popup lists logins for the site", t.includes("Logins for github.com") && t.includes("GitHub"), t);
  await shot(pop, "popup-2-logins");
  await pop.click(".item");
  await sleep(1000);
  let v = await values(page, "login_field", "password");
  check("popup fill puts the username and password in the page", same(v, ["tyler-e2e", "E2e-Gh-Pass-1!"]), v);
  if (!pop.isClosed()) await pop.close();

  // ── 2. The Lockbox button in the field ──
  await page.reload();
  await sleep(1500);
  await clickFieldButton(page, "login_field");
  let menu = await frame(page, "menu");
  check("the field's Lockbox button opens the menu", menu);
  if (menu) {
    await waitText(menu, /GitHub/, 8000, "body");
    await sleep(500);
    const h = await frameHeight(menu);
    check("the menu is sized to fit what's in it", h > 60 && h < 420, h);
    await shot(page, "page-1-field-menu");
    await clickButton(menu, "GitHub");
    await sleep(1000);
    v = await values(page, "login_field", "password");
    check("picking a login in the menu fills the page", same(v, ["tyler-e2e", "E2e-Gh-Pass-1!"]), v);
    check("the menu closes after filling", (await frame(page, "menu", 1500)) === null);
  }

  // ── 3. A look-alike site gets nothing ──
  const evil = await newPage();
  await evil.goto("https://github.com.evil.net/login");
  await sleep(1500);
  await clickFieldButton(evil, "login_field");
  check("no Lockbox button on a look-alike site with no saved logins", (await frame(evil, "menu", 2000)) === null);
  pop = await popupFor("https://github.com.evil.net/");
  t = await waitText(pop, /No logins saved for evil\.net/);
  check("popup on github.com.evil.net shows no logins (its site is evil.net)", t.includes("No logins saved for evil.net"), t);
  await pop.type("input[type=search]", "tyler-e2e");
  await sleep(800);
  await pop.click(".item");
  t = await waitText(pop, /saved for github\.com/);
  check("filling a github.com login there needs an explicit 'fill anyway'", t.includes("saved for github.com") && t.includes("different site"), t);
  check("and nothing was filled while asking", same(await values(evil, "login_field", "password"), ["", ""]));
  await pop.close();
  await evil.close();

  // ── 4. Sign-up: generate a password, then save the new login ──
  await page.goto("https://github.com/signup");
  await page.type("#email", "new-user@example.com");
  await sleep(1500);
  await clickFieldButton(page, "pw1");
  menu = await frame(page, "menu");
  check("new-password boxes get a Lockbox button with the generator", menu);
  let generated = "";
  if (menu) {
    await waitText(menu, /Generate a strong password/, 8000, "body");
    await clickButton(menu, "Generate a strong password");
    await sleep(1000);
    const [pw1, pw2] = await values(page, "pw1", "pw2");
    generated = pw1;
    check("generator fills the new and confirm boxes with the same strong password", pw1.length === 20 && pw1 === pw2, `${pw1.length} ${pw1 === pw2}`);
  }
  results.push({ name: "_generated", ok: true, detail: generated });
  await submit(page, "#go", "https://github.com/welcome");
  let save = await frame(page, "save", 10000);
  t = save ? await waitText(save, /Save this login/, 5000, "body") : "";
  check("after signing up, Lockbox offers to save the login", save && t.includes("Save this login as new-user@example.com?"), t);
  await shot(page, "page-2-save-bar");
  if (save) {
    await clickButton(save, "Save");
    t = await waitText(save, /Saved to Lockbox/, 20000, "body");
    check("Save confirms in the bar", t.includes("Saved to Lockbox"), t);
  }

  // ── 5. Changed password: offer to update ──
  await page.goto("https://github.com/login");
  await sleep(500);
  await page.type("#login_field", "tyler-e2e");
  await page.type("#password", "E2e-Gh-Changed-2!");
  await submit(page, "#go", "https://github.com/session");
  save = await frame(page, "save", 10000);
  t = save ? await waitText(save, /Update the password/, 5000, "body") : "";
  check("a changed password offers to update the saved login", save && t.includes("Update the password for GitHub?"), t);
  if (save) {
    await clickButton(save, "Update");
    t = await waitText(save, /Updated in Lockbox/, 20000, "body");
    check("Update confirms in the bar", t.includes("Updated in Lockbox"), t);
    check("and the bar closes by itself", await waitGone(page, "save"));
  }

  // ── 6. Two-step sign-in (username, then password on the next page) ──
  const g = await newPage();
  await g.goto("https://accounts.google.com/identifier");
  await sleep(500);
  await g.type("#identifier", "tyler@gmail.com");
  await submit(g, "#next", "https://accounts.google.com/challenge");
  await sleep(500);
  await g.type("#passwd", "G00gle-Two-Step!");
  await submit(g, "#next", "https://accounts.google.com/done");
  save = await frame(g, "save", 10000);
  t = save ? await waitText(save, /tyler@gmail\.com/, 5000, "body") : "";
  check("two-step sign-in remembers the username from the first page", save && t.includes("tyler@gmail.com"), t);
  if (save) {
    await clickButton(save, "Save");
    await waitText(save, /Saved to Lockbox/, 20000, "body");
  }
  await g.close();

  // ── 7. Never save for a site ──
  const page2 = await newPage();
  for (const [i, answer] of [[1, "Never for this site"], [2, null]]) {
    await page2.goto("https://github.com.evil.net/login");
    await sleep(500);
    await page2.type("#login_field", "someone");
    await page2.type("#password", `whatever-${i}`);
    await submit(page2, "#go", "https://github.com.evil.net/session");
    save = await frame(page2, "save", answer ? 10000 : 3000);
    if (answer && save) {
      await waitText(save, /Never for this site/, 5000, "body");
      await clickButton(save, answer);
      await sleep(800);
    } else if (!answer) {
      check("'Never for this site' stops the offers there", save === null);
    } else {
      check("a new login on the look-alike site is offered for saving (to that site)", false, "no save bar");
    }
  }
  await page2.close();

  // ── 8. Protected login: Lockbox asks for the master password first ──
  const bank = await newPage();
  await bank.goto("https://bank.example.com/login");
  pop = await popupFor("https://bank.example.com/");
  await waitText(pop, /Example Bank/);
  await pop.click(".item");
  await sleep(2500);
  v = await values(bank, "login_field", "password");
  check("a protected login fills after the master password is entered in Lockbox", same(v, ["tyler-bank", "Bank-Pass-9!"]), v);
  if (!pop.isClosed()) await pop.close();
  await bank.close();

  // ── 9. Keyboard shortcut: with exactly one login for the site, Ctrl+Shift+L fills it straight away ──
  // WebDriver's key presses go to the page, not to Firefox's own shortcut handling, so run the shortcut the
  // way Firefox does when the keys are pressed: through the <key> it registered for the add-on.
  const g2 = await newPage();
  await g2.goto("https://accounts.google.com/identifier");
  await sleep(1500);
  await g2.bringToFront();
  await g2.click("#identifier");
  const tree = await browser.connection.send("browsingContext.getTree", { "moz:scope": "chrome" });
  const win = tree.result.contexts.find((c) => c.url.includes("browser.xhtml"));
  const pressed = await browser.connection.send("script.evaluate", {
    target: { context: win.context }, awaitPromise: false,
    expression: `(() => { const k = document.getElementById("ext-keyset-id-lockbox_tylerjayp_github_io")?.firstElementChild;
      if (!k) return "no shortcut"; const keys = k.getAttribute("modifiers") + "+" + k.getAttribute("key"); k.doCommand(); return keys; })()`,
  }).then((r) => r.result.result?.value).catch((e) => e.message);
  check("the add-on's shortcut is Ctrl+Shift+L", /accel.*shift|shift.*accel/.test(pressed) && /\+L$/i.test(pressed), pressed);
  const end = Date.now() + 5000;
  while (Date.now() < end && !(await values(g2, "identifier"))[0]) await sleep(200);
  v = await values(g2, "identifier");
  check("Ctrl+Shift+L fills the only login for the site", same(v, ["tyler@gmail.com"]), v);
  await g2.close();

  // ── 10. Locked ──
  closeSync(openSync(path.join(args.signal, "lock-request"), "w"));
  for (let i = 0; i < 100 && !existsSync(path.join(args.signal, "lock-done")); i++) await sleep(100);
  pop = await popupFor("https://github.com/");
  t = await waitText(pop, /Lockbox is locked/);
  check("when Lockbox locks, the popup says so and offers to unlock", t.includes("Lockbox is locked") && t.includes("Unlock Lockbox"), t);
  await shot(pop, "popup-3-locked");
  await page.goto("https://github.com/login");
  await sleep(1500);
  await clickFieldButton(page, "login_field");
  menu = await frame(page, "menu");
  t = menu ? await waitText(menu, /locked/, 8000, "body") : "";
  check("the field's menu says Lockbox is locked and fills nothing", t.includes("locked") && same(await values(page, "login_field", "password"), ["", ""]), t);
}

try {
  await main();
} catch (e) {
  console.log("error:", e.stack || e);
  check("the Firefox test ran to the end", false, e.message);
}
writeFileSync(args.report, JSON.stringify(results, null, 1));
process.exit(0);
