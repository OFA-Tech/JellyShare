// Spike Step 4: play an item in jellyfin-web (headless Chromium via playwright-cli run-code)
// and record which URLs the browser itself requests. Item id is set below by the caller.
async page => {
  const ITEM = globalThis.SPIKE_ITEM || '9a719cf2883824eea0034ca7f6ee6512';
  const seen = [];
  if (globalThis.SPIKE_REWRITE !== false) {
    // Simulate a server-side PlaybackInfo filter: no direct play of the .strm URL, hide the URL.
    await page.route('**/PlaybackInfo*', async route => {
      const resp = await route.fetch();
      const body = await resp.json();
      for (const ms of body.MediaSources || []) { if (ms.IsRemote) { ms.SupportsDirectPlay = false; ms.Path = ''; } }
      await route.fulfill({ response: resp, json: body });
    });
  }
  page.on('request', r => {
    const u = r.url();
    if (!/\.(js|css|woff2?|png|svg|json|ico)(\?|$)/.test(u) || u.includes('PlaybackInfo')) seen.push(r.method() + ' ' + u.replace(/ApiKey=[^&]+/i, 'ApiKey=REDACTED').slice(0, 200));
  });
  page.on('requestfailed', r => seen.push('FAILED ' + r.url().slice(0, 150) + ' ' + (r.failure() || {}).errorText));
  await page.goto('http://localhost:8101/web/#/login');
  await page.waitForTimeout(3000);
  if (await page.getByRole('heading', { name: 'Please sign in' }).count()) {
    await page.locator('#txtManualName:visible').fill('admin');
    await page.locator('#txtManualPassword:visible').fill('admin');
    await page.getByRole('button', { name: 'Sign In' }).click();
  }
  await page.waitForTimeout(4000);
  seen.length = 0;
  await page.goto('http://localhost:8101/web/#/details?id=' + ITEM);
  await page.waitForTimeout(4000);
  await page.locator('.btnPlay').first().click();
  await page.waitForTimeout(15000);
  const video = await page.evaluate(() => {
    const v = document.querySelector('video');
    return v ? { src: v.currentSrc.replace(/ApiKey=[^&]+/i, 'ApiKey=REDACTED').slice(0, 200), t: v.currentTime, paused: v.paused, err: v.error && v.error.code } : null;
  });
  return JSON.stringify({ video, requests: seen.filter(s => /Videos|PlaybackInfo|8900|127\.0\.0\.1|FAILED|Sessions\/Playing/.test(s)) }, null, 1);
}
