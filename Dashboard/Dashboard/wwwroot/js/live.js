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
    const filterBar = document.getElementById('strategyFilter');
    const baseTitle = document.title;

    const hubUrl = root.dataset.hubUrl;
    const niftyChgEl = document.getElementById('niftyChg');
    const closeUrl = root.dataset.closeUrl;
    const openUrl = root.dataset.openUrl;
    const initialRisk = parseFloat(root.dataset.initialRisk) || 15;
    const nifTargetPoints = parseFloat(root.dataset.targetPoints) || 30;

    const inr = new Intl.NumberFormat('en-IN', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const STRATEGY_LABELS = { Ema: 'EMA', Breakout: 'Breakout', Macd: 'MACD', Reversal: 'Liquidity sweep (CE)', EmaCrossA: 'EMA 9/21 · 15m', EmaCrossB: 'EMA 9/21 · 1h' };
    const STRATEGY_CLASS = { Ema: 'st-ema', Breakout: 'st-breakout', Macd: 'st-macd', Reversal: 'st-reversal', EmaCrossA: 'st-ema', EmaCrossB: 'st-ema' };

    let activeFilter = 'all';

    // key -> { dto, el, closing }. Keyed by strategy|symbol|entryTime rather than positionId,
    // because a position whose DB save failed carries Id -1 (and two EMA legs could collide).
    const cards = new Map();

    const keyOf = p => `${p.underlying || 'NIFTY50'}|${p.strategy}|${p.tradingSymbol}|${p.entryTime}`;
    const instrumentLabel = p => 'NIFTY';

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

    const LEG_LABELS = { CurrentWeekItm: 'Curr-wk ITM', NextWeekAtm: 'Next-wk ATM' };

    // Card: instrument + strategy | big P&L, then entry / LTP / SL, the gauge, and entry time + Exit.
    function createRow(p) {
        const tr = document.createElement('article');
        tr.className = 'ct-card pc live-pos-row';
        tr.dataset.strategy = p.strategy;
        const canClose = p.positionId > 0;
        const leg = LEG_LABELS[p.leg] ? `${LEG_LABELS[p.leg]} \u00b7 ` : '';

        tr.innerHTML = `
            <div class="pc-top">
                <div>
                    <div class="ct-sym">${instrumentLabel(p)} ${esc(p.strike)} ${esc(p.optionType)}</div>
                    <div class="ct-sub pos-subtitle d-flex align-items-center gap-2 mt-1">
                        <span class="st-chip ${STRATEGY_CLASS[p.strategy] ?? ''}">${esc(STRATEGY_LABELS[p.strategy] ?? p.strategy)}</span>
                        <span>${esc(leg)}Exp ${esc(fmtExpiry(p.expiry))}</span>
                    </div>
                </div>
                <div class="pc-pnl">
                    <span class="pos-pnl num"></span>
                    <span class="pos-pnl-pct"></span>
                </div>
            </div>
            <div class="pc-px">
                <span>Entry <b>${inr.format(p.entryPremium)}</b></span>
                <span>LTP <b class="pos-current"></b></span>
                <span>SL <b class="pos-slval"></b></span>
            </div>
            <div class="gauge">
                <div class="gauge-track"></div>
                <div class="gauge-fill"></div>
                <div class="gauge-mark sl"></div>
                <div class="gauge-mark entry"></div>
                <div class="gauge-mark target"></div>
            </div>
            <div class="pc-foot">
                <span>In at ${esc(fmtEntryTime(p.entryTime))} <span class="sl-note pos-slnote"></span></span>
                <button type="button" class="btn-exit pos-exit"
                        title="${canClose ? 'Exit this position' : 'Not saved to DB -- cannot be closed from the Dashboard'}"
                        ${canClose ? '' : 'disabled'}>Exit</button>
            </div>`;
        return tr;
    }

    // Brief green/red wash on a value that just changed. Restarting the CSS animation needs a reflow in between.
    function flash(el, dir) {
        el.classList.remove('flash-up', 'flash-down');
        void el.offsetWidth;
        el.classList.add(dir > 0 ? 'flash-up' : 'flash-down');
    }

    function updateRow(tr, p) {
        const cls = pnlClass(p.pnlRupees);
        const pnl = tr.querySelector('.pos-pnl');
        const pct = tr.querySelector('.pos-pnl-pct');
        const prevPnl = tr.dataset.pnl === undefined ? null : Number(tr.dataset.pnl);
        tr.dataset.pnl = p.pnlRupees;
        pnl.textContent = fmtMoney(p.pnlRupees);
        pct.textContent = fmtPct(p.pnlPercent);
        pnl.className = `pos-pnl num ${cls}`;
        tr.classList.toggle('pc-neg', p.pnlRupees < 0);
        pct.className = `pos-pnl-pct ${cls}`;

        const curEl = tr.querySelector('.pos-current');
        const prevLtp = tr.dataset.ltp === undefined ? null : Number(tr.dataset.ltp);
        tr.dataset.ltp = p.currentPremium;
        curEl.textContent = inr.format(p.currentPremium);
        if (prevLtp !== null && Number(p.currentPremium) !== prevLtp) flash(curEl, p.currentPremium - prevLtp);
        if (prevPnl !== null && Number(p.pnlRupees) !== prevPnl) flash(pnl, p.pnlRupees - prevPnl);

        const sl = Number(p.stopLossPremium) || 0;
        const slSpot = Number(p.stopLossSpot) || 0;

        // Stop-loss column: current (trailing) SL. Premium-based for EMA / Breakout / MACD, Nifty level (plus the trailing premium SL) for Liquidity sweep.
        const slVal = tr.querySelector('.pos-slval');
        const slNote = tr.querySelector('.pos-slnote');
        if (slSpot > 0) {
            slVal.textContent = `Nifty ${inr.format(slSpot)}`;
            slNote.textContent = p.stopLossTrailing ? 'moved to cost' : 'initial stop';
            slNote.className = 'sl-note pos-slnote' + (p.stopLossTrailing ? ' trailing' : '');
        } else if (sl > 0) {
            const entryPx = Number(p.entryPremium);
            const risk = initialRisk;
            const trailing = sl > entryPx - risk + 0.005;
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
        // Liquidity sweep has no fixed premium target (EMA 21 / trailing stop-loss), so no premium target marker.
        const hasPremiumTarget = p.strategy !== 'Reversal' && !String(p.strategy).startsWith('EmaCross');   // spot-level / open-ended exits have no premium target
        const targetPoints = nifTargetPoints;
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
        // Live P&L in the browser tab, so it can be read without opening the page.
        document.title = live.length ? `${fmtMoney(total)} \u00b7 ${baseTitle}` : baseTitle;
        const pill = document.getElementById('pnlPill');
        if (pill) { pill.classList.toggle('pnl-up', total >= 0); pill.classList.toggle('pnl-down', total < 0); }

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
        entry.el.classList.add('pc-closed');
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

    // ---- Live clock next to the date (IST, ticks every second) ----
    const clockEl = document.getElementById('liveClock');
    function tickClock() {
        if (!clockEl) return;
        clockEl.textContent = ' \u00b7 ' + new Date().toLocaleTimeString('en-IN', { timeZone: 'Asia/Kolkata', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: true }).toUpperCase() + ' IST';
    }
    tickClock();
    setInterval(tickClock, 1000);

    // ---- PCR (put/call ratio) on the index cards: the Worker pushes "PcrChanged" about every 90 s in market hours ----
    const PCR_CLASS = { 'Bullish-ish': 'pcr-bull', 'Bearish-ish': 'pcr-bear', 'Stretched high': 'pcr-stretch', 'Stretched low': 'pcr-stretch' };
    const fmtPcr = v => (v === null || v === undefined) ? '\u2014' : Number(v).toFixed(2);
    const fmtLevel = v => (v === null || v === undefined) ? '\u2014' : Number(v).toLocaleString('en-IN');

    function renderPcr(d) {
        const el = document.getElementById('pcr-nifty');
        if (!el) return;
        el.querySelector('[data-pcr="value"]').textContent = fmtPcr(d.windowPcr);
        el.querySelector('[data-pcr="label"]').textContent = d.label;
        el.className = 'pcr-chip ' + (PCR_CLASS[d.label] ?? 'pcr-neutral');
        const chg = (d.change === null || d.change === undefined) ? ''
            : ` \u00b7 ${d.change > 0 ? '+' : d.change < 0 ? '\u2212' : ''}${Math.abs(d.change).toFixed(2)} today`;
        el.title = `Put/Call ratio near the money. Full chain ${fmtPcr(d.fullPcr)} \u00b7 Support ${fmtLevel(d.supportStrike)} \u00b7 Resistance ${fmtLevel(d.resistanceStrike)}${chg}`;
    }

    // ---- Status strip: market hours, token, last tick ----
    const marketEl = document.getElementById('stripMarket');
    const tickEl = document.getElementById('stripTick');
    const tickWrap = document.getElementById('stripTickWrap');
    const tokenEl = document.getElementById('stripToken');
    let lastTick = 0;
    const markTick = () => { lastTick = Date.now(); };

    function istNow() {
        const parts = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Kolkata', weekday: 'short', hour: '2-digit', minute: '2-digit', hour12: false }).formatToParts(new Date());
        const get = t => parts.find(x => x.type === t).value;
        return { wd: get('weekday'), mins: parseInt(get('hour'), 10) * 60 + parseInt(get('minute'), 10) };
    }
    // Weekday 09:15-15:30 IST. Exchange holidays aren't known here, so a holiday still reads "open".
    function marketState() {
        const { wd, mins } = istNow();
        if (wd === 'Sat' || wd === 'Sun') return { open: false, text: 'closed (weekend)' };
        if (mins < 9 * 60 + 15) return { open: false, text: 'opens 9:15 AM' };
        if (mins >= 15 * 60 + 30) return { open: false, text: 'closed' };
        return { open: true, text: 'open \u00b7 closes 3:30 PM' };
    }

    function renderToken(t) {
        if (!tokenEl || !t) return;
        tokenEl.className = 'strip-val';
        if (t.state === 'generated') {
            const time = t.at ? new Date(t.at).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' }) : '';
            tokenEl.textContent = 'OK' + (time ? ' \u00b7 ' + time : '');
        } else if (t.state === 'missing') {
            tokenEl.textContent = 'missing today'; tokenEl.classList.add('strip-warn');
        } else if (t.state === 'error') {
            tokenEl.textContent = 'unknown'; tokenEl.classList.add('strip-warn');
        } else {
            tokenEl.textContent = 'checking\u2026';
        }
    }
    window.addEventListener('ct:token', e => renderToken(e.detail));
    renderToken(window.ctToken);

    function tickStrip() {
        const m = marketState();
        if (marketEl) { marketEl.textContent = m.text; marketEl.classList.toggle('strip-ok', m.open); }
        if (!tickEl) return;
        if (!lastTick) {
            tickEl.textContent = m.open ? 'waiting\u2026' : '\u2014';
            tickWrap.classList.remove('strip-stale');
            return;
        }
        const age = Math.round((Date.now() - lastTick) / 1000);
        tickEl.textContent = age < 60 ? `${age}s ago` : `${Math.floor(age / 60)}m ago`;
        // Ticks arrive about once a second, so 10s of silence while the market is open means the feed is stuck.
        tickWrap.classList.toggle('strip-stale', m.open && age >= 10);
    }
    tickStrip();
    setInterval(tickStrip, 1000);

    // ---- Exit button ----
    grid.addEventListener('click', async ev => {
        const btn = ev.target.closest('.pos-exit');
        if (!btn || btn.disabled) return;

        const rowEl = btn.closest('.pc');
        const entry = [...cards.values()].find(c => c.el === rowEl);
        if (!entry) return;

        const p = entry.dto;
        if (!confirm(`Exit ${p.tradingSymbol}?\nCurrent P&L: ${fmtMoney(p.pnlRupees)} (${fmtPct(p.pnlPercent)})`)) return;

        btn.disabled = true;
        try {
            const res = await fetch(closeUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
                body: 'id=' + encodeURIComponent(p.positionId) + '&underlying=' + encodeURIComponent(p.underlying || 'NIFTY50')
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

    connection.on('PositionChanged', p => { markTick(); upsert(p); });
    connection.on('PcrChanged', renderPcr);

    // Spot plus its change against the previous close (the close today's CPR was computed from; no change shown without it).
    function setSpot(el, chgEl, price) {
        if (!el) return;
        el.textContent = inr.format(price);
        const prev = parseFloat(el.dataset.prevClose);
        if (!chgEl || !(prev > 0)) return;
        const pct = (price / prev - 1) * 100;
        chgEl.textContent = `${pct > 0 ? '+' : pct < 0 ? '\u2212' : ''}${Math.abs(pct).toFixed(2)}%`;
        chgEl.className = 'ix-chg num ' + (pct > 0 ? 'pnl-positive' : pct < 0 ? 'pnl-negative' : 'pnl-flat');
    }

    connection.on('SpotChanged', d => {
        markTick();
        setSpot(spotEl, niftyChgEl, d.price);
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
    try {
        const initial = JSON.parse(document.getElementById('initialPositions').textContent || '[]');
        initial.forEach(upsert);
    } catch { /* malformed/empty initial payload -- SignalR resync will fill it in */ }
    refreshChrome();
    start();
})();