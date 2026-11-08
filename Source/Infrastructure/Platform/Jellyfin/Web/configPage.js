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
    var syncTimer = null;
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
        return isFinite(n) && n > 0 && n <= 2000 ? Math.round(n * 1e6) : NaN;
    }

    var BITRATE_ERROR = 'Max bitrate must be a number of Mbps between 0 and 2000, or empty for no limit.';
    var STREAMS_ERROR = 'Max streams must be a whole number between 1 and 100.';

    function streamsFrom(text) {
        var t = String(text || '').trim();
        var n = /^\d+$/.test(t) ? parseInt(t, 10) : NaN;
        return n >= 1 && n <= 100 ? n : NaN;
    }

    var ACCOUNT_STREAMS_ERROR = 'Max streams at a time must be a whole number between 1 and 20.';

    function accountStreamsFrom(text) {
        var t = String(text || '').trim();
        var n = /^\d+$/.test(t) ? parseInt(t, 10) : NaN;
        return n >= 1 && n <= 20 ? n : NaN;
    }

    // The API names the enums as strings; a number (the enum value) is accepted too.
    function isAccount(p) {
        return p.kind === 'Account' || p.kind === 1;
    }

    function limitActionName(p) {
        return p.limitAction === 'StopOldest' || p.limitAction === 1 ? 'StopOldest' : 'Refuse';
    }

    function limitActionSelect(value) {
        var select = el('select', {}, [
            el('option', { value: 'Refuse', text: 'Refuse the new viewer' }),
            el('option', { value: 'StopOldest', text: 'Stop the oldest viewer' })
        ]);
        select.value = value;
        return select;
    }

    function streamsText(n) {
        return 'max ' + n + (n === 1 ? ' stream' : ' streams');
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

    // ---- media types ----

    var TYPES = [
        ['Movies', 'Movies'],
        ['Shows', 'Shows'],
        ['Music', 'Music'],
        ['Audiobooks', 'Audiobooks & podcasts'],
        ['Ebooks', 'Ebooks'],
        ['Collections', 'Collections'],
        ['LiveTv', 'Live TV']
    ];
    var ALL_TYPES = TYPES.map(function (t) {
        return t[0];
    });
    var SHARE_TYPES_ERROR = 'Pick at least one media type to share.';
    var RECEIVE_TYPES_ERROR = 'Pick at least one media type to receive.';

    function supportedTypes() {
        return (status && status.supportedTypes) || ['Movies', 'Shows'];
    }

    // One tick per media type. Types this version cannot handle yet are always disabled.
    function typePicker() {
        var supported = supportedTypes();
        var inputs = {};
        var node = el('div', { class: 'js-types' }, TYPES.map(function (t) {
            var input = el('input', { type: 'checkbox', 'data-type': t[0] });
            inputs[t[0]] = input;
            return el('label', { class: 'js-check' }, [
                input,
                t[1],
                supported.indexOf(t[0]) >= 0 ? null : el('span', { class: 'js-muted', text: '(coming later)' })
            ]);
        }));
        return {
            node: node,
            set: function (types, editable) {
                ALL_TYPES.forEach(function (k) {
                    inputs[k].checked = types.indexOf(k) >= 0;
                    inputs[k].disabled = !editable || supported.indexOf(k) < 0;
                });
            },
            types: function () {
                return ALL_TYPES.filter(function (k) {
                    return inputs[k].checked;
                });
            }
        };
    }

    function supportedOnly(types) {
        var supported = supportedTypes();
        return types.filter(function (k) {
            return supported.indexOf(k) >= 0;
        });
    }

    // A select (everything / a list) with the ticks of the list; the ticks are locked while "everything" is chosen.
    // `list` remembers the last list so switching back and forth does not lose it.
    function typeChoice(labelText, allText, listText, startAll, stored, shownWhenAll) {
        var list = supportedOnly(stored);
        if (!list.length) {
            list = supportedTypes().slice();
        }
        var picker = typePicker();
        var select = el('select', {}, [el('option', { value: 'all', text: allText }), el('option', { value: 'list', text: listText })]);
        select.value = startAll ? 'all' : 'list';
        var apply = function () {
            if (select.value === 'all') {
                picker.set(shownWhenAll, false);
            } else {
                picker.set(list, true);
            }
        };
        var current = select.value;
        select.addEventListener('change', function () {
            if (current === 'list') {
                list = picker.types();
            }
            current = select.value;
            apply();
        });
        apply();
        return {
            node: el('div', { class: 'js-row' }, [field(labelText, select), picker.node]),
            isAll: function () {
                return select.value === 'all';
            },
            types: function () {
                return select.value === 'list' ? picker.types() : list;
            }
        };
    }

    function defaultShownTypes() {
        var d = status && status.defaultSharing;
        return d && !d.all ? d.types : ALL_TYPES;
    }

    function shareTypes(startAll, stored) {
        return typeChoice('Media types', 'Use the default', 'Custom', startAll, stored, defaultShownTypes());
    }

    function receiveTypes(startAll, stored) {
        return typeChoice('Receive', 'Everything they share', 'Only:', startAll, stored, ALL_TYPES);
    }

    // ---- share tab ----

    var newShareTypes = null;
    var newPeerTypes = null;

    function renderDefaultSharing() {
        var holder = $('js-default-sharing');
        clear(holder);
        var d = (status && status.defaultSharing) || { all: true, types: ALL_TYPES };
        var picker = typePicker();
        var all = el('input', { type: 'checkbox', checked: d.all });
        var list = supportedOnly(d.types);
        if (!list.length) {
            list = supportedTypes().slice();
        }
        var apply = function () {
            if (all.checked) {
                picker.set(ALL_TYPES, false);
            } else {
                picker.set(list, true);
            }
        };
        all.addEventListener('change', function () {
            if (all.checked) {
                list = picker.types();
            }
            apply();
        });
        apply();
        var save = btn('Save', 'primary', function () {
            var types = all.checked ? [] : picker.types();
            if (!all.checked && !types.length) {
                return showMessage('error', SHARE_TYPES_ERROR);
            }
            busy(save, api('PUT', 'sharing/default', { all: all.checked, types: types })).then(function () {
                showMessage('ok', 'Default sharing saved.');
                return refresh();
            }).catch(fail);
        });
        holder.appendChild(el('div', { class: 'js-row' }, [
            el('label', { class: 'js-check' }, [all, 'All media types']),
            picker.node
        ]));
        holder.appendChild(el('p', {}, [save]));
    }

    function renderNewTypes() {
        var holder = $('js-new-types');
        clear(holder);
        newShareTypes = newShareTypes
            ? shareTypes(newShareTypes.isAll(), newShareTypes.types())
            : shareTypes(true, []);
        holder.appendChild(newShareTypes.node);
    }

    function renderNewPeerTypes() {
        var holder = $('js-peer-types');
        clear(holder);
        newPeerTypes = newPeerTypes
            ? receiveTypes(newPeerTypes.isAll(), newPeerTypes.types())
            : receiveTypes(true, []);
        holder.appendChild(newPeerTypes.node);
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
            var types = shareTypes(s.useDefaultTypes, s.types || []);
            var active = el('span', { 'data-active': s.id, text: String(s.activeStreams) });
            var save = btn('Save', 'primary', function () {
                var count = streamsFrom(streams.value);
                if (isNaN(count)) {
                    return showMessage('error', STREAMS_ERROR);
                }
                var bps = bpsFromMbps(mbps.value);
                if (isNaN(bps)) {
                    return showMessage('error', BITRATE_ERROR);
                }
                var body = {
                    name: name.value,
                    maxStreams: count,
                    maxBitrate: bps,
                    enabled: enabled.checked,
                    useDefaultTypes: types.isAll()
                };
                if (!types.isAll()) {
                    body.types = types.types();
                    if (!body.types.length) {
                        return showMessage('error', SHARE_TYPES_ERROR);
                    }
                }
                busy(save, api('PUT', 'shares/' + encodeURIComponent(s.id), body)).then(function () {
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
                types.node,
                el('p', {}, [
                    el('span', { class: 'js-muted' }, ['Streams playing now: ', active, ' of ' + s.maxStreams + '  ']),
                    save, ' ', revoke
                ])
            ]));
        });
    }

    function createShare() {
        var button = $('js-create-share');
        var count = streamsFrom($('js-new-streams').value);
        if (isNaN(count)) {
            return showMessage('error', STREAMS_ERROR);
        }
        var bps = bpsFromMbps($('js-new-mbps').value);
        if (isNaN(bps)) {
            return showMessage('error', BITRATE_ERROR);
        }
        var body = {
            name: $('js-new-name').value,
            maxStreams: count,
            maxBitrate: bps,
            useDefaultTypes: newShareTypes.isAll()
        };
        if (!newShareTypes.isAll()) {
            body.types = newShareTypes.types();
            if (!body.types.length) {
                return showMessage('error', SHARE_TYPES_ERROR);
            }
        }
        busy(button, api('POST', 'shares', body)).then(function (created) {
            $('js-created-name').textContent = created.name;
            $('js-created-token').value = created.token;
            $('js-created').classList.remove('js-hidden');
            $('js-new-name').value = '';
            $('js-new-mbps').value = '';
            newShareTypes = null;
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
        if (status && status.syncError) {
            holder.appendChild(el('div', { class: 'js-msg error', text: 'The last sync failed: ' + status.syncError }));
        }
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
            var account = isAccount(p);
            var token = el('input', { type: 'password', placeholder: 'unchanged', autocomplete: 'new-password' });
            var streams = account ? el('input', { type: 'number', value: String(p.maxStreams), min: '1', max: '20' }) : null;
            var action = account ? limitActionSelect(limitActionName(p)) : null;
            var enabled = el('input', { type: 'checkbox', checked: p.enabled });
            var result = el('span', { class: 'js-muted' });
            var accept = receiveTypes(p.acceptAllTypes, p.acceptTypes || []);
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
                var body = account
                    ? { url: url.value, peerId: p.id, kind: 'Account', username: p.username, password: token.value }
                    : { url: url.value, token: token.value, peerId: p.id };
                busy(test, api('POST', 'peers/test', body)).then(function (r) {
                    result.className = r.ok ? '' : 'js-muted';
                    result.textContent = testResultText(r);
                }).catch(function (e) {
                    result.textContent = '';
                    fail(e);
                });
            });
            var submit = function (button, okText) {
                var body = { name: name.value, baseUrl: url.value, enabled: enabled.checked, acceptAllTypes: accept.isAll() };
                if (!accept.isAll()) {
                    body.acceptTypes = accept.types();
                    if (!body.acceptTypes.length) {
                        return showMessage('error', RECEIVE_TYPES_ERROR);
                    }
                }
                if (account) {
                    var count = accountStreamsFrom(streams.value);
                    if (isNaN(count)) {
                        return showMessage('error', ACCOUNT_STREAMS_ERROR);
                    }
                    body.kind = 'Account';
                    body.username = p.username;
                    body.password = token.value;
                    body.maxStreams = count;
                    body.limitAction = action.value;
                } else {
                    body.token = token.value;
                }
                if (account) {
                    token.value = '';
                }
                busy(button, api('PUT', 'peers/' + encodeURIComponent(p.id), body)).then(function () {
                    showMessage('ok', okText);
                    return refresh();
                }).catch(fail);
            };
            var save = btn('Save', 'primary', function () {
                submit(save, 'Saved "' + name.value.trim() + '". Run a sync to apply the change.');
            });
            var signIn = btn('Sign in again', 'primary', function () {
                if (!token.value) {
                    token.focus();
                    return showMessage('error', 'Type the password for "' + p.username + '" in the Password box first.');
                }
                submit(signIn, 'Signed in "' + p.name + '". Run a sync to fetch its titles.');
            });
            var remove = btn('Remove', 'danger', function () {
                if (!window.confirm('Remove "' + p.name + '"? Its placeholders are deleted by the sync that starts now.')) {
                    return;
                }
                busy(remove, api('DELETE', 'peers/' + encodeURIComponent(p.id))).then(function () {
                    showMessage('ok', 'Removed "' + p.name + '". A sync was started to remove its placeholders.');
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
                    account ? null : field('New token (optional)', token, 'grow'),
                    account ? field(p.signInFailed ? 'Password' : 'New password (optional)', token, 'grow') : null,
                    account ? field('Max streams at a time', streams) : null,
                    account ? field('When the limit is reached', action) : null,
                    el('label', { class: 'js-check', title: 'Turning a peer off removes its placeholders at the next sync.' }, [enabled, 'Enabled'])
                ]),
                accept.node,
                account ? el('div', { class: 'js-muted', text: 'Jellyfin account \u00B7 ' + p.username + ' \u00B7 ' + streamsText(p.maxStreams) }) : null,
                el('div', { class: 'js-muted', text: 'Disabling a peer removes its placeholders at the next sync (they come back when you enable it again).' }),
                el('div', { class: st.cls, text: st.text }),
                p.signInFailed ? el('div', { class: 'js-msg error', text: 'Sign-in failed. Type the current password in the Password box and press "Sign in again".' }) : null,
                el('p', {}, [up, ' ', down, ' ', test, ' ', save, ' ', p.signInFailed ? signIn : null, p.signInFailed ? ' ' : null, remove, ' ', result])
            ]));
        });
    }

    function newPeerIsAccount() {
        return $('js-peer-kind').value === 'Account';
    }

    function showPeerKind() {
        var account = newPeerIsAccount();
        $('js-peer-token-field').classList.toggle('js-hidden', account);
        $('js-peer-account-fields').classList.toggle('js-hidden', !account);
        $('js-peer-account-note').classList.toggle('js-hidden', !account);
    }

    function addPeer() {
        var button = $('js-peer-add');
        var body = { name: $('js-peer-name').value, baseUrl: $('js-peer-url').value, enabled: true, acceptAllTypes: newPeerTypes.isAll() };
        if (!newPeerTypes.isAll()) {
            body.acceptTypes = newPeerTypes.types();
            if (!body.acceptTypes.length) {
                return showMessage('error', RECEIVE_TYPES_ERROR);
            }
        }
        if (newPeerIsAccount()) {
            var count = accountStreamsFrom($('js-peer-max-streams').value);
            if (isNaN(count)) {
                return showMessage('error', ACCOUNT_STREAMS_ERROR);
            }
            body.kind = 'Account';
            body.username = $('js-peer-username').value;
            body.password = $('js-peer-password').value;
            body.maxStreams = count;
            body.limitAction = $('js-peer-limit-action').value;
        } else {
            body.token = $('js-peer-token').value;
        }
        $('js-peer-password').value = '';
        busy(button, api('POST', 'peers', body)).then(function () {
            $('js-peer-token').value = '';
            $('js-peer-name').value = '';
            $('js-peer-url').value = '';
            $('js-peer-username').value = '';
            $('js-peer-test-result').textContent = '';
            newPeerTypes = null;
            showMessage('ok', 'Peer added. Press "Sync now" to fetch its titles.');
            return refresh();
        }).catch(fail);
    }

    function testNewPeer() {
        var out = $('js-peer-test-result');
        out.textContent = 'Testing...';
        var body = newPeerIsAccount()
            ? { url: $('js-peer-url').value, kind: 'Account', username: $('js-peer-username').value, password: $('js-peer-password').value }
            : { url: $('js-peer-url').value, token: $('js-peer-token').value };
        busy($('js-peer-test'), api('POST', 'peers/test', body)).then(function (r) {
            out.textContent = testResultText(r);
        }).catch(function (e) {
            out.textContent = '';
            fail(e);
        });
    }

    // The sync runs in the background on the server; the page polls the status until it is done.
    function watchSync(announce) {
        var button = $('js-sync');
        var state = $('js-sync-state');
        if (syncTimer) {
            return;
        }
        button.disabled = true;
        state.textContent = 'Syncing... this can take a while for large libraries. You can leave this page.';
        syncTimer = setInterval(function () {
            if (!document.body.contains(page)) {
                clearInterval(syncTimer);
                syncTimer = null;
                return;
            }
            api('GET', 'status').then(function (s) {
                if (s.syncRunning) {
                    return;
                }
                clearInterval(syncTimer);
                syncTimer = null;
                button.disabled = false;
                state.textContent = '';
                status = s;
                render();
                if (announce) {
                    if (s.syncError) {
                        showMessage('error', 'Sync failed: ' + s.syncError);
                    } else {
                        showMessage('ok', 'Sync finished.');
                    }
                }
            }).catch(function () { /* the next tick tries again */ });
        }, 2000);
    }

    function syncNow() {
        busy($('js-sync'), api('POST', 'sync')).then(function (r) {
            if (r && r.alreadyRunning) {
                showMessage('ok', 'A sync is already running; showing it.');
            }
            watchSync(true);
        }).catch(fail);
    }

    // ---- refresh ----

    function render() {
        $('js-server').textContent = status.serverName ? 'This server: ' + status.serverName + '.' : '';
        renderDefaultSharing();
        renderNewTypes();
        renderShares();
        renderSetup();
        renderReport();
        renderPeers();
        renderNewPeerTypes();
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
            if (s.syncRunning) {
                watchSync(false);
            }
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
    $('js-peer-kind').addEventListener('change', showPeerKind);
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
