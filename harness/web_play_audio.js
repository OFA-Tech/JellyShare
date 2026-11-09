// Plays audio items (music/audiobook placeholders) in jellyfin-web (headless Chromium) on one harness server and prints
// one JSON line: per item the <audio> state after a few seconds and any browser request that leaked the relay address.
//   node harness/web_play_audio.js <server url> <item id> [<item id> ...]
// Used by harness/e2e.py (media types). Needs playwright-core (the global @playwright/cli ships one; override with
// PLAYWRIGHT_CORE=<path>) and a Chromium: set PLAYWRIGHT_CHROME to the headless shell
// (%LOCALAPPDATA%\ms-playwright\chromium_headless_shell-*\chrome-headless-shell-win64\chrome-headless-shell.exe).
// Signs in as the throwaway harness admin (admin/admin). Access keys in URLs are redacted from the output.
const core = process.env.PLAYWRIGHT_CORE || 'C:/nvm4w/nodejs/node_modules/@playwright/cli/node_modules/playwright-core';
const { chromium } = require(core);

const [base, ...items] = process.argv.slice(2);
const red = u => u.replace(/(ApiKey|api_key)=[^&]+/ig, '$1=REDACTED').slice(0, 220);

(async () => {
  const browser = await chromium.launch(process.env.PLAYWRIGHT_CHROME ? { executablePath: process.env.PLAYWRIGHT_CHROME } : {});
  const out = [];
  try {
    const page = await browser.newPage();
    const seen = [];
    page.on('request', r => { const u = r.url(); if (/Audio|PlaybackInfo|JellyShare|:8096|127\.0\.0\.1:8096/.test(u)) seen.push(r.method() + ' ' + red(u)); });
    page.on('requestfailed', r => seen.push('FAILED ' + red(r.url()) + ' ' + (r.failure() || {}).errorText));
    await page.goto(base + '/web/#/login');
    await page.waitForTimeout(3000);
    if (await page.getByRole('heading', { name: 'Please sign in' }).count()) {
      await page.locator('#txtManualName:visible').fill('admin');
      await page.locator('#txtManualPassword:visible').fill('admin');
      await page.getByRole('button', { name: 'Sign In' }).click();
    }
    await page.waitForTimeout(4000);
    for (const id of items) {
      seen.length = 0;
      await page.goto(base + '/web/#/details?id=' + id);
      await page.waitForTimeout(4000);
      await page.locator('.btnPlay:visible, .detailButton-primary:visible').first().click();
      await page.waitForTimeout(8000);
      const audio = await page.evaluate(() => {
        const a = document.querySelector('audio');
        return a ? { t: a.currentTime, paused: a.paused, err: a.error && a.error.code, dur: a.duration } : null;
      });
      out.push({ id, audio, leaks: seen.filter(s => /JellyShare\/v1\/play|:8096/.test(s)), requests: seen.slice(0, 8) });
      await page.evaluate(() => { const a = document.querySelector('audio'); if (a) a.pause(); });
    }
  } finally {
    await browser.close();
  }
  console.log(JSON.stringify(out));
})().catch(e => { console.log(JSON.stringify({ error: String(e).slice(0, 300) })); process.exit(1); });
