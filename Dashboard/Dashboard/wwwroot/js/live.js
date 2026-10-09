// Live Positions page (Task 8).
// - Renders the initial snapshot the server embedded in #initialPositions.
// - Connects straight to the Worker's SignalR hub for real-time "PositionChanged" / "SpotChanged".
// - "Exit" posts to this app's own /Live/Close, which forwards to the Worker's Command API.
(function () {
    'use strict';

    const root = document.getElementById('liveRoot');
    const grid = document.getElementById('positionGrid');
    const emptyState = document.getElementById('emptyState');
    const openCount = document.getElementById('openCount');
    const spotEl = document.getElementById('niftySpot');
    const hubDot = document.getElementById('hubDot');
    const hubStatus = document.getElementById('hubStatus');

    const hubUrl = root.dataset.hubUrl;
    const closeUrl = root.dataset.closeUrl;
    const openUrl = root.dataset.openUrl;

    const inr = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const STRATEGY_LABELS = { Ema: 'EMA', Breakout: 'Breakout', Macd: 'MACD' };

    // key -> { dto, el }. Keyed by strategy|symbol|entryTime rather than positionId,
    // because a position whose DB save failed carries Id -1 (and two EMA legs could collide).
    const cards = new Map();

    const keyOf = p => `${p.strategy}|${p.tradingSymbol}|${p.entryTime}`;

    function esc(s) {
        return String(s ?? '').replace(/[&<>"']/g, c =>
            ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    function fmtMoney(v) {
        const sign = v > 0 ? '+' : v < 0 ? '-' : '';
        return `${sign}\u20B9${inr.format(Math.abs(v))}`;
    }

    function fmtPct(v) {
        const sign = v > 0 ? '+' : v < 0 ? '-' : '';
        return `${sign}${Math.abs(v).toFixed(1)}%`;
    }

    function fmtExpiry(iso) {
        const d = new Date(iso + 'T00:00:00');
        return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short' });
    }

    // Entry time as its own table column -- positions are always intraday (force-closed
    // by cutoff), so a bare time is enough; no need to disambiguate the date.
    function fmtEntryTime(iso) {
        return new Date(iso).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', hour12: true });
    }

    // Row layout confirmed: Symbol (strike + type, with expiry/strategy as a small
    // subtitle) | Entry time | Entry -> LTP (merged) | P&L | Exit button.
    function createRow(p) {
        const tr = document.createElement('tr');
        tr.className = 'live-pos-row';
        const canClose = p.positionId > 0;

        tr.innerHTML = `
            <td>
                <span class="num fw-semibold">${esc(p.strike)} ${esc(p.optionType)}</span>
                <div class="small text-secondary num pos-subtitle">${esc(fmtExpiry(p.expiry))} &middot; ${esc(STRATEGY_LABELS[p.strategy] ?? p.strategy)}</div>
            </td>
            <td class="num text-secondary text-nowrap">${esc(fmtEntryTime(p.entryTime))}</td>
            <td class="text-end num text-nowrap">\u20B9${inr.format(p.entryPremium)}&rarr;<span class="pos-current"></span></td>
            <td class="text-end"><span class="pnl-chip num"></span></td>
            <td class="text-end">
                <button type="button" class="btn btn-sm btn-outline-danger pos-exit"
                        title="${canClose ? 'Exit this position' : 'Not saved to DB -- cannot be closed from the Dashboard'}"
                        ${canClose ? '' : 'disabled'}>\u23FB</button>
            </td>`;
        return tr;
    }

    function updateRow(tr, p) {
        const chip = tr.querySelector('.pnl-chip');
        chip.textContent = `${fmtMoney(p.pnlRupees)} (${fmtPct(p.pnlPercent)})`;
        chip.classList.toggle('pnl-chip-pos', p.pnlRupees > 0);
        chip.classList.toggle('pnl-chip-neg', p.pnlRupees < 0);
        chip.classList.toggle('pnl-chip-flat', p.pnlRupees === 0);
        tr.querySelector('.pos-current').textContent = `\u20B9${inr.format(p.currentPremium)}`;

        // The row's left edge encodes P&L sign at a glance (scanning many rows fast).
        tr.classList.toggle('live-pos-row-gain', p.pnlRupees > 0);
        tr.classList.toggle('live-pos-row-loss', p.pnlRupees < 0);
    }

    function refreshChrome() {
        const count = [...cards.values()].filter(c => !c.closing).length;
        openCount.textContent = count;
        emptyState.classList.toggle('d-none', cards.size > 0);
    }

    function upsert(p) {
        const key = keyOf(p);
        let entry = cards.get(key);

        if (p.status === 'Closed') {
            if (entry && !entry.closing) markClosed(key, entry, p);
            return;
        }

        if (!entry) {
            const el = createRow(p);
            grid.appendChild(el);
            entry = { dto: p, el, closing: false };
            cards.set(key, entry);
        }
        entry.dto = p;
        updateRow(entry.el, p);
        refreshChrome();
    }

    // Show the final state + exit reason for a few seconds, then drop the row.
    function markClosed(key, entry, p) {
        entry.closing = true;
        updateRow(entry.el, p);
        entry.el.classList.add('live-pos-row-closed');
        entry.el.querySelector('.pos-exit').disabled = true;
        entry.el.querySelector('.pos-subtitle').textContent = `Closed: ${p.exitReason ?? 'exited'}`;
        refreshChrome();

        setTimeout(() => {
            entry.el.remove();
            cards.delete(key);
            refreshChrome();
        }, 6000);
    }

    // Replace everything with a fresh server snapshot (used on reconnect / after an exit).
    function applySnapshot(list) {
        const seen = new Set(list.map(keyOf));
        for (const [key, entry] of cards) {
            if (!seen.has(key) && !entry.closing) {
                entry.el.remove();
                cards.delete(key);
            }
        }
        list.forEach(upsert);
        refreshChrome();
    }

    async function resync() {
        try {
            const res = await fetch(openUrl, { headers: { Accept: 'application/json' } });
            if (res.ok) applySnapshot(await res.json());
        } catch { /* worker unreachable -- SignalR status pill already tells the story */ }
    }

    // ---- Exit button ----
    grid.addEventListener('click', async ev => {
        const btn = ev.target.closest('.pos-exit');
        if (!btn || btn.disabled) return;

        const rowEl = btn.closest('tr');
        const entry = [...cards.values()].find(c => c.el === rowEl);
        if (!entry) return;

        const p = entry.dto;
        if (!confirm(`Exit ${p.tradingSymbol}?\nCurrent P&L: ${fmtMoney(p.pnlRupees)} (${fmtPct(p.pnlPercent)})`)) return;

        btn.disabled = true;
        try {
            const res = await fetch(closeUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: 'id=' + encodeURIComponent(p.positionId)
            });

            if (!res.ok) {
                const msg = (await res.json().catch(() => ({}))).message ?? `Close failed (${res.status}).`;
                alert(msg);
                btn.disabled = false;
                if (res.status === 404) resync(); // already closed elsewhere -- refresh the view
                return;
            }

            // The Worker also broadcasts "Closed" over SignalR; resync shortly after as a safety net
            // in case that event was missed (e.g. hub momentarily disconnected).
            setTimeout(resync, 2500);
        } catch {
            alert('Dashboard could not reach the server -- position NOT closed.');
            btn.disabled = false;
        }
    });

    // ---- SignalR ----
    function setStatus(kind, text) {
        hubDot.className = 'status-dot status-' + kind;
        hubStatus.textContent = text;
    }

    const connection = new signalR.HubConnectionBuilder()
        .withUrl(hubUrl)
        .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
        .build();

    connection.on('PositionChanged', upsert);

    connection.on('SpotChanged', d => {
        spotEl.textContent = inr.format(d.price);
    });

    connection.onreconnecting(() => setStatus('connecting', 'Reconnecting...'));
    connection.onreconnected(() => {
        setStatus('live', 'Live');
        resync(); // anything that happened while we were disconnected
    });
    connection.onclose(() => setStatus('offline', 'Disconnected'));

    async function start() {
        try {
            await connection.start();
            setStatus('live', 'Live');
            resync();
        } catch {
            setStatus('offline', 'Worker unreachable -- retrying...');
            setTimeout(start, 5000);
        }
    }

    // ---- Boot ----
    try {
        const initial = JSON.parse(document.getElementById('initialPositions').textContent || '[]');
        initial.forEach(upsert);
    } catch { /* malformed/empty initial payload -- SignalR resync will fill it in */ }
    refreshChrome();
    start();
})();