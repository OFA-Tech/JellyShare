// Task 11 harness check: drives the JellyShare admin page in headless Chromium on alice and bob.
//   node harness/admin_page.js explore     # open the page on alice, print console output, screenshot
//   node harness/admin_page.js account     # alice adds bob as an account peer (throwaway bob user), edits, removes
//   node harness/admin_page.js full        # create share on alice, add it on bob, test, sync, error cases
//   node harness/admin_page.js types       # media types: default sharing, invitation types, receive row (alice, restored after)
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
    } else if (mode === 'account') {
      await accountPeer(browser);
    } else if (mode === 'types') {
      await typesCheck(browser);
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
  await a.fill('#js-new-streams', '');
  await a.click('#js-create-share');
  log('alice empty Max streams ->', await message(a));
  await a.fill('#js-new-streams', '2');
  await a.fill('#js-new-mbps', '2001');
  await a.click('#js-create-share');
  log('alice 2001 Mbps ->', await message(a));
  log('alice invitations after the two refusals:', await a.locator('#js-shares .js-card').count());
}

function check(label, ok) {
  log(`${ok ? 'PASS' : 'FAIL'}: ${label}`);
  if (!ok) process.exitCode = 1;
}

// Account peer on the admin page: alice adds bob through a throwaway bob user (created here through bob's admin API
// and deleted at the end; its password is random and never printed), looks at the list, edits it, removes it.
//   node harness/admin_page.js account
async function accountPeer(browser) {
  const crypto = require('crypto');
  const bobAdmin = await adminToken('bob');
  const user = 'jsacct-' + crypto.randomBytes(4).toString('hex');
  const pw = crypto.randomBytes(12).toString('base64url');
  const created = await rest('bob', 'POST', '/Users/New', { Name: user, Password: pw }, bobAdmin);
  const peerName = 'bob account (admin page test)';
  const a = await (await browser.newContext({ viewport: { width: 1280, height: 1300 } })).newPage();
  a.on('pageerror', e => log('alice pageerror', e.message.slice(0, 300)));
  a.on('dialog', d => d.accept());
  const bodies = [];
  a.on('request', r => { if (/\/admin\/peers/.test(r.url()) && r.postData()) bodies.push(r.method() + ' ' + r.postData()); });
  const cardFor = async () => a.locator('#js-peers .js-card').nth(await cardIndex(a, '#js-peers', peerName));
  try {
    await login(a, SERVERS.alice.url);
    await openAdmin(a, SERVERS.alice.url, 'receive');
    check('add form offers an account kind', await a.locator('#js-peer-kind option[value="Account"]').count() === 1);
    check('account fields hidden for the default kind', !(await a.locator('#js-peer-username').isVisible()));
    await a.selectOption('#js-peer-kind', 'Account');
    check('token field hidden for an account', !(await a.locator('#js-peer-token').isVisible()));
    await a.fill('#js-peer-name', peerName);
    await a.fill('#js-peer-url', SERVERS.bob.internal);
    await a.fill('#js-peer-username', user);
    await a.fill('#js-peer-password', pw);
    check('password input is type=password', await a.getAttribute('#js-peer-password', 'type') === 'password');
    await a.click('#js-peer-test');
    await a.waitForFunction(() => { const t = document.querySelector('#js-peer-test-result').textContent; return /\S/.test(t) && !/Testing/.test(t); }, null, { timeout: 40000 });
    const tested = await a.locator('#js-peer-test-result').innerText();
    check('test connection works for an account: ' + redact(tested).slice(0, 80), /^OK/.test(tested));
    await a.click('#js-peer-add');
    await a.waitForFunction(() => /Peer added/.test(document.querySelector('#js-message').textContent), null, { timeout: 40000 });
    check('password cleared after the add', (await a.inputValue('#js-peer-password')) === '');
    await a.waitForFunction(n => Array.from(document.querySelectorAll('#js-peers .js-card input[type=text]')).some(i => i.value === n), peerName, { timeout: 20000 });
    const c = await cardFor();
    const text = await c.innerText();
    check('list shows the kind "Jellyfin account"', text.includes('Jellyfin account'));
    check('list shows the user name', text.includes(user));
    check('list shows "max 1 stream"', /max 1 stream\b/.test(text));
    check('no sign-in failed badge', !/Sign-in failed/.test(text));
    check('the password is not in the page', !(await a.content()).includes(pw));
    await shot(a, 'account-peer');

    await c.locator('input[type=number]').fill('2');
    await c.locator('select:has(option[value=StopOldest])').selectOption('StopOldest');
    await c.locator('button:has-text("Save")').click();
    await a.waitForFunction(() => /^Saved/.test(document.querySelector('#js-message').textContent), null, { timeout: 40000 });
    await a.waitForFunction(() => /max 2 streams/.test(document.querySelector('#js-peers').innerText), null, { timeout: 20000 });
    const edited = await (await cardFor()).innerText();
    check('edit shows "max 2 streams"', /max 2 streams/.test(edited));
    check('edit keeps the limit action', (await (await cardFor()).locator('select:has(option[value=StopOldest])').inputValue()) === 'StopOldest');
    check('save request carried the password only when typed', bodies.filter(b => b.startsWith('PUT')).every(b => !b.includes(pw)));

    // wrong password on bob -> the next sync marks the peer, the page offers "Sign in again"
    await rest('bob', 'POST', `/Users/${created.Id}/Password`, { NewPw: pw + 'x', ResetPassword: false }, bobAdmin);
    await a.click('#js-sync');
    await a.waitForFunction(() => !document.querySelector('#js-sync').disabled, null, { timeout: 180000 });
    await reopen(a, SERVERS.alice.url, 'receive');
    const failed = await (await cardFor()).innerText();
    check('a changed password shows "Sign-in failed"', /Sign-in failed/.test(failed));
    check('and a "Sign in again" button', /Sign in again/.test(failed));
    await shot(a, 'account-peer-failed');
    await (await cardFor()).locator('input[type=password]').fill(pw + 'x');
    await (await cardFor()).locator('button:has-text("Sign in again")').click();
    await a.waitForFunction(() => /Signed in/.test(document.querySelector('#js-message').textContent), null, { timeout: 40000 });
    // The badge reflects the last sync's error, so it goes away with the next successful sync.
    await a.click('#js-sync');
    await a.waitForTimeout(1000);
    await a.waitForFunction(() => !document.querySelector('#js-sync').disabled, null, { timeout: 180000 });
    await reopen(a, SERVERS.alice.url, 'receive');
    check('after signing in again and a sync the badge is gone', !/Sign-in failed/.test(await (await cardFor()).innerText()));
  } finally {
    try {
      await reopen(a, SERVERS.alice.url, 'receive');
      for (let i = 0; i < 3; i++) {
        const idx = await cardIndex(a, '#js-peers', peerName);
        if (idx < 0) break;
        await a.locator('#js-peers .js-card').nth(idx).locator('button:has-text("Remove")').click();
        await a.waitForTimeout(2500);
      }
    } catch (e) { log('cleanup of the alice peer failed:', e.message); }
    await rest('bob', 'DELETE', `/Users/${created.Id}`, undefined, bobAdmin).catch(e => log('could not delete the bob user:', e.message));
  }
}

// Media types on the admin page (alice): default sharing, invitation types, the legacy share, the receive row of a peer.
// The plugin configuration is snapshotted first and written back at the end, so alice is left as it was found.
//   node harness/admin_page.js types
const PLUGIN_ID = '8a55d113-5072-47cf-9827-f3c49f1c3971';

async function typesCheck(browser) {
  const admin = await adminToken('alice');
  const configUrl = `/Plugins/${PLUGIN_ID}/Configuration`;
  const snapshot = await rest('alice', 'GET', configUrl, undefined, admin);
  const status = () => rest('alice', 'GET', '/JellyShare/v1/admin/status', undefined, admin);
  const a = await (await browser.newContext({ viewport: { width: 1280, height: 1600 } })).newPage();
  a.on('pageerror', e => log('alice pageerror', e.message.slice(0, 300)));
  a.on('dialog', d => d.accept());
  const toast = async re => a.waitForFunction(r => new RegExp(r).test(document.querySelector('#js-message').textContent), re.source, { timeout: 15000 });
  const cardOf = async (list, name) => a.locator(`${list} .js-card`).nth(await cardIndex(a, list, name));
  const tick = (scope, type) => scope.locator(`input[data-type="${type}"]`);
  const flags = async (scope, types) => Promise.all(types.map(async t => `${t}:${await tick(scope, t).isChecked() ? 'on' : 'off'}${await tick(scope, t).isDisabled() ? '/locked' : ''}`));
  const setup = async tab => { await reopen(a, SERVERS.alice.url, tab); await a.waitForTimeout(500); };
  try {
    await login(a, SERVERS.alice.url);
    await openAdmin(a, SERVERS.alice.url, 'share');
    const ds = a.locator('#js-default-sharing');
    const found = await ds.locator('input[data-type]').first().waitFor({ timeout: 5000 }).then(() => true, () => false);
    check('the Share tab has a "Default sharing" card with ticks', found);
    if (!found) return;
    const all7 = ['Movies', 'Shows', 'Music', 'Audiobooks', 'Ebooks', 'Collections', 'LiveTv'];
    const unsupported = ['Music', 'Audiobooks', 'Ebooks', 'Collections', 'LiveTv'];
    const allBox = ds.locator('input:not([data-type])');
    check('7 ticks', await ds.locator('input[data-type]').count() === 7);
    check('"All media types" is on', await allBox.isChecked());
    check('in All every tick is on and locked', (await flags(ds, all7)).every(f => f.endsWith('on/locked')));
    check('unsupported types say "coming later"', (await ds.innerText()).split('(coming later)').length - 1 === unsupported.length);
    check('no library picker on the page', await a.locator('#js-new-libs, input[data-lib]').count() === 0);
    check('the account note appears once', (await a.locator('#JellySharePage').innerText()).split('apply to invitations').length === 2);

    await allBox.uncheck();
    check('after unticking All Movies and Shows are editable, the rest stay locked',
      !(await tick(ds, 'Movies').isDisabled()) && !(await tick(ds, 'Shows').isDisabled()) && (await Promise.all(unsupported.map(t => tick(ds, t).isDisabled()))).every(Boolean));
    await tick(ds, 'Shows').uncheck();
    await tick(ds, 'Movies').uncheck();
    await ds.locator('button:has-text("Save")').click();
    await toast(/Pick at least one media type to share\./);
    check('saving an empty list is refused with the share message', true);
    await tick(ds, 'Movies').check();
    await ds.locator('button:has-text("Save")').click();
    await toast(/Default sharing saved/);
    await setup('share');
    check('the default list persists after a reload', !(await allBox.isChecked()) && (await flags(ds, ['Movies', 'Shows'])).join() === 'Movies:on,Shows:off');
    const st = await status();
    log('status.defaultSharing =', JSON.stringify(st.defaultSharing ?? st.DefaultSharing), '; supportedTypes =', JSON.stringify(st.supportedTypes ?? st.SupportedTypes));

    const create = a.locator('#js-new-types');
    check('the create form offers "Use the default" (preselected) and "Custom"',
      (await create.locator('select').inputValue()) === 'all' && (await create.locator('option').allInnerTexts()).join('|') === 'Use the default|Custom');
    check('with the default, the create form shows the default list locked', (await flags(create, ['Movies', 'Shows'])).join() === 'Movies:on/locked,Shows:off/locked');
    await a.fill('#js-new-name', 'types check default');
    await a.click('#js-create-share');
    await a.waitForSelector('#js-created:not(.js-hidden)', { timeout: 15000 });
    await a.click('#js-dismiss-token');
    await create.locator('select').selectOption('list');
    await tick(create, 'Shows').check();
    await tick(create, 'Movies').uncheck();
    await a.fill('#js-new-name', 'types check custom');
    await a.click('#js-create-share');
    await a.waitForSelector('#js-created:not(.js-hidden)', { timeout: 15000 });
    await a.click('#js-dismiss-token');
    await setup('share');
    const sh = (await status()).shares ?? (await status()).Shares;
    log('shares:', JSON.stringify(sh.map(s => ({ name: s.name ?? s.Name, useDefaultTypes: s.useDefaultTypes ?? s.UseDefaultTypes, types: s.types ?? s.Types }))));

    const cDefault = await cardOf('#js-shares', 'types check default');
    check('a default share shows "Use the default", locked, with the default list', (await cDefault.locator('select').inputValue()) === 'all' && (await flags(cDefault, ['Movies', 'Shows'])).join() === 'Movies:on/locked,Shows:off/locked');
    const cCustom = await cardOf('#js-shares', 'types check custom');
    check('a custom share shows Custom with Shows only', (await cCustom.locator('select').inputValue()) === 'list' && (await flags(cCustom, ['Movies', 'Shows'])).join() === 'Movies:off,Shows:on');
    const cLegacy = await cardOf('#js-shares', 'bob');
    check('the legacy share shows Custom with Movies and Shows ticked', (await cLegacy.locator('select').inputValue()) === 'list' && (await flags(cLegacy, ['Movies', 'Shows'])).join() === 'Movies:on,Shows:on');

    await cCustom.locator('select').selectOption('all');
    await cCustom.locator('button:has-text("Save")').click();
    await toast(/^Saved/);
    await setup('share');
    check('switching a share to the default persists', (await (await cardOf('#js-shares', 'types check custom')).locator('select').inputValue()) === 'all');
    const cDefault2 = await cardOf('#js-shares', 'types check default');
    await cDefault2.locator('select').selectOption('list');
    await tick(cDefault2, 'Movies').uncheck();
    await cDefault2.locator('button:has-text("Save")').click();
    await toast(/Pick at least one media type to share\./);
    check('an empty Custom list on a share is refused', true);

    await a.click('#JellySharePage .js-tab[data-tab="receive"]');
    const peer = await cardOf('#js-peers', 'bob');
    check('a peer starts on "Everything they share" with every tick locked', (await peer.locator('select').inputValue()) === 'all' && (await peer.locator('option').allInnerTexts()).join('|') === 'Everything they share|Only:' && (await flags(peer, all7)).every(f => f.endsWith('on/locked')));
    check('the receive row is labelled "Receive"', (await peer.innerText()).includes('Receive'));
    await peer.locator('select').selectOption('list');
    check('Only: unlocks Movies and Shows', !(await tick(peer, 'Movies').isDisabled()) && !(await tick(peer, 'Shows').isDisabled()));
    await tick(peer, 'Shows').uncheck();
    await tick(peer, 'Movies').uncheck();
    await peer.locator('button:has-text("Save")').click();
    await toast(/Pick at least one media type to receive\./);
    check('an empty Only: list is refused with the receive message', true);
    await tick(peer, 'Movies').check();
    await peer.locator('button:has-text("Save")').click();
    await toast(/^Saved/);
    await setup('receive');
    const peer2 = await cardOf('#js-peers', 'bob');
    check('Only Movies persists after a reload', (await peer2.locator('select').inputValue()) === 'list' && (await flags(peer2, ['Movies', 'Shows'])).join() === 'Movies:on,Shows:off');
    const p = ((await status()).peers ?? (await status()).Peers)[0];
    log('peer acceptAllTypes =', JSON.stringify(p.acceptAllTypes ?? p.AcceptAllTypes), '; acceptTypes =', JSON.stringify(p.acceptTypes ?? p.AcceptTypes));
    await a.click('#JellySharePage .js-tab[data-tab="share"]');
    await shot(a, 'media-types');
  } finally {
    await rest('alice', 'POST', configUrl, snapshot, admin).then(() => log('alice configuration restored'), e => log('RESTORE FAILED:', e.message));
  }
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
