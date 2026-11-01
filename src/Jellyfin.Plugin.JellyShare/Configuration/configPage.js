(function () {
    'use strict';

    var page = document.getElementById('JellySharePage');
    if (!page || page.__jellyShareBound) {
        return;
    }
    page.__jellyShareBound = true;

    var BASE = 'JellyShare/v1/admin/';
    var status = null;
    var refreshing = false;
    var pollTimer = null;
    var messageTimer = null;

    function $(id) {
        return page.querySelector('#' + id);
    }

    // ---- DOM helpers: every dynamic string goes through textContent / value, never innerHTML ----

    function el(tag, attrs, children) {
        var e = document.createElement(tag);
        Object.keys(attrs || {}).forEach(function (k) {
            var v = attrs[k];
            if (v === null || v === undefined || v === false) {
                return;
            }
            if (k === 'class') {
                e.className = v;
            } else if (k === 'text') {
                e.textContent = v;
            } else if (k === 'value' || k === 'checked' || k === 'disabled') {
                e[k] = v;
            } else if (k.indexOf('on') === 0) {
                e.addEventListener(k.slice(2), v);
            } else {
                e.setAttribute(k, v === true ? '' : v);
            }
        });
        (children || []).forEach(function (c) {
            if (c === null || c === undefined) {
                return;
            }
            e.appendChild(typeof c === 'string' ? document.createTextNode(c) : c);
        });
        return e;
    }

    function clear(node) {
        while (node.firstChild) {
            node.removeChild(node.firstChild);
        }
    }

    function field(labelText, input, cls) {
        return el('div', { class: 'js-field' + (cls ? ' ' + cls : '') }, [el('label', { text: labelText }), input]);
    }

    function btn(text, cls, onclick) {
        return el('button', { type: 'button', class: 'js-btn' + (cls ? ' ' + cls : ''), text: text, onclick: onclick });
    }

    // The API answers PascalCase; accept either by lower-casing the first letter of every key.
    function camel(o) {
        if (Array.isArray(o)) {
            return o.map(camel);
        }
        if (o && typeof o === 'object') {
            var r = {};
            Object.keys(o).forEach(function (k) {
                r[k.charAt(0).toLowerCase() + k.slice(1)] = camel(o[k]);
            });
            return r;
        }
        return o;
    }

    function fmtTime(v) {
        if (!v) {
            return 'never';
        }
        var d = new Date(v);
        return isNaN(d.getTime()) ? String(v) : d.toLocaleString();
    }

    function mbpsText(bps) {
        return bps ? String(Math.round(bps / 1e5) / 10) : '';
    }

    function bpsFromMbps(text) {
        var t = String(text || '').trim();
        if (t === '') {
            return null;
        }
        var n = Number(t);
        return isFinite(n) && n > 0 ? Math.round(n * 1e6) : NaN;
    }

    // ---- API ----

    function errorText(e) {
        if (e && typeof e.text === 'function') {
            return e.text().then(function (t) {
                try {
                    var j = JSON.parse(t);
                    if (j && (j.error || j.Error)) {
                        return String(j.error || j.Error);
                    }
                    if (j && j.title) {
                        return String(j.title);
                    }
                } catch (x) { /* not JSON */ }
                return (e.status ? 'HTTP ' + e.status : 'Request failed') + (t ? ': ' + t.slice(0, 200) : '');
            });
        }
        return Promise.resolve(e && e.message ? e.message : 'Request failed');
    }

    function api(method, path, body) {
        var opts = { type: method, url: ApiClient.getUrl(BASE + path), dataType: 'text' };
        if (body !== undefined) {
            opts.data = JSON.stringify(body);
            opts.contentType = 'application/json';
        }
        return ApiClient.ajax(opts).then(
            function (text) {
                if (!text) {
                    return null;
                }
                return camel(JSON.parse(text));
            },
            function (e) {
                return errorText(e).then(function (m) {
                    throw new Error(m);
                });
            });
    }

    // ---- messages ----

    function showMessage(kind, text) {
        var box = $('js-message');
        box.className = 'js-msg ' + kind;
        box.textContent = text;
        clearTimeout(messageTimer);
        if (kind === 'ok') {
            messageTimer = setTimeout(hideMessage, 8000);
        }
    }

    function hideMessage() {
        $('js-message').className = 'js-msg js-hidden';
    }

    function fail(e) {
        showMessage('error', e && e.message ? e.message : String(e));
    }

    function busy(button, promise) {
        button.disabled = true;
        return promise.then(function (r) {
            button.disabled = false;
            return r;
        }, function (e) {
            button.disabled = false;
            throw e;
        });
    }

    // ---- libraries ----

    function libraryChecks(prefix, selected) {
        var box = el('div', {});
        var libs = (status && status.libraries) || [];
        var any = false;
        libs.forEach(function (l) {
            if (l.collectionType !== 'movies' && l.collectionType !== 'tvshows') {
                return;
            }
            any = true;
            box.appendChild(el('label', { class: 'js-check' }, [
                el('input', { type: 'checkbox', 'data-lib': l.id, checked: selected.indexOf(l.id) >= 0 }),
                l.name
            ]));
        });
        if (!any) {
            box.appendChild(el('span', { class: 'js-muted', text: 'No movie or show libraries found.' }));
        }
        return box;
    }

    function checkedLibs(box) {
        return Array.prototype.map.call(box.querySelectorAll('input[data-lib]:checked'), function (i) {
            return i.getAttribute('data-lib');
        });
    }

    // ---- share tab ----

    function renderNewLibs() {
        var holder = $('js-new-libs');
        var keep = checkedLibs(holder);
        clear(holder);
        holder.appendChild(libraryChecks('new', keep));
    }

    function renderShares() {
        var holder = $('js-shares');
        clear(holder);
        var shares = (status && status.shares) || [];
        if (!shares.length) {
            holder.appendChild(el('p', { class: 'js-muted', text: 'No invitations yet. Create one above and send the token to your friend.' }));
            return;
        }
        shares.forEach(function (s) {
            var name = el('input', { type: 'text', value: s.name, maxlength: '100' });
            var streams = el('input', { type: 'number', value: String(s.maxStreams), min: '1', max: '100' });
            var mbps = el('input', { type: 'number', value: mbpsText(s.maxBitrate), min: '0.1', step: '0.1', placeholder: 'no limit' });
            var enabled = el('input', { type: 'checkbox', checked: s.enabled });
            var libs = el('div', {}, [libraryChecks(s.id, s.libraryIds || [])]);
            var active = el('span', { 'data-active': s.id, text: String(s.activeStreams) });
            var save = btn('Save', 'primary', function () {
                var bps = bpsFromMbps(mbps.value);
                if (isNaN(bps)) {
                    return showMessage('error', 'Max bitrate must be a positive number of Mbps, or empty for no limit.');
                }
                busy(save, api('PUT', 'shares/' + encodeURIComponent(s.id), {
                    name: name.value,
                    libraryIds: checkedLibs(libs),
                    maxStreams: parseInt(streams.value, 10),
                    maxBitrate: bps,
                    enabled: enabled.checked
                })).then(function () {
                    showMessage('ok', 'Saved "' + name.value.trim() + '".');
                    return refresh();
                }).catch(fail);
            });
            var revoke = btn('Revoke', 'danger', function () {
                if (!window.confirm('Revoke "' + s.name + '"? Their server loses access immediately and their placeholders will disappear at their next sync.')) {
                    return;
                }
                busy(revoke, api('DELETE', 'shares/' + encodeURIComponent(s.id))).then(function () {
                    showMessage('ok', 'Revoked "' + s.name + '".');
                    return refresh();
                }).catch(fail);
            });
            holder.appendChild(el('div', { class: 'js-card' }, [
                el('div', { class: 'js-row' }, [
                    field('Name', name, 'grow'),
                    field('Max streams', streams),
                    field('Max bitrate (Mbps)', mbps),
                    el('label', { class: 'js-check' }, [enabled, 'Enabled'])
                ]),
                field('Libraries', libs),
                el('p', {}, [
                    el('span', { class: 'js-muted' }, ['Streams playing now: ', active, ' of ' + s.maxStreams + '  ']),
                    save, ' ', revoke
                ])
            ]));
        });
    }

    function createShare() {
        var button = $('js-create-share');
        var bps = bpsFromMbps($('js-new-mbps').value);
        if (isNaN(bps)) {
            return showMessage('error', 'Max bitrate must be a positive number of Mbps, or empty for no limit.');
        }
        busy(button, api('POST', 'shares', {
            name: $('js-new-name').value,
            libraryIds: checkedLibs($('js-new-libs')),
            maxStreams: parseInt($('js-new-streams').value, 10),
            maxBitrate: bps
        })).then(function (created) {
            $('js-created-name').textContent = created.name;
            $('js-created-token').value = created.token;
            $('js-created').classList.remove('js-hidden');
            $('js-new-name').value = '';
            $('js-new-mbps').value = '';
            hideMessage();
            return refresh();
        }).catch(fail);
    }

    function dismissToken() {
        $('js-created-token').value = '';
        $('js-created').classList.add('js-hidden');
    }

    function copyToken() {
        var input = $('js-created-token');
        var done = function () {
            showMessage('ok', 'Token copied. Keep it somewhere safe until you have sent it.');
        };
        if (navigator.clipboard && window.isSecureContext) {
            navigator.clipboard.writeText(input.value).then(done, function () {
                input.select();
                showMessage('warn', 'Press Ctrl+C to copy the selected token.');
            });
            return;
        }
        input.focus();
        input.select();
        try {
            if (document.execCommand('copy')) {
                return done();
            }
        } catch (x) { /* fall through */ }
        showMessage('warn', 'Press Ctrl+C to copy the selected token.');
    }

    // ---- receive tab ----

    function attachControl(type, root) {
        var box = el('div', { class: 'js-card' }, [
            el('h4', { text: type + ' placeholder folder' }),
            el('p', {}, [el('code', { text: root.path })])
        ]);
        if (root.attached) {
            box.appendChild(el('p', { text: 'Added to the library "' + root.libraryName + '".' }));
            return box;
        }
        var kind = type === 'Movies' ? 'movies' : 'tvshows';
        var libs = ((status && status.libraries) || []).filter(function (l) {
            return l.collectionType === kind;
        });
        if (!libs.length) {
            box.appendChild(el('p', { class: 'js-muted', text: 'Create a ' + type + ' library in Dashboard > Libraries first, then come back here.' }));
            return box;
        }
        var select = el('select', {}, libs.map(function (l) {
            return el('option', { value: l.id, text: l.name });
        }));
        var button = btn('Add to library', 'primary', function () {
            busy(button, api('POST', 'libraries/attach', { libraryId: select.value, type: type })).then(function () {
                showMessage('ok', 'Added the ' + type + ' folder. Jellyfin is scanning the library now.');
                return refresh();
            }).catch(fail);
        });
        box.appendChild(el('p', {}, [
            'Not in a library yet. ',
            select,
            ' ',
            button
        ]));
        return box;
    }

    function renderSetup() {
        var holder = $('js-roots');
        clear(holder);
        if (!status) {
            return;
        }
        holder.appendChild(attachControl('Movies', status.movies));
        holder.appendChild(attachControl('Shows', status.shows));
        var warning = $('js-grouping-warning');
        if (status.shows.attached && status.shows.automaticSeriesGrouping === false) {
            warning.textContent = 'The library "' + status.shows.libraryName + '" has "Automatically merge series that are spread across multiple folders" turned off. ' +
                'Episodes from peers would show up as separate series. Turn it on in Dashboard > Libraries > ' + status.shows.libraryName + ' > Manage library > Settings.';
            warning.classList.remove('js-hidden');
        } else {
            warning.classList.add('js-hidden');
        }
    }

    function renderReport() {
        var holder = $('js-report');
        clear(holder);
        var r = status && status.lastSync;
        if (!r) {
            holder.appendChild(el('p', { class: 'js-muted', text: 'No sync has run yet.' }));
            return;
        }
        var stats = [
            ['Titles offered', r.offered],
            ['Placeholders written', r.written],
            ['Placeholders removed', r.deleted],
            ['Skipped (you already own them)', r.skippedLocal],
            ['Without usable ids', r.unmatched],
            ['Conflicting ids', r.conflicts],
            ['Unsafe names skipped', r.skipped],
            ['Scans waiting', r.pendingScans]
        ];
        var box = el('p', { class: 'js-stats' }, stats.map(function (s) {
            return el('span', {}, [s[0] + ': ', el('strong', { text: String(s[1] || 0) })]);
        }));
        holder.appendChild(el('p', { class: 'js-muted', text: 'Last sync finished ' + fmtTime(r.finishedUtc) + '.' }));
        holder.appendChild(box);
        (r.peerErrors || []).forEach(function (line) {
            holder.appendChild(el('div', { class: 'js-msg error', text: line }));
        });
    }

    function peerStatusText(p) {
        if (p.lastError) {
            return { cls: 'js-msg error', text: 'Failing since ' + fmtTime(p.failingSinceUtc) + ': ' + p.lastError };
        }
        if (p.lastSuccessUtc) {
            return { cls: 'js-muted', text: 'Last answered ' + fmtTime(p.lastSuccessUtc) + (p.titles !== null && p.titles !== undefined ? ' - ' + p.titles + ' titles' : '') };
        }
        return { cls: 'js-muted', text: 'Not synced yet.' };
    }

    function testResultText(r) {
        return r.ok
            ? 'OK: "' + (r.serverName || 'server') + '", ' + r.titles + ' titles'
            : r.error;
    }

    function renderPeers() {
        var holder = $('js-peers');
        clear(holder);
        var peers = (status && status.peers) || [];
        if (!peers.length) {
            holder.appendChild(el('p', { class: 'js-muted', text: 'No peers yet. Add one below with the address and token a friend gave you.' }));
            return;
        }
        var ids = peers.map(function (p) {
            return p.id;
        });
        peers.forEach(function (p, index) {
            var name = el('input', { type: 'text', value: p.name, maxlength: '100' });
            var url = el('input', { type: 'text', value: p.baseUrl });
            var token = el('input', { type: 'password', placeholder: 'unchanged', autocomplete: 'new-password' });
            var enabled = el('input', { type: 'checkbox', checked: p.enabled });
            var result = el('span', { class: 'js-muted' });
            var st = peerStatusText(p);
            var move = function (delta) {
                var next = ids.slice();
                var tmp = next[index];
                next[index] = next[index + delta];
                next[index + delta] = tmp;
                api('POST', 'peers/order', { ids: next }).then(refresh).catch(fail);
            };
            var test = btn('Test', '', function () {
                result.textContent = 'Testing...';
                busy(test, api('POST', 'peers/test', { url: url.value, token: token.value, peerId: p.id })).then(function (r) {
                    result.className = r.ok ? '' : 'js-muted';
                    result.textContent = testResultText(r);
                }).catch(function (e) {
                    result.textContent = '';
                    fail(e);
                });
            });
            var save = btn('Save', 'primary', function () {
                busy(save, api('PUT', 'peers/' + encodeURIComponent(p.id), {
                    name: name.value,
                    baseUrl: url.value,
                    token: token.value,
                    enabled: enabled.checked
                })).then(function () {
                    showMessage('ok', 'Saved "' + name.value.trim() + '". Run a sync to apply the change.');
                    return refresh();
                }).catch(fail);
            });
            var remove = btn('Remove', 'danger', function () {
                if (!window.confirm('Remove "' + p.name + '"? Its placeholders are deleted at the next sync.')) {
                    return;
                }
                busy(remove, api('DELETE', 'peers/' + encodeURIComponent(p.id))).then(function () {
                    showMessage('ok', 'Removed "' + p.name + '". Run a sync to clean up its placeholders.');
                    return refresh();
                }).catch(fail);
            });
            var up = btn('\u25B2', '', function () {
                move(-1);
            });
            var down = btn('\u25BC', '', function () {
                move(1);
            });
            up.disabled = index === 0;
            down.disabled = index === peers.length - 1;
            up.title = 'Higher priority';
            down.title = 'Lower priority';
            holder.appendChild(el('div', { class: 'js-card' }, [
                el('div', { class: 'js-row' }, [
                    field('Name', name, 'grow'),
                    field('Server address', url, 'grow'),
                    field('New token (optional)', token, 'grow'),
                    el('label', { class: 'js-check' }, [enabled, 'Enabled'])
                ]),
                el('div', { class: st.cls, text: st.text }),
                el('p', {}, [up, ' ', down, ' ', test, ' ', save, ' ', remove, ' ', result])
            ]));
        });
    }

    function addPeer() {
        var button = $('js-peer-add');
        busy(button, api('POST', 'peers', {
            name: $('js-peer-name').value,
            baseUrl: $('js-peer-url').value,
            token: $('js-peer-token').value,
            enabled: true
        })).then(function () {
            $('js-peer-name').value = '';
            $('js-peer-url').value = '';
            $('js-peer-token').value = '';
            $('js-peer-test-result').textContent = '';
            showMessage('ok', 'Peer added. Press "Sync now" to fetch its titles.');
            return refresh();
        }).catch(fail);
    }

    function testNewPeer() {
        var out = $('js-peer-test-result');
        out.textContent = 'Testing...';
        busy($('js-peer-test'), api('POST', 'peers/test', {
            url: $('js-peer-url').value,
            token: $('js-peer-token').value
        })).then(function (r) {
            out.textContent = testResultText(r);
        }).catch(function (e) {
            out.textContent = '';
            fail(e);
        });
    }

    function syncNow() {
        var button = $('js-sync');
        var state = $('js-sync-state');
        state.textContent = 'Syncing... this can take a while for large libraries.';
        busy(button, api('POST', 'sync')).then(function (report) {
            state.textContent = '';
            if (status) {
                status.lastSync = report;
            }
            renderReport();
            showMessage('ok', 'Sync finished.');
            return refresh();
        }).catch(function (e) {
            state.textContent = '';
            fail(e);
        });
    }

    // ---- refresh ----

    function render() {
        $('js-server').textContent = status.serverName ? 'This server: ' + status.serverName + '.' : '';
        renderNewLibs();
        renderShares();
        renderSetup();
        renderReport();
        renderPeers();
    }

    function refresh() {
        if (refreshing) {
            return Promise.resolve();
        }
        refreshing = true;
        return api('GET', 'status').then(function (s) {
            status = s;
            refreshing = false;
            render();
        }, function (e) {
            refreshing = false;
            fail(e);
        });
    }

    function pollActive() {
        if (!document.body.contains(page)) {
            return stopPolling();
        }
        api('GET', 'status').then(function (s) {
            (s.shares || []).forEach(function (sh) {
                var span = page.querySelector('[data-active="' + sh.id + '"]');
                if (span) {
                    span.textContent = String(sh.activeStreams);
                }
            });
        }).catch(function () { /* the next tick tries again */ });
    }

    function stopPolling() {
        clearInterval(pollTimer);
        pollTimer = null;
    }

    function selectTab(name) {
        Array.prototype.forEach.call(page.querySelectorAll('.js-tab'), function (t) {
            t.classList.toggle('active', t.getAttribute('data-tab') === name);
        });
        $('js-tab-share').classList.toggle('js-hidden', name !== 'share');
        $('js-tab-receive').classList.toggle('js-hidden', name !== 'receive');
    }

    Array.prototype.forEach.call(page.querySelectorAll('.js-tab'), function (t) {
        t.addEventListener('click', function () {
            selectTab(t.getAttribute('data-tab'));
        });
    });
    $('js-create-share').addEventListener('click', createShare);
    $('js-copy-token').addEventListener('click', copyToken);
    $('js-dismiss-token').addEventListener('click', dismissToken);
    $('js-peer-add').addEventListener('click', addPeer);
    $('js-peer-test').addEventListener('click', testNewPeer);
    $('js-sync').addEventListener('click', syncNow);

    page.addEventListener('pageshow', function () {
        refresh();
        if (!pollTimer) {
            pollTimer = setInterval(pollActive, 10000);
        }
    });
    page.addEventListener('pagehide', function () {
        stopPolling();
        dismissToken();
    });

    refresh();
    if (!pollTimer) {
        pollTimer = setInterval(pollActive, 10000);
    }
})();
