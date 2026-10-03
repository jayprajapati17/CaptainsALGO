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
    const LEG_LABELS = { CurrentWeekItm: 'Curr-wk ITM', NextWeekAtm: 'Next-wk ATM' };

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
        return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short', year: 'numeric' });
    }

    function fmtEntry(iso) {
        const d = new Date(iso);
        const sameDay = d.toDateString() === new Date().toDateString();
        const time = d.toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', hour12: true });
        return sameDay ? `Entered ${time}` : `Entered ${d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short' })}, ${time}`;
    }

    function createCard(p) {
        const col = document.createElement('div');
        col.className = 'col';
        const isCall = p.optionType === 'CE';
        const leg = p.leg ? `<span class="badge text-bg-dark border ms-1">${esc(LEG_LABELS[p.leg] ?? p.leg)}</span>` : '';
        const canClose = p.positionId > 0;

        col.innerHTML = `
            <div class="card pos-card h-100">
                <div class="card-body">
                    <div class="d-flex justify-content-between align-items-start">
                        <div class="d-flex align-items-center gap-2">
                            <div class="pos-icon ${isCall ? 'pos-icon-ce' : 'pos-icon-pe'}" title="${isCall ? 'Call (CE)' : 'Put (PE)'}">${isCall ? '\u25B2' : '\u25BC'}</div>
                            <div>
                                <div class="fw-semibold pos-symbol">${esc(p.tradingSymbol)}</div>
                                <div class="small text-secondary">
                                    Exp ${esc(fmtExpiry(p.expiry))}
                                    <span class="badge text-bg-secondary ms-1">${esc(STRATEGY_LABELS[p.strategy] ?? p.strategy)}</span>${leg}
                                </div>
                            </div>
                        </div>
                        <button type="button" class="btn btn-sm btn-outline-danger pos-exit"
                                title="${canClose ? 'Exit this position' : 'Not saved to DB -- cannot be closed from the Dashboard'}"
                                ${canClose ? '' : 'disabled'}>\u23FB</button>
                    </div>

                    <div class="mt-3">
                        <span class="pnl-chip"></span>
                    </div>

                    <div class="mt-3 small num">
                        Entry \u20B9${inr.format(p.entryPremium)} \u2192
                        <strong class="pos-current"></strong>
                    </div>
                    <div class="small text-secondary mt-1 pos-entry-time">${esc(fmtEntry(p.entryTime))}</div>
                    <div class="small mt-2 pos-closed-note d-none"></div>
                </div>
            </div>`;
        return col;
    }

    function updateCard(el, p) {
        const chip = el.querySelector('.pnl-chip');
        chip.textContent = `${fmtMoney(p.pnlRupees)}  (${fmtPct(p.pnlPercent)})`;
        chip.classList.toggle('pnl-chip-pos', p.pnlRupees > 0);
        chip.classList.toggle('pnl-chip-neg', p.pnlRupees < 0);
        chip.classList.toggle('pnl-chip-flat', p.pnlRupees === 0);
        el.querySelector('.pos-current').textContent = `\u20B9${inr.format(p.currentPremium)}`;

        // The card's left border encodes P&L sign at a glance (scanning many cards fast).
        const cardEl = el.querySelector('.pos-card');
        cardEl.classList.toggle('pos-card-gain', p.pnlRupees > 0);
        cardEl.classList.toggle('pos-card-loss', p.pnlRupees < 0);
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
            const el = createCard(p);
            grid.appendChild(el);
            entry = { dto: p, el, closing: false };
            cards.set(key, entry);
        }
        entry.dto = p;
        updateCard(entry.el, p);
        refreshChrome();
    }

    // Show the final state + exit reason for a few seconds, then drop the card.
    function markClosed(key, entry, p) {
        entry.closing = true;
        updateCard(entry.el, p);
        const cardEl = entry.el.querySelector('.pos-card');
        cardEl.classList.add('pos-card-closed');
        entry.el.querySelector('.pos-exit').disabled = true;
        const note = entry.el.querySelector('.pos-closed-note');
        note.textContent = `Closed: ${p.exitReason ?? 'exited'}`;
        note.classList.remove('d-none');
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

        const colEl = btn.closest('.col');
        const entry = [...cards.values()].find(c => c.el === colEl);
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
