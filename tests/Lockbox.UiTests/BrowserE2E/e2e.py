"""
End-to-end test of the Lockbox browser extension in a real Chromium, driven by Playwright.

Run by the UI tests when LOCKBOX_BROWSER_E2E=1 (see Smoke.BrowserE2E): the C# side runs Lockbox with an
unlocked vault and answers its dialogs; this script plays the person at the browser. Websites are served
from the fixtures below (no internet needed). Results go to --report as JSON: [{"name", "ok", "detail"}].
"""
import argparse, asyncio, json, os, re, shutil, time
from pathlib import Path
from playwright.async_api import async_playwright

ap = argparse.ArgumentParser()
ap.add_argument("--ext", required=True)
ap.add_argument("--manifest", required=True, help="native host manifest Lockbox wrote")
ap.add_argument("--profile", required=True)
ap.add_argument("--report", required=True)
ap.add_argument("--signal", required=True, help="folder for lock-request / lock-done files")
ap.add_argument("--chrome", default=shutil.which("google-chrome") or shutil.which("chromium"))
ap.add_argument("--shots", default=os.environ.get("LOCKBOX_BROWSER_SHOTS"), help="folder for screenshots (optional)")
args = ap.parse_args()

STYLE = "<style>body{font:16px sans-serif;margin:40px}input{display:block;width:320px;padding:8px;margin:8px 0}</style>"
LOGIN_FORM = STYLE + """<h1>{title}</h1><form action="/session" method="post">
<label>Username <input type="text" name="login" id="login_field" autocomplete="username"></label>
<label>Password <input type="password" name="password" id="password" autocomplete="current-password"></label>
<button type="submit" id="go">Sign in</button></form>"""
PAGES = {
    "https://github.com/login": LOGIN_FORM.replace("{title}", "Sign in to GitHub"),
    "https://github.com/session": STYLE + "<h1>Signed in</h1>",
    "https://github.com/signup": STYLE + """<h1>Join GitHub</h1><form action="/welcome" method="post">
<label>Email <input type="email" name="email" id="email" autocomplete="email"></label>
<label>Password <input type="password" name="password" id="pw1" autocomplete="new-password"></label>
<label>Confirm <input type="password" name="confirm" id="pw2" autocomplete="new-password"></label>
<button type="submit" id="go">Create account</button></form>""",
    "https://github.com/welcome": STYLE + "<h1>Welcome!</h1>",
    "https://github.com.evil.net/login": LOGIN_FORM.replace("{title}", "Sign in to GitHub"),
    "https://github.com.evil.net/session": STYLE + "<h1>Gotcha</h1>",
    "https://accounts.google.com/identifier": STYLE + """<h1>Sign in</h1><form action="/challenge" method="post">
<input type="email" name="identifier" id="identifier" autocomplete="username"><button type="submit" id="next">Next</button></form>""",
    "https://accounts.google.com/challenge": STYLE + """<h1>Welcome</h1><form action="/done" method="post">
<input type="password" name="Passwd" id="passwd" autocomplete="current-password"><button type="submit" id="next">Next</button></form>""",
    "https://accounts.google.com/done": STYLE + "<h1>Done</h1>",
    "https://bank.example.com/login": LOGIN_FORM.replace("{title}", "Example Bank"),
}

results = []

async def shot(page, name, selector=None):
    if not args.shots:
        return
    Path(args.shots).mkdir(parents=True, exist_ok=True)
    try:
        target = page.locator(selector) if selector else page
        await target.screenshot(path=str(Path(args.shots) / f"{name}.png"))
    except Exception as e:
        print("screenshot failed", name, e, flush=True)
def check(name, ok, detail=""):
    results.append({"name": name, "ok": bool(ok), "detail": str(detail)[:300]})
    print(("PASS " if ok else "FAIL ") + name + (f"  ({detail})" if detail and not ok else ""), flush=True)


async def main():
    shutil.rmtree(args.profile, ignore_errors=True)
    # Chrome reads native host registrations from the profile folder (and the usual per-user folders).
    hosts = Path(args.profile) / "NativeMessagingHosts"
    hosts.mkdir(parents=True)
    shutil.copy(args.manifest, hosts / "com.tylerjayp.lockbox.json")

    async with async_playwright() as p:
        ctx = await p.chromium.launch_persistent_context(
            args.profile, executable_path=args.chrome, headless=True, viewport={"width": 1100, "height": 800},
            args=["--headless=new", f"--disable-extensions-except={args.ext}", f"--load-extension={args.ext}"])
        try:
            await run(ctx)
        finally:
            await ctx.close()


async def run(ctx):
    async def serve(route):
        url = route.request.url.split("?")[0]
        if not url.startswith("http"):
            await route.continue_()          # the extension's own files
        elif url in PAGES:
            await route.fulfill(status=200, content_type="text/html", body=PAGES[url])
        else:
            await route.abort()
    await ctx.route("**/*", serve)

    sw = ctx.service_workers[0] if ctx.service_workers else await ctx.wait_for_event("serviceworker")
    ext_id = sw.url.split("/")[2]
    check("extension has the ID Lockbox trusts", ext_id == "pnjncldjklkgjghfknegebmndmhmchmj", ext_id)

    async def tab_id(url_prefix):
        return await sw.evaluate("async (u) => (await chrome.tabs.query({})).find(t => t.url && t.url.startsWith(u))?.id", url_prefix)

    async def popup_for(url_prefix):
        tid = await tab_id(url_prefix)
        pop = await ctx.new_page()
        await pop.set_viewport_size({"width": 360, "height": 520})
        pop.on("pageerror", lambda e: print("popup error:", e, flush=True))
        pop.on("console", lambda m: print("popup console:", m.text, flush=True) if m.type in ("error", "warning") else None)
        await pop.goto(f"chrome-extension://{ext_id}/popup/popup.html?tab={tid}")
        return pop

    async def text_of(page, sel="main"):
        try:
            return re.sub(r"\s+", " ", await page.inner_text(sel))
        except Exception as e:
            return f"<{e}>"

    async def wait_text(page, pattern, timeout=20000, sel="main"):
        end = time.time() + timeout / 1000
        while time.time() < end:
            t = await text_of(page, sel)
            if re.search(pattern, t):
                return t
            await asyncio.sleep(0.2)
        return await text_of(page, sel)

    async def values(page, *ids):
        return [await page.eval_on_selector(f"#{i}", "e => e.value") for i in ids]

    async def click_field_button(page, field_id):
        """The Lockbox button sits inside the right edge of the field (in a closed shadow root, so click by position)."""
        box = await page.locator(f"#{field_id}").bounding_box()
        await page.mouse.click(box["x"] + box["width"] - 19, box["y"] + box["height"] / 2)

    async def wait_gone(page, name, timeout=6000):
        end = time.time() + timeout / 1000
        while time.time() < end:
            if not any(f"/frames/{name}.html" in f.url for f in page.frames):
                return True
            await asyncio.sleep(0.1)
        return False

    async def frame(page, name, timeout=8000):
        end = time.time() + timeout / 1000
        while time.time() < end:
            for f in page.frames:
                if f"/frames/{name}.html" in f.url:
                    return f
            await asyncio.sleep(0.1)
        return None

    # ── 1. Pairing and popup fill ──
    page = await ctx.new_page()
    await page.goto("https://github.com/login")
    pop = await popup_for("https://github.com/login")
    t = await wait_text(pop, r"Connect this browser")
    code = re.search(r"\d{3} \d{3}", t)
    check("popup asks to connect, with a six-digit code", "Connect this browser" in t and code, t or "(empty)")
    results.append({"name": "_pairing_code", "ok": True, "detail": code.group(0) if code else ""})
    await shot(pop, "popup-1-connect", "body")
    await pop.click("button.btn.primary:has-text('Connect')")
    t = await wait_text(pop, r"Logins for github\.com")
    check("after approving in Lockbox, the popup lists logins for the site", "Logins for github.com" in t and "GitHub" in t, t)
    await shot(pop, "popup-2-logins", "body")
    await pop.click(".item")
    await asyncio.sleep(1.0)
    check("popup fill puts the username and password in the page", await values(page, "login_field", "password") == ["tyler-e2e", "E2e-Gh-Pass-1!"],
          await values(page, "login_field", "password"))
    if not pop.is_closed():
        await pop.close()

    # ── 2. The Lockbox button in the field ──
    await page.reload()
    await asyncio.sleep(1.5)
    await click_field_button(page, "login_field")
    menu = await frame(page, "menu")
    check("the field's Lockbox button opens the menu", menu is not None)
    if menu:
        await menu.wait_for_selector("button:has-text('GitHub')", timeout=8000)
        await asyncio.sleep(0.4)
        await shot(page, "page-1-field-menu")
        await menu.click("button:has-text('GitHub')")
        await asyncio.sleep(1.0)
        check("picking a login in the menu fills the page", await values(page, "login_field", "password") == ["tyler-e2e", "E2e-Gh-Pass-1!"],
              await values(page, "login_field", "password"))
        check("the menu closes after filling", await frame(page, "menu", 1500) is None)

    # ── 3. A look-alike site gets nothing ──
    evil = await ctx.new_page()
    await evil.goto("https://github.com.evil.net/login")
    await asyncio.sleep(1.5)
    await click_field_button(evil, "login_field")
    check("no Lockbox button on a look-alike site with no saved logins", await frame(evil, "menu", 2000) is None)
    pop = await popup_for("https://github.com.evil.net/")
    t = await wait_text(pop, r"No logins saved for evil\.net")
    check("popup on github.com.evil.net shows no logins (its site is evil.net)", "No logins saved for evil.net" in t, t)
    await pop.fill("input[type=search]", "tyler-e2e")
    await asyncio.sleep(0.8)
    await pop.click(".item")
    t = await wait_text(pop, r"saved for github\.com")
    check("filling a github.com login there needs an explicit 'fill anyway'", "saved for github.com" in t and "different site" in t, t)
    await shot(pop, "popup-3-other-site", "body")
    check("and nothing was filled while asking", await values(evil, "login_field", "password") == ["", ""])
    await pop.close()
    await evil.close()

    # ── 4. Sign-up: generate a password, then save the new login ──
    await page.goto("https://github.com/signup")
    await page.fill("#email", "new-user@example.com")
    await asyncio.sleep(1.5)
    await click_field_button(page, "pw1")
    menu = await frame(page, "menu")
    check("new-password boxes get a Lockbox button with the generator", menu is not None)
    if menu:
        await asyncio.sleep(0.6)
        await shot(page, "page-2-generator-menu")
    generated = ""
    if menu:
        await menu.click("button:has-text('Generate a strong password')")
        await asyncio.sleep(1.0)
        pw1, pw2 = await values(page, "pw1", "pw2")
        generated = pw1
        check("generator fills the new and confirm boxes with the same strong password", len(pw1) == 20 and pw1 == pw2, f"{len(pw1)} {pw1 == pw2}")
    results.append({"name": "_generated", "ok": True, "detail": generated})
    await page.click("#go")
    await page.wait_for_url("https://github.com/welcome")
    save = await frame(page, "save", 10000)
    t = await save.inner_text("body") if save else ""
    check("after signing up, Lockbox offers to save the login", save is not None and "Save this login as new-user@example.com?" in t, t)
    await asyncio.sleep(0.4)
    await shot(page, "page-3-save-bar")
    if save:
        await save.click("button:has-text('Save')")
        t = await wait_text(save, r"Saved to Lockbox", sel="body")
        check("Save confirms in the bar", "Saved to Lockbox" in t, t)

    # ── 5. Changed password: offer to update ──
    await page.goto("https://github.com/login")
    await page.fill("#login_field", "tyler-e2e")
    await page.fill("#password", "E2e-Gh-Changed-2!")
    await page.click("#go")
    await page.wait_for_url("https://github.com/session")
    save = await frame(page, "save", 10000)
    t = await save.inner_text("body") if save else ""
    check("a changed password offers to update the saved login", save is not None and "Update the password for GitHub?" in t, t)
    await asyncio.sleep(0.4)
    await shot(page, "page-4-update-bar")
    if save:
        await save.click("button:has-text('Update')")
        t = await wait_text(save, r"Updated in Lockbox", sel="body")
        check("Update confirms in the bar", "Updated in Lockbox" in t, t)
        check("and the bar closes by itself", await wait_gone(page, "save"))

    # ── 6. Two-step sign-in (username, then password on the next page) ──
    g = await ctx.new_page()
    await g.goto("https://accounts.google.com/identifier")
    await g.fill("#identifier", "tyler@gmail.com")
    await g.click("#next")
    await g.wait_for_url("https://accounts.google.com/challenge")
    await g.fill("#passwd", "G00gle-Two-Step!")
    await g.click("#next")
    await g.wait_for_url("https://accounts.google.com/done")
    save = await frame(g, "save", 10000)
    t = await save.inner_text("body") if save else ""
    check("two-step sign-in remembers the username from the first page", save is not None and "tyler@gmail.com" in t, t)
    if save:
        await save.click("button:has-text('Save')")
        await wait_text(save, r"Saved to Lockbox", sel="body")
    await g.close()

    # ── 7. Never save for a site ──
    page2 = await ctx.new_page()
    await page2.goto("https://github.com.evil.net/login")
    await page2.fill("#login_field", "someone")
    await page2.fill("#password", "whatever-1")
    await page2.click("#go")
    await page2.wait_for_url("https://github.com.evil.net/session")
    save = await frame(page2, "save", 10000)
    if save:
        await save.click("button:has-text('Never for this site')")
        await asyncio.sleep(0.8)
    await page2.goto("https://github.com.evil.net/login")
    await page2.fill("#login_field", "someone")
    await page2.fill("#password", "whatever-2")
    await page2.click("#go")
    await page2.wait_for_url("https://github.com.evil.net/session")
    check("'Never for this site' stops the offers there", save is not None and await frame(page2, "save", 3000) is None)
    await page2.close()

    # ── 8. Protected login: Lockbox asks for the master password first ──
    bank = await ctx.new_page()
    await bank.goto("https://bank.example.com/login")
    pop = await popup_for("https://bank.example.com/")
    await wait_text(pop, r"Example Bank|Bank")
    await pop.click(".item")
    await asyncio.sleep(2.5)
    check("a protected login fills after the master password is entered in Lockbox",
          await values(bank, "login_field", "password") == ["tyler-bank", "Bank-Pass-9!"], await values(bank, "login_field", "password"))
    if not pop.is_closed():
        await pop.close()
    await bank.close()

    # ── 9. Locked ──
    Path(args.signal, "lock-request").touch()
    for _ in range(100):
        if Path(args.signal, "lock-done").exists():
            break
        await asyncio.sleep(0.1)
    pop = await popup_for("https://github.com/")
    t = await wait_text(pop, r"Lockbox is locked")
    check("when Lockbox locks, the popup says so and offers to unlock", "Lockbox is locked" in t and "Unlock Lockbox" in t, t)
    await shot(pop, "popup-4-locked", "body")
    await page.goto("https://github.com/login")
    await asyncio.sleep(1.5)
    await click_field_button(page, "login_field")
    menu = await frame(page, "menu")
    t = await wait_text(menu, r"locked", sel="body") if menu else ""
    check("the field's menu says Lockbox is locked and fills nothing", "locked" in t and await values(page, "login_field", "password") == ["", ""], t)


asyncio.run(main())
Path(args.report).write_text(json.dumps(results, indent=1))
