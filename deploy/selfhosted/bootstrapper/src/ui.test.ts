import { afterAll, beforeAll, describe, expect, test } from "bun:test";
import { chromium, type Browser, type Page } from "playwright";
import { mkdtemp, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { PANEL_PATH } from "./compose";
import { createServer } from "./server";
import { setupFromEnvironment } from "./setup";

/* ------------------------------------------------------------------------------------------------
 * The page, in a browser, against the real server.
 *
 * Everything else in this suite tests a function. This tests the arrangement — the parts that only
 * exist once markup, a stylesheet, a script, a session cookie and an HTTP path are all present at the
 * same time, which is exactly where the mistakes that reach an operator live.
 *
 * Two of them are worth the price of a browser on their own:
 *
 *  1. **The API base.** The page is served at `/` during setup and at `/panel/` afterwards, with the
 *     prefix stripped before this process sees it. Every request it makes is relative, so both have to
 *     resolve to the same routes. The proxy below is Traefik's arrangement in twenty lines.
 *  2. **Which screen a stage draws.** `degraded` means containers are running and a retry is not free.
 *     It fell through to the wizard and rendered an Install button, which is the single worst thing this
 *     page could put on screen — and no unit test noticed, because every function involved was right.
 *
 * Assertions are `bun:test`'s. Playwright's `expect` is a different object with its own matchers
 * (`toBeVisible` and friends) and mixing the two silently gives you the one without them.
 * ---------------------------------------------------------------------------------------------- */

const CODE = "TEST-CODE-ABCD-2345";
const CODE_FIELD = 'input[aria-label="Bootstrap code"]';

let browser: Browser;
let panel: ReturnType<typeof createServer>;
let proxy: ReturnType<typeof Bun.serve>;
let direct: string;
let mounted: string;

beforeAll(async () => {
    const root = await mkdtemp(join(tmpdir(), "argon-ui-"));

    await writeFile(join(root, "bootstrap.code"), `${CODE}\n`, { mode: 0o600 });

    // A real setup over a real directory, so `/api/state` answers with something the wizard can be drawn
    // from. It can never interrogate an image or start anything here — there is no docker — and it does
    // not need to: the screens that require a running install are driven through the preview harness.
    let stored: string | undefined;

    panel = createServer({
        code: CODE,
        hostname: "127.0.0.1",
        port: 0,
        setup: setupFromEnvironment({ ARGON_BOOTSTRAP_CONFIG_DIR: root }),

        // In memory: what these tests drive is the page, and `credential.test.ts` is where the file and
        // its mode are checked. `write` hands back what would have been stored, so the server starts
        // accepting the same password immediately — which is the property the page depends on.
        credentials: {
            read: async () => stored,
            write: async (password) => (stored = await Bun.password.hash(password, { algorithm: "argon2id" })),
        },
    });

    direct = `http://127.0.0.1:${panel.server!.port}`;

    // What the edge does: `/panel` redirects to `/panel/`, and everything under it reaches the panel with
    // the prefix removed. Written out rather than mocked, because what is under test is whether the
    // page's relative URLs survive it.
    proxy = Bun.serve({
        port: 0,
        hostname: "127.0.0.1",
        async fetch(request) {
            const incoming = new URL(request.url);

            if (incoming.pathname === PANEL_PATH)
                return new Response(null, { status: 302, headers: { location: `${PANEL_PATH}/` } });

            if (!incoming.pathname.startsWith(`${PANEL_PATH}/`)) return new Response("not found", { status: 404 });

            const target = new URL(incoming.pathname.slice(PANEL_PATH.length) + incoming.search, direct);

            return await fetch(target, {
                method: request.method,
                headers: request.headers,
                body: request.method === "GET" || request.method === "HEAD" ? undefined : await request.arrayBuffer(),
                redirect: "manual",
            });
        },
    });

    mounted = `http://127.0.0.1:${proxy.port}${PANEL_PATH}`;

    browser = await chromium.launch();
});

afterAll(async () => {
    await browser?.close();
    proxy?.stop(true);
    await panel?.stop(true);
});

/**
 * A page that reports what the console said, because a thrown error still leaves something rendered.
 *
 * One exception, and only one: the browser logs a failed request as a console error, and the first
 * thing this page does is ask for a state it is not yet allowed to have. That 401 is the designed way
 * to find out nobody has signed in — the sign-in form is what it renders. Counting it would mean every
 * test starts with a problem it is supposed to have.
 */
async function open(url: string): Promise<{ page: Page; problems: string[] }> {
    const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
    const problems: string[] = [];

    const expected = (text: string): boolean => text.includes("401 (Unauthorized)");

    page.on("console", (message) => {
        if (message.type() === "error" && !expected(message.text())) problems.push(message.text());
    });

    page.on("pageerror", (error) => problems.push(String(error)));

    await page.goto(url, { waitUntil: "networkidle" });
    await page.waitForSelector("#app *", { timeout: 10_000 });

    return { page, problems };
}

/** Whether the page has this text anywhere on it. */
async function shows(page: Page, text: string): Promise<boolean> {
    return (await page.locator(`text=${text}`).count()) > 0;
}

async function signIn(page: Page): Promise<void> {
    await page.fill(CODE_FIELD, CODE);
    await page.click("text=Continue");
    await page.waitForSelector("text=Domain", { timeout: 10_000 });
}

describe("the page, served directly", () => {
    test("renders the code form and nothing throws", async () => {
        const { page, problems } = await open(direct);

        expect(await shows(page, "Enter the setup code")).toBe(true);
        expect(problems).toEqual([]);

        await page.close();
    });

    /**
     * The code is read off another screen and typed back, so the field reshapes what is typed into the
     * shape it was printed in. Pasting it with its hyphens has to work too — that is how most people
     * will do it, and a field that mangles a correct paste is worse than one that does nothing.
     */
    test("the code field groups and uppercases what is typed", async () => {
        const { page } = await open(direct);

        await page.fill(CODE_FIELD, "testcodeabcd2345");
        expect(await page.inputValue(CODE_FIELD)).toBe("TEST-CODE-ABCD-2345");

        await page.fill(CODE_FIELD, "TEST-CODE-ABCD-2345");
        expect(await page.inputValue(CODE_FIELD)).toBe("TEST-CODE-ABCD-2345");

        await page.close();
    });

    test("a wrong code is refused and says so", async () => {
        const { page } = await open(direct);

        await page.fill(CODE_FIELD, "AAAA-BBBB-CCCC-DDDD");
        await page.click("text=Continue");
        await page.waitForSelector("text=not accepted", { timeout: 10_000 });

        await page.close();
    });

    /**
     * The proof is computed in the browser with WebCrypto and checked on the server with node's crypto.
     * Two implementations of one HMAC over one nonce, and this is the only place they meet.
     */
    test("the right code signs in and the questions appear", async () => {
        const { page, problems } = await open(direct);

        await signIn(page);

        expect(await shows(page, "Roles")).toBe(true);
        expect(problems).toEqual([]);

        await page.close();
    });

    /** Too short is refused with the reason, not with a shrug. */
    test("a password that is too short is refused where it was typed", async () => {
        const { page } = await open(direct);

        await signIn(page);

        await page.fill('input[aria-label="New panel password"]', "short");
        await page.click('button[aria-label="Save panel password"]');

        await page.waitForSelector("text=At least 12 characters", { timeout: 10_000 });

        await page.close();
    }, 30_000);

    /**
     * The install is gated on the credential that will still work tomorrow.
     *
     * Finishing retires the bootstrap code — it is printed in a terminal and left in a file, and §4 is
     * right that leaving it valid afterwards replaces one problem with a worse one. Retiring it with
     * nothing behind it would leave a panel holding the docker socket that nobody can ever sign into,
     * so the two are one change and this is the half the operator sees.
     */
    test("the install waits for a panel password, and the button says so", async () => {
        const { page } = await open(direct);

        await signIn(page);

        const install = page.locator("button", { hasText: "Install" });

        expect(await install.isDisabled()).toBe(true);
        expect(await shows(page, "a panel password")).toBe(true);

        await page.fill('input[aria-label="New panel password"]', "operator-panel-password");
        await page.click('button[aria-label="Save panel password"]');
        await page.waitForSelector("text=A password is set", { timeout: 10_000 });

        // Still short of ready — no version has been interrogated here — but no longer for this reason.
        expect(await shows(page, "a panel password")).toBe(false);

        await page.close();
    }, 30_000);

    /**
     * Every answer the server is waiting for has somewhere to be given.
     *
     * The wizard shipped with five cards for six answers: `traffic` had no control at all, so `missing`
     * named it forever, the stage never reached `ready`, and the Install button was permanently
     * disabled — with nothing on screen explaining which question was unanswered, because the question
     * was not on screen.
     *
     * Asserted against what the server reports rather than against a list here, so a seventh answer
     * added to `Answers` fails this the day it appears instead of the day someone tries to install.
     */
    test("the wizard asks for every answer the server is waiting for", async () => {
        const { page } = await open(direct);

        await signIn(page);

        // Asked through the page's own request context, so it carries the session cookie the sign-in
        // just established and resolves the path the way the page does. Not `page.evaluate`: this file
        // is type-checked as server code, where the DOM globals a browser closure would need do not
        // exist — and widening the project's `lib` to get them would hand every server module a
        // `document` it must never touch.
        const state = await page.request.get(new URL("api/state", page.url()).toString());
        const missing = ((await state.json()) as { missing: string[] }).missing;

        // The heading each answer is given under. A key with no entry is the failure this exists for.
        const asked: Record<string, string> = {
            domain: "Domain",
            serverVersion: "Version",
            roles: "Roles",
            storage: "File storage",
            traffic: "How traffic gets here",
            voice: "Voice",
        };

        expect(missing.length).toBeGreaterThan(0);

        for (const key of missing) {
            const heading = asked[key];

            // Every answer has a heading, whether or not it can be asked yet.
            expect([key, heading !== undefined]).toEqual([key, true]);

            // `roles` is the one that legitimately cannot be on screen yet: the choice is the image's to
            // offer, and no image has been interrogated here (there is no docker in this suite). Every
            // other missing answer must have its control in front of the operator right now.
            if (key === "roles") continue;

            expect([key, await shows(page, heading!)]).toEqual([key, true]);
        }

        await page.close();
    });
});

describe("the page, served under /panel", () => {
    /**
     * The bare path has to reach the page at all, and it only does because the edge sends it one level
     * down first. Without that redirect every relative URL on the page resolves one directory too high.
     */
    test("the bare path redirects to the trailing slash", async () => {
        const { page } = await open(mounted);

        expect(new URL(page.url()).pathname).toBe(`${PANEL_PATH}/`);

        await page.close();
    });

    /**
     * The whole point of the exercise: the same page, mounted somewhere else, still finds its API.
     *
     * A hardcoded `/api` passes every test that serves the page at the root, and fails only once the
     * instance is up and `/` has become Argon — by which time nobody is watching the install.
     */
    test("signing in works from the mounted path too", async () => {
        const { page, problems } = await open(mounted);
        const asked: string[] = [];

        page.on("request", (request) => {
            const path = new URL(request.url()).pathname;

            if (path.includes("/api/")) asked.push(path);
        });

        await signIn(page);

        expect(asked.length).toBeGreaterThan(0);

        for (const path of asked) expect([path, path.startsWith(`${PANEL_PATH}/api/`)]).toEqual([path, true]);

        expect(problems).toEqual([]);

        await page.close();
    });
});

/* ------------------------------------------------------------------------------------------------
 * Which screen a stage draws.
 *
 * Driven through the preview harness, which stubs the one fetch this page makes. Reaching `degraded`
 * for real would mean breaking an instance on purpose, and what is under test is what gets drawn.
 * ---------------------------------------------------------------------------------------------- */

describe("the screens", () => {
    let files: ReturnType<typeof Bun.serve>;

    beforeAll(() => {
        const types: Record<string, string> = {
            ".html": "text/html; charset=utf-8",
            ".js": "text/javascript; charset=utf-8",
            ".css": "text/css; charset=utf-8",
        };

        files = Bun.serve({
            port: 0,
            hostname: "127.0.0.1",
            async fetch(request) {
                const path = new URL(request.url).pathname.replace(/\/$/, "/index.html");
                const file = Bun.file(join(import.meta.dir, "..", path));

                if (!(await file.exists())) return new Response("not found", { status: 404 });

                return new Response(file, {
                    headers: { "content-type": types[path.slice(path.lastIndexOf("."))] ?? "text/plain" },
                });
            },
        });
    });

    afterAll(() => files?.stop(true));

    const screen = (name: string): string => `http://127.0.0.1:${files.port}/preview/?screen=${name}`;

    /**
     * The one that matters most. `degraded` means containers were created and the instance did not come
     * up, so trying again is not free — and this page must not offer to.
     *
     * It used to: every stage that was not explicitly handled fell through to the wizard, which ends in
     * an Install button. An operator looking at a broken live system was being invited to press it.
     */
    test("degraded offers no way to install again", async () => {
        const { page, problems } = await open(screen("degraded"));

        expect(await shows(page, "The instance did not come up")).toBe(true);
        expect(await shows(page, "not a clean retry")).toBe(true);
        expect(await page.locator("button", { hasText: "Install" }).count()).toBe(0);

        // What to do instead of a button.
        expect(await shows(page, "docker compose -p argon logs")).toBe(true);

        expect(problems).toEqual([]);

        await page.close();
    });

    test("a refused configuration shows the server's own words, and the questions under them", async () => {
        const { page, problems } = await open(screen("invalid"));

        expect(await shows(page, "What the server said")).toBe(true);
        expect(await shows(page, "Database:ConnectionString")).toBe(true);

        // Nothing was written, so the fix is to change an answer — and the answers are still there.
        expect(await shows(page, "Set up this instance")).toBe(true);

        expect(problems).toEqual([]);

        await page.close();
    });

    /**
     * Up is not the end of a wizard, it is the beginning of a panel.
     *
     * This used to assert the terminal screen of an install — "This instance is up" with a link — and
     * that screen stayed on it forever: an operator returning the next day saw the last frame of a film
     * that had ended, with no way through to anything. The install's final state is now the panel.
     */
    test("a running instance shows the panel, not the end of the install", async () => {
        const { page, problems } = await open(screen("running"));

        await page.waitForSelector("text=Services", { timeout: 10_000 });

        expect(await shows(page, "Certificates")).toBe(true);
        expect(await shows(page, "Backups")).toBe(true);
        expect(await shows(page, "argon-postgres")).toBe(true);

        expect(problems).toEqual([]);

        await page.close();
    });

    /**
     * The panel's own row carries no lifecycle buttons.
     *
     * Stopping it is the operator switching off the thing they are using, with no way back short of
     * ssh. `controllable()` in panel/containers.ts refuses it and the route enforces that; this is only
     * about not drawing a button whose single outcome is a refusal.
     */
    test("the panel offers no way to switch itself off", async () => {
        const { page } = await open(screen("running"));

        await page.waitForSelector("text=Services", { timeout: 10_000 });

        const row = page.locator('[data-service="argon-panel"]');

        expect(await row.locator("button", { hasText: "Stop" }).count()).toBe(0);
        expect(await row.locator("button", { hasText: "Restart" }).count()).toBe(0);

        // Its log is still readable — the refusal is about lifecycle, not about looking.
        expect(await row.locator("button", { hasText: "Logs" }).count()).toBe(1);

        await page.close();
    });

    /**
     * A certificate that does not cover the name it is served on is a real misconfiguration — an origin
     * certificate for the wrong host — and it reaches a visitor as a browser error nobody attributes to
     * it. The panel is the only place it is visible before that happens.
     */
    test("a certificate that covers the wrong name says so", async () => {
        const { page } = await open(screen("running"));

        await page.waitForSelector("text=Certificates", { timeout: 10_000 });

        expect(await shows(page, "neither of which is media.example.org")).toBe(true);

        // And it is not labelled valid. Its dates are fine, which is what the module reports; a name it
        // does not cover is not something an operator should have to read past a green badge to find.
        expect(await shows(page, "wrong name")).toBe(true);

        // And the one that is merely close to expiry is shown as that rather than as a failure.
        expect(await shows(page, "11 days left")).toBe(true);

        await page.close();
    });

    test("while it runs, the log and the services are both on screen", async () => {
        const { page, problems } = await open(screen("applying"));

        expect(await shows(page, "Pulling images and starting containers")).toBe(true);
        expect(await shows(page, "argon-postgres")).toBe(true);

        expect(problems).toEqual([]);

        await page.close();
    });

    /**
     * Every answer is in and the stage is still not `ready`. Printing the empty list gave
     * "Still to answer: ." and sent the operator hunting for a field that was not there.
     */
    test("nothing outstanding does not read as something outstanding", async () => {
        const { page } = await open(screen("ready"));

        expect(await shows(page, "Still to answer: .")).toBe(false);

        await page.close();
    });
});
