// Task 9 gate (c): harness/spike/web_play.js on bob, without any client-side rewrite.
// Plays the Big Buck Bunny placeholder in jellyfin-web (headless Chromium) and records which URLs the
// browser itself requests. Set ITEM to the placeholder id printed by harness/relay_gate.py, then:
//   playwright-cli open
//   playwright-cli run-code --filename=harness/relay_web_play.js
// Afterwards check bob's /config/log for new FFmpeg.Transcode-*.log files (expect none with libx264).
async page => {
  const BASE = 'http://localhost:8102/jellyfin';
  const ITEM = '2ae23e938ae810c2114e157b568c6605';
  const seen = [];
  page.on('request', r => {
    const u = r.url();
    if (!/\.(js|css|woff2?|png|svg|json|ico)(\?|$)/.test(u) || u.includes('PlaybackInfo')) seen.push(r.method() + ' ' + u.replace(/ApiKey=[^&]+/i, 'ApiKey=REDACTED').slice(0, 200));
  });
  page.on('requestfailed', r => seen.push('FAILED ' + r.url().replace(/ApiKey=[^&]+/i, 'ApiKey=REDACTED').slice(0, 150) + ' ' + (r.failure() || {}).errorText));
  await page.goto(BASE + '/web/#/login');
  await page.waitForTimeout(3000);
  if (await page.getByRole('heading', { name: 'Please sign in' }).count()) {
    await page.locator('#txtManualName:visible').fill('admin');
    await page.locator('#txtManualPassword:visible').fill('admin');
    await page.getByRole('button', { name: 'Sign In' }).click();
  }
  await page.waitForTimeout(4000);
  seen.length = 0;
  await page.goto(BASE + '/web/#/details?id=' + ITEM);
  await page.waitForTimeout(4000);
  await page.locator('.btnPlay').first().click();
  await page.waitForTimeout(12000);
  const before = await page.evaluate(() => { const v = document.querySelector('video'); return v ? v.currentTime : null; });
  await page.evaluate(() => { const v = document.querySelector('video'); if (v) { v.currentTime = 90; } });
  await page.waitForTimeout(6000);
  const video = await page.evaluate(() => {
    const v = document.querySelector('video');
    return v ? { src: v.currentSrc.replace(/ApiKey=[^&]+/i, 'ApiKey=REDACTED').slice(0, 200), t: v.currentTime, paused: v.paused, err: v.error && v.error.code } : null;
  });
  const all = seen.filter(s => /Videos|videos|PlaybackInfo|127\.0\.0\.1|JellyShare|FAILED|Sessions\/Playing/.test(s));
  return JSON.stringify({
    timeBeforeSeek: before,
    video,
    browserRequestsTo127: seen.filter(s => s.includes('127.0.0.1')).length,
    requests: all.slice(0, 25),
  }, null, 1);
}
