// Live desk page.
// - Renders the initial snapshot the server embedded in #initialPositions.
// - Connects straight to the Worker's SignalR hub for real-time "PositionChanged" / "SpotChanged".
// - "Exit" posts to this app's own /Live/Close, which forwards to the Worker's Command API.
(function () {
    'use strict';

    const root = document.getElementById('liveRoot');
    const grid = document.getElementById('positionGrid');
    const emptyState = document.getElementById('emptyState');
    const tableWrap = document.getElementById('positionTableWrap');
    const openCount = document.getElementById('openCount');
    const totalPnlEl = document.getElementById('totalPnl');
    const spotEl = document.getElementById('niftySpot');
    const hubDot = document.getElementById('hubDot');
    const hubStatus = document.getElementById('hubStatus');
    const cprRange = document.getElementById('cprRange');
    const cprSpot = document.getElementById('cprSpot');
    const filterBar = document.getElementById('strategyFilter');

    const hubUrl = root.dataset.hubUrl;
    const closeUrl = root.dataset.closeUrl;
    const openUrl = root.dataset.openUrl;
    const initialRisk = parseFloat(root.dataset.initialRisk) || 15;
    const targetPoints = parseFloat(root.dataset.targetPoints) || 30;
    const cprBc = parseFloat(root.dataset.cprBc);
    const cprTc = parseFloat(root.dataset.cprTc);

    const inr = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const STRATEGY_LABELS = { Ema: 'EMA', Breakout: 'Breakout', Macd: 'MACD', Reversal: 'Reversal' };
    const STRATEGY_CLASS = { Ema: 'st-ema', Breakout: 'st-breakout', Macd: 'st-macd', Reversal: 'st-reversal' };

    let activeFilter = 'all';

    // key -> { dto, el, closing }. Keyed by strategy|symbol|entryTime rather than positionId,
    // because a position whose DB save failed carries Id -1 (and two EMA legs could collide).
    const cards = new Map();

    const keyOf = p => `${p.strategy}|${p.tradingSymbol}|${p.entryTime}`;

    function esc(s) {
        return String(s ?? '').replace(/[&<>"']/g, c =>
            ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    function fmtMoney(v) {
        const sign = v > 0 ? '+' : v < 0 ? '-' : '';
        return `${sign}₹${inr.format(Math.abs(v))}`;
    }

    function fmtPct(v) {
        const sign = v > 0 ? '+' : v < 0 ? '-' : '';
        return `${sign}${Math.abs(v).toFixed(1)}%`;
    }

    function fmtExpiry(iso) {
        const d = new Date(iso + 'T00:00:00');
        return d.toLocaleDateString('en-IN', { day: '2-digit', month: 'short' });
    }

    function fmtEntryTime(iso) {
        return new Date(iso).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit', hour12: true });
    }

    function pnlClass(v) { return v > 0 ? 'pnl-positive' : v < 0 ? 'pnl-negative' : 'pnl-flat'; }

    // Row: Instrument | Entry time | Entry -> LTP + stop-loss gauge | P&L | Exit
    function createRow(p) {
        const tr = document.createElement('tr');
        tr.className = 'live-pos-row';
        tr.dataset.strategy = p.strategy;
        const canClose = p.positionId > 0;

        tr.innerHTML = `
            <td>
                <div class="ct-sym">NIFTY ${esc(p.strike)} ${esc(p.optionType)}</div>
                <div class="ct-sub pos-subtitle d-flex align-items-center gap-2 mt-1">
                    <span class="st-chip ${STRATEGY_CLASS[p.strategy] ?? ''}">${esc(STRATEGY_LABELS[p.strategy] ?? p.strategy)}</span>
                    <span>Exp ${esc(fmtExpiry(p.expiry))}</span>
                </div>
            </td>
            <td class="num text-nowrap">${esc(fmtEntryTime(p.entryTime))}</td>
            <td>
                <div class="pos-px">
                    <span class="e">${inr.format(p.entryPremium)}</span><span class="arrow">&rarr;</span><span class="l pos-current"></span>
                </div>
                <div class="gauge">
                    <div class="gauge-track"></div>
                    <div class="gauge-fill"></div>
                    <div class="gauge-mark sl"></div>
                    <div class="gauge-mark entry"></div>
                    <div class="gauge-mark target"></div>
                </div>
            </td>
            <td class="pos-slcell">
                <div class="sl-val pos-slval"></div>
                <div class="sl-note pos-slnote"></div>
            </td>
            <td class="text-end">
                <span class="pos-pnl num"></span>
                <span class="pos-pnl-pct"></span>
            </td>
            <td class="text-end">
                <button type="button" class="btn-exit pos-exit"
                        title="${canClose ? 'Exit this position' : 'Not saved to DB -- cannot be closed from the Dashboard'}"
                        ${canClose ? '' : 'disabled'}>Exit</button>
            </td>`;
        return tr;
    }

    function updateRow(tr, p) {
        const cls = pnlClass(p.pnlRupees);
        const pnl = tr.querySelector('.pos-pnl');
        const pct = tr.querySelector('.pos-pnl-pct');
        pnl.textContent = fmtMoney(p.pnlRupees);
        pct.textContent = fmtPct(p.pnlPercent);
        pnl.className = `pos-pnl num ${cls}`;
        pct.className = `pos-pnl-pct ${cls}`;

        tr.querySelector('.pos-current').textContent = inr.format(p.currentPremium);

        const sl = Number(p.stopLossPremium) || 0;
        const slSpot = Number(p.stopLossSpot) || 0;

        // Stop-loss column: current (trailing) SL. Premium-based for EMA / Breakout / MACD, Nifty level for Reversal.
        const slVal = tr.querySelector('.pos-slval');
        const slNote = tr.querySelector('.pos-slnote');
        if (slSpot > 0) {
            slVal.textContent = `Nifty ${inr.format(slSpot)}`;
            slNote.textContent = p.stopLossTrailing ? 'moved to cost' : 'initial stop';
            slNote.className = 'sl-note pos-slnote' + (p.stopLossTrailing ? ' trailing' : '');
        } else if (sl > 0) {
            const entryPx = Number(p.entryPremium);
            const trailing = sl > entryPx - initialRisk + 0.005;
            slVal.textContent = `₹${inr.format(sl)}`;
            slNote.textContent = sl >= entryPx ? 'trailing · profit locked' : trailing ? 'trailing' : 'initial stop';
            slNote.className = 'sl-note pos-slnote' + (trailing ? ' trailing' : '');
        } else {
            slVal.textContent = '—';
            slNote.textContent = '';
        }

        // Gauge scale: from just below the lowest of (SL, entry, LTP) to just above the target / LTP.
        const entry = Number(p.entryPremium), ltp = Number(p.currentPremium);
        const lows = [entry, ltp].concat(sl > 0 ? [sl] : []);
        const min = Math.min(...lows) - 6;
        // Reversal exits on Nifty spot levels (not premium points), so it has no premium target marker.
        const hasPremiumTarget = p.strategy !== 'Reversal';
        const max = Math.max(hasPremiumTarget ? entry + targetPoints : entry, ltp) + 6;
        const pos = v => ((v - min) / (max - min)) * 100;
        const lo = Math.min(entry, ltp), hi = Math.max(entry, ltp);

        const fill = tr.querySelector('.gauge-fill');
        fill.style.left = pos(lo).toFixed(1) + '%';
        fill.style.width = (pos(hi) - pos(lo)).toFixed(1) + '%';
        fill.classList.toggle('neg', p.pnlRupees < 0);

        const slMark = tr.querySelector('.gauge-mark.sl');
        slMark.style.display = sl > 0 ? '' : 'none';
        slMark.style.left = pos(sl).toFixed(1) + '%';
        tr.querySelector('.gauge-mark.entry').style.left = pos(entry).toFixed(1) + '%';
        const targetMark = tr.querySelector('.gauge-mark.target');
        targetMark.style.display = hasPremiumTarget ? '' : 'none';
        targetMark.style.left = pos(entry + targetPoints).toFixed(1) + '%';
    }

    function applyFilter() {
        for (const entry of cards.values()) {
            entry.el.hidden = activeFilter !== 'all' && entry.dto.strategy !== activeFilter;
        }
    }

    function refreshChrome() {
        const live = [...cards.values()].filter(c => !c.closing);
        openCount.textContent = live.length;

        const total = live.reduce((sum, c) => sum + (Number(c.dto.pnlRupees) || 0), 0);
        totalPnlEl.textContent = fmtMoney(total);

        emptyState.classList.toggle('d-none', cards.size > 0);
        tableWrap.classList.toggle('d-none', cards.size === 0);
        applyFilter();
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
        entry.dto = p;
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
        } catch { /* worker unreachable -- the connection pill already tells the story */ }
    }

    // ---- Strategy filter chips ----
    filterBar.addEventListener('click', ev => {
        const chip = ev.target.closest('[data-filter]');
        if (!chip) return;
        ev.preventDefault();
        activeFilter = chip.dataset.filter;
        filterBar.querySelectorAll('.chip').forEach(c => c.classList.toggle('chip-active', c === chip));
        applyFilter();
    });

    // ---- CPR range + live spot marker ----
    const hasCpr = Number.isFinite(cprBc) && Number.isFinite(cprTc) && cprTc > cprBc;

    function updateCprSpot(price) {
        if (!hasCpr || !cprSpot) return;
        // BC..TC occupies the middle 40% of the bar; the spot can wander outside it.
        const pct = 30 + ((price - cprBc) / (cprTc - cprBc)) * 40;
        cprSpot.style.left = Math.min(98, Math.max(1, pct)) + '%';
        cprSpot.classList.remove('d-none');
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
        updateCprSpot(d.price);
    });

    connection.onreconnecting(() => setStatus('connecting', 'Reconnecting...'));
    connection.onreconnected(() => {
        setStatus('live', 'Connected to Worker');
        resync(); // anything that happened while we were disconnected
    });
    connection.onclose(() => setStatus('offline', 'Disconnected'));

    async function start() {
        try {
            await connection.start();
            setStatus('live', 'Connected to Worker');
            resync();
        } catch {
            setStatus('offline', 'Worker unreachable -- retrying...');
            setTimeout(start, 5000);
        }
    }

    // ---- Boot ----
    if (cprRange && hasCpr) { cprRange.style.left = '30%'; cprRange.style.width = '40%'; }
    try {
        const initial = JSON.parse(document.getElementById('initialPositions').textContent || '[]');
        initial.forEach(upsert);
    } catch { /* malformed/empty initial payload -- SignalR resync will fill it in */ }
    refreshChrome();
    start();
})();
