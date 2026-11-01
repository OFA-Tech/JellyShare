// Task 11 harness check: drives the JellyShare admin page in headless Chromium on alice and bob.
//   node harness/admin_page.js explore     # open the page on alice, print console output, screenshot
//   node harness/admin_page.js full        # create share on alice, add it on bob, test, sync, error cases
// Needs playwright-core (the global @playwright/cli ships one); override with PLAYWRIGHT_CORE=<path>.
// Tokens are written to harness/.secrets (gitignored) and never printed. Screenshots go to harness/screens/.
const fs = require('fs');
const path = require('path');
const core = process.env.PLAYWRIGHT_CORE || 'C:/nvm4w/nodejs/node_modules/@playwright/cli/node_modules/playwright-core';
const { chromium } = require(core);

const SERVERS = {
  alice: { url: 'http://127.0.0.1:8101', internal: 'http://alice:8096' },
  bob: { url: 'http://127.0.0.1:8102/jellyfin', internal: 'http://bob:8096/jellyfin' },
};
const SCREENS = path.join(__dirname, 'screens');
const SECRETS = path.join(__dirname, '.secrets');
fs.mkdirSync(SCREENS, { recursive: true });
fs.mkdirSync(SECRETS, { recursive: true });

async function login(page, base) {
  await page.goto(base + '/web/#/login');
  await page.waitForTimeout(3000);
  if (await page.getByRole('heading', { name: 'Please sign in' }).count()) {
    await page.locator('#txtManualName:visible').fill('admin');
    await page.locator('#txtManualPassword:visible').fill('admin');
    await page.getByRole('button', { name: 'Sign In' }).click();
  }
  await page.waitForTimeout(4000);
}

async function openAdmin(page, base, tab) {
  await page.goto(base + '/web/#/configurationpage?name=JellyShare');
  await page.waitForSelector('#JellySharePage .js-tab', { timeout: 20000 });
  await page.waitForTimeout(1500);
  if (tab) await page.click(`#JellySharePage .js-tab[data-tab="${tab}"]`);
}

const redact = s => String(s).replace(/js1_[A-Za-z0-9_-]+/g, 'js1_***');
const log = (...a) => console.log(...a.map(redact));
const shot = (page, name) => page.screenshot({ path: path.join(SCREENS, name + '.png'), fullPage: true });
const message = page => page.locator('#js-message').innerText();

async function main() {
  const mode = process.argv[2] || 'explore';
  // PLAYWRIGHT_CHROME: path to an installed chrome(-headless-shell).exe when playwright-core's own build is not installed.
  const browser = await chromium.launch(process.env.PLAYWRIGHT_CHROME ? { executablePath: process.env.PLAYWRIGHT_CHROME } : {});
  const ctx = await browser.newContext({ viewport: { width: 1280, height: 1000 } });
  const page = await ctx.newPage();
  page.on('console', m => { if (m.type() === 'error' || /JellyShare/.test(m.text())) log('console', m.type(), m.text().slice(0, 300)); });
  page.on('pageerror', e => log('pageerror', e.message.slice(0, 300)));
  page.on('dialog', d => d.accept());
  try {
    if (mode === 'explore') {
      await login(page, SERVERS.alice.url);
      await openAdmin(page, SERVERS.alice.url);
      await shot(page, 'explore-share');
      await page.click('#JellySharePage .js-tab[data-tab="receive"]');
      await shot(page, 'explore-receive');
      log('title text:', (await page.locator('#JellySharePage h2').first().innerText()));
    } else if (mode === 'full') {
      await full(browser);
    } else if (mode === 'setup') {
      await setupChecks(browser);
    } else if (mode === 'recheck') {
      await recheck(browser);
    } else if (mode === 'cleanup') {
      await cleanup(browser);
    }
  } finally {
    await browser.close();
  }
}

async function full(browser) {
  // --- alice: create a share for bob
  const a = await (await browser.newContext({ viewport: { width: 1280, height: 1000 } })).newPage();
  a.on('pageerror', e => log('alice pageerror', e.message.slice(0, 300)));
  a.on('dialog', d => d.accept());
  await login(a, SERVERS.alice.url);
  await openAdmin(a, SERVERS.alice.url, 'share');
  await a.fill('#js-new-name', 'bob (admin page test)');
  await a.fill('#js-new-streams', '3');
  await a.fill('#js-new-mbps', '8');
  // check every library
  for (const box of await a.locator('#js-new-libs input[data-lib]').all()) await box.check();
  await a.click('#js-create-share');
  await a.waitForSelector('#js-created:not(.js-hidden)', { timeout: 15000 });
  const token = await a.inputValue('#js-created-token');
  if (!/^js1_/.test(token)) throw new Error('no token shown');
  fs.writeFileSync(path.join(SECRETS, 'alice-share-bob-adminpage.token'), token);
  log('alice: token shown once (length', token.length + ', saved to harness/.secrets)');
  await shot(a, 'alice-token-shown');
  await a.click('#js-dismiss-token');
  await a.reload();
  await a.waitForSelector('#JellySharePage .js-tab');
  await a.waitForTimeout(1500);
  const visible = await a.locator('#js-created').isVisible();
  const leaked = (await a.content()).includes(token);
  log('alice: token box hidden after reload =', !visible, '; token in page html =', leaked);
  await shot(a, 'alice-after-reload');
  const sharesText = await a.locator('#js-shares').innerText();
  log('alice shares listed:', JSON.stringify(sharesText.slice(0, 120)));

  // --- bob: add alice, test, sync
  const b = await (await browser.newContext({ viewport: { width: 1280, height: 1200 } })).newPage();
  b.on('pageerror', e => log('bob pageerror', e.message.slice(0, 300)));
  b.on('dialog', d => d.accept());
  await login(b, SERVERS.bob.url);
  await openAdmin(b, SERVERS.bob.url, 'receive');
  await shot(b, 'bob-receive-before');
  const cases = [
    ['garbage', 'not a url', token],
    ['non-JellyShare (/web/)', SERVERS.alice.internal + '/web/', token],
    ['redirect (bob without its /jellyfin base url)', 'http://bob:8096', token],
    ['unreachable', 'http://127.0.0.1:1', token],
    ['wrong token', SERVERS.alice.internal, 'js1_definitely-not-a-valid-token'],
  ];
  for (const [label, url, tok] of cases) {
    await b.fill('#js-peer-url', url);
    await b.fill('#js-peer-token', tok);
    await b.click('#js-peer-test');
    await b.waitForFunction(() => /\S/.test(document.querySelector('#js-peer-test-result').textContent) && !/Testing/.test(document.querySelector('#js-peer-test-result').textContent), null, { timeout: 40000 });
    log(`bob test [${label}]:`, await b.locator('#js-peer-test-result').innerText());
    if (label === 'non-JellyShare (/web/)') await shot(b, 'bob-test-error');
  }
  await b.fill('#js-peer-name', 'alice (admin page test)');
  await b.fill('#js-peer-url', SERVERS.alice.internal);
  await b.fill('#js-peer-token', token);
  await b.click('#js-peer-test');
  await b.waitForFunction(() => /OK|\S{6,}/.test(document.querySelector('#js-peer-test-result').textContent) && !/Testing/.test(document.querySelector('#js-peer-test-result').textContent), null, { timeout: 40000 });
  log('bob test [alice, valid]:', await b.locator('#js-peer-test-result').innerText());
  await shot(b, 'bob-test-ok');
  await b.click('#js-peer-add');
  await b.waitForTimeout(2500);
  log('bob message after add:', await message(b));
  await b.click('#js-sync');
  await b.waitForFunction(() => !document.querySelector('#js-sync').disabled, null, { timeout: 180000 });
  await b.waitForTimeout(1500);
  log('bob message after sync:', await message(b));
  log('bob report:', (await b.locator('#js-report').innerText()).replace(/\n/g, ' | '));
  log('bob peers:', (await b.locator('#js-peers').innerText()).replace(/\n/g, ' | ').slice(0, 400));
  await shot(b, 'bob-after-sync');
  await b.reload();
  await b.waitForSelector('#JellySharePage .js-tab');
  await b.waitForTimeout(1500);
  await b.click('#JellySharePage .js-tab[data-tab="receive"]');
  log('bob report after reload:', (await b.locator('#js-report').innerText()).replace(/\n/g, ' | ').slice(0, 300));
  await shot(b, 'bob-after-reload');
}

const CLIENT = 'MediaBrowser Client="jellyshare-harness", Device="admin_page", DeviceId="jellyshare-harness-admin-page", Version="1.0"';

async function rest(server, method, urlPath, body, token) {
  const headers = { Authorization: token ? `${CLIENT}, Token="${token}"` : CLIENT };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const res = await fetch(SERVERS[server].url + urlPath, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${urlPath.split('?')[0]} -> ${res.status}`);
  return text ? JSON.parse(text) : null;
}

async function adminToken(server) {
  return (await rest(server, 'POST', '/Users/AuthenticateByName', { Username: 'admin', Pw: 'admin' })).AccessToken;
}

async function waitScan(server, token) {
  await new Promise(r => setTimeout(r, 3000));
  for (let i = 0; i < 90; i++) {
    const tasks = await rest(server, 'GET', '/ScheduledTasks', undefined, token);
    const scan = tasks.find(t => t.Key === 'RefreshLibrary');
    if (!scan || scan.State === 'Idle') return;
    await new Promise(r => setTimeout(r, 2000));
  }
  throw new Error('scan did not finish');
}

async function cardIndex(page, listSelector, name) {
  return page.evaluate(([sel, n]) => Array.from(document.querySelectorAll(sel + ' .js-card')).findIndex(c => {
    const i = c.querySelector('input[type=text]');
    return i && i.value === n;
  }), [listSelector, name]);
}

async function reopen(page, base, tab) {
  await page.reload();
  await page.waitForSelector('#JellySharePage .js-tab');
  await page.waitForTimeout(1500);
  if (tab) await page.click(`#JellySharePage .js-tab[data-tab="${tab}"]`);
}

// Attach (remove the Movies root from bob's library, re-add it with the page's button), series-grouping warning
// (turn the option off on bob's Shows library, look at the page, turn it back on), peer ordering, share edit.
async function setupChecks(browser) {
  const token = await adminToken('bob');
  const b = await (await browser.newContext({ viewport: { width: 1280, height: 1300 } })).newPage();
  b.on('pageerror', e => log('bob pageerror', e.message.slice(0, 300)));
  b.on('dialog', d => d.accept());
  await login(b, SERVERS.bob.url);

  const folders = await rest('bob', 'GET', '/Library/VirtualFolders', undefined, token);
  const root = folders.find(f => f.Name === 'Movies').Locations.find(l => l.endsWith('/placeholders/Movies'));
  if (!root) throw new Error('bob Movies library has no placeholder root');
  await rest('bob', 'DELETE', `/Library/VirtualFolders/Paths?name=Movies&path=${encodeURIComponent(root)}&refreshLibrary=false`, undefined, token);
  log('bob: removed the Movies placeholder root from the Movies library (setup for the attach check)');
  await openAdmin(b, SERVERS.bob.url, 'receive');
  log('bob roots before attach:', (await b.locator('#js-roots').innerText()).replace(/\n/g, ' | '));
  await shot(b, 'bob-attach-before');
  await b.locator('#js-roots button:has-text("Add to library")').first().click();
  await b.waitForFunction(() => /Added the Movies folder/.test(document.querySelector('#js-message').textContent), null, { timeout: 30000 });
  log('bob message after attach:', await message(b));
  await waitScan('bob', token);
  await reopen(b, SERVERS.bob.url, 'receive');
  log('bob roots after attach:', (await b.locator('#js-roots').innerText()).replace(/\n/g, ' | '));
  const again = await rest('bob', 'GET', '/Library/VirtualFolders', undefined, token);
  log('bob Movies library locations include the root again:', again.find(f => f.Name === 'Movies').Locations.includes(root));
  await shot(b, 'bob-attach-after');

  const shows = again.find(f => f.Name === 'Shows');
  const opts = shows.LibraryOptions;
  opts.EnableAutomaticSeriesGrouping = false;
  await rest('bob', 'POST', '/Library/VirtualFolders/LibraryOptions', { Id: shows.ItemId, LibraryOptions: opts }, token);
  try {
    await reopen(b, SERVERS.bob.url, 'receive');
    log('bob grouping warning visible:', await b.locator('#js-grouping-warning').isVisible(), '-', (await b.locator('#js-grouping-warning').innerText()).slice(0, 110));
    await shot(b, 'bob-grouping-warning');
  } finally {
    opts.EnableAutomaticSeriesGrouping = true;
    await rest('bob', 'POST', '/Library/VirtualFolders/LibraryOptions', { Id: shows.ItemId, LibraryOptions: opts }, token);
  }
  await reopen(b, SERVERS.bob.url, 'receive');
  log('bob grouping warning visible after restoring the option:', await b.locator('#js-grouping-warning').isVisible());

  const order = () => b.evaluate(() => Array.from(document.querySelectorAll('#js-peers .js-card')).map(c => c.querySelector('input[type=text]').value));
  log('bob peer order before:', JSON.stringify(await order()));
  await b.locator('#js-peers .js-card').first().locator('button:has-text("\u25BC")').click();
  await b.waitForTimeout(2000);
  log('bob peer order after moving first down:', JSON.stringify(await order()));
  await shot(b, 'bob-peers-reordered');
  await b.locator('#js-peers .js-card').first().locator('button:has-text("\u25BC")').click();
  await b.waitForTimeout(2000);
  log('bob peer order after moving it back:', JSON.stringify(await order()));

  const a = await (await browser.newContext({ viewport: { width: 1280, height: 1000 } })).newPage();
  a.on('dialog', d => d.accept());
  await login(a, SERVERS.alice.url);
  await openAdmin(a, SERVERS.alice.url, 'share');
  const card = a.locator('#js-shares .js-card').nth(await cardIndex(a, '#js-shares', 'bob (admin page test)'));
  await card.locator('input[type=number]').nth(0).fill('4');
  await card.locator('input[type=number]').nth(1).fill('2.5');
  await card.locator('input[type=checkbox]').nth(0).uncheck();
  await card.locator('button:has-text("Save")').click();
  await a.waitForFunction(() => /^Saved/.test(document.querySelector('#js-message').textContent), null, { timeout: 15000 });
  const cfg = await rest('alice', 'GET', '/Plugins/8a55d113-5072-47cf-9827-f3c49f1c3971/Configuration', undefined, await adminToken('alice'));
  const share = cfg.Shares.find(s => s.Name === 'bob (admin page test)');
  log('alice share after edit: MaxStreams', share.MaxStreams, 'MaxBitrate', share.MaxBitrate, 'Enabled', share.Enabled, 'Libraries', share.LibraryIds.length);
  await shot(a, 'alice-share-edited');
}

// Review fixes: a saved peer's Test button with an edited address must ask for the token; attach still works;
// empty Max streams / bitrate over 2000 Mbps are refused by the page.
async function recheck(browser) {
  const b = await (await browser.newContext({ viewport: { width: 1280, height: 1300 } })).newPage();
  b.on('pageerror', e => log('bob pageerror', e.message.slice(0, 300)));
  b.on('dialog', d => d.accept());
  const requests = [];
  b.on('request', r => { if (r.url().includes('/peers/test')) requests.push(r.postData() || ''); });
  await login(b, SERVERS.bob.url);
  await openAdmin(b, SERVERS.bob.url, 'receive');
  const card = b.locator('#js-peers .js-card').first();
  const urlInput = card.locator('input[type=text]').nth(1);
  const result = card.locator('p span.js-muted, p span').last();
  const testOnce = async label => {
    await card.locator('button:has-text("Test")').click();
    await b.waitForFunction(() => {
      const s = document.querySelector('#js-peers .js-card p:last-child > span:last-child');
      return s && /\S/.test(s.textContent) && !/Testing/.test(s.textContent);
    }, null, { timeout: 40000 });
    log(`bob peer Test [${label}]:`, await card.locator('p:last-child > span:last-child').innerText());
  };
  const saved = await urlInput.inputValue();
  await testOnce('saved address, stored token');
  await urlInput.fill('http://alice-typo:8096');
  await testOnce('edited address, token field empty');
  await shot(b, 'bob-test-edited-url');
  await urlInput.fill(saved);
  void result;
  log('bob peers/test request bodies carried a token:', requests.some(r => /"token":"[^"]/.test(r)));

  const token = await adminToken('bob');
  const folders = await rest('bob', 'GET', '/Library/VirtualFolders', undefined, token);
  const root = folders.find(f => f.Name === 'Movies').Locations.find(l => l.endsWith('/placeholders/Movies'));
  await rest('bob', 'DELETE', `/Library/VirtualFolders/Paths?name=Movies&path=${encodeURIComponent(root)}&refreshLibrary=false`, undefined, token);
  await reopen(b, SERVERS.bob.url, 'receive');
  await b.locator('#js-roots button:has-text("Add to library")').first().click();
  await b.waitForFunction(() => /Added the Movies folder/.test(document.querySelector('#js-message').textContent), null, { timeout: 30000 });
  await waitScan('bob', token);
  const again = await rest('bob', 'GET', '/Library/VirtualFolders', undefined, token);
  log('bob attach after the fix: Movies library has the root again:', again.find(f => f.Name === 'Movies').Locations.includes(root));

  const a = await (await browser.newContext({ viewport: { width: 1280, height: 1000 } })).newPage();
  await login(a, SERVERS.alice.url);
  await openAdmin(a, SERVERS.alice.url, 'share');
  await a.fill('#js-new-name', 'validation check');
  for (const box of await a.locator('#js-new-libs input[data-lib]').all()) await box.check();
  await a.fill('#js-new-streams', '');
  await a.click('#js-create-share');
  log('alice empty Max streams ->', await message(a));
  await a.fill('#js-new-streams', '2');
  await a.fill('#js-new-mbps', '2001');
  await a.click('#js-create-share');
  log('alice 2001 Mbps ->', await message(a));
  log('alice invitations after the two refusals:', await a.locator('#js-shares .js-card').count());
}

// Revokes the test invitation on alice and removes the test peer on bob through the page; syncs bob afterwards.
async function cleanup(browser) {
  const b = await (await browser.newContext({ viewport: { width: 1280, height: 1200 } })).newPage();
  b.on('dialog', d => d.accept());
  await login(b, SERVERS.bob.url);
  await openAdmin(b, SERVERS.bob.url, 'receive');
  for (let i = 0; i < 5; i++) {
    const idx = await cardIndex(b, '#js-peers', 'alice (admin page test)');
    if (idx < 0) break;
    await b.locator('#js-peers .js-card').nth(idx).locator('button:has-text("Remove")').click();
    await b.waitForTimeout(2500);
  }
  await b.click('#js-sync');
  await b.waitForFunction(() => !document.querySelector('#js-sync').disabled, null, { timeout: 180000 });
  await b.waitForTimeout(1500);
  log('bob peers left:', (await b.evaluate(() => Array.from(document.querySelectorAll('#js-peers .js-card')).map(c => c.querySelector('input[type=text]').value))).join(', '));
  log('bob report:', (await b.locator('#js-report').innerText()).replace(/\n/g, ' | '));

  const a = await (await browser.newContext({ viewport: { width: 1280, height: 1000 } })).newPage();
  a.on('dialog', d => d.accept());
  await login(a, SERVERS.alice.url);
  await openAdmin(a, SERVERS.alice.url, 'share');
  for (let i = 0; i < 5; i++) {
    const idx = await cardIndex(a, '#js-shares', 'bob (admin page test)');
    if (idx < 0) break;
    await a.locator('#js-shares .js-card').nth(idx).locator('button:has-text("Revoke")').click();
    await a.waitForTimeout(2500);
  }
  log('alice shares left:', (await a.evaluate(() => Array.from(document.querySelectorAll('#js-shares .js-card')).map(c => c.querySelector('input[type=text]').value))).join(', '));
  try { fs.unlinkSync(path.join(SECRETS, 'alice-share-bob-adminpage.token')); } catch (e) { /* already gone */ }
}

main().catch(e => { console.error(redact(e.stack || e)); process.exit(1); });
