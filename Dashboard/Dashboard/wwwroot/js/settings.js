// Settings page behaviour (Control Room design).
// Everything is driven by the real <input data-k="Section:Key"> fields rendered by Views/Settings/_Field.cshtml,
// so the normal POST /Settings/Save flow is unchanged -- this script only adds the UI niceties:
// tabs, switches, +/- steppers, pending-change tracking + review diff, Reset, the live trailing-SL chart,
// the "What if..." calculator, the Telegram test button and the Upstox token status card.
import { sgn, inr, pad } from './lib/format.js';
import { slOffset } from './lib/trailing.js';

(function () {
    'use strict';

    const form = document.getElementById('settingsForm');
    if (!form) return;

    const $ = (sel, root) => (root || document).querySelector(sel);
    const $$ = (sel, root) => Array.from((root || document).querySelectorAll(sel));
    const fields = () => $$('[data-k]', form);
    const byKey = (k) => form.querySelector('[data-k="' + k + '"]');
    const num = (k, fallback) => {
        const el = byKey(k);
        const v = el ? parseFloat(el.value) : NaN;
        return Number.isFinite(v) ? v : fallback;
    };

    // ------------------------------------------------------------------ values & changes
    function isChanged(el) {
        const orig = el.dataset.orig ?? '';
        if (el.dataset.kind === 'number') {
            const a = parseFloat(el.value), b = parseFloat(orig);
            if (Number.isFinite(a) && Number.isFinite(b)) return a !== b;
        }
        return (el.value ?? '') !== orig;
    }

    function display(el) {
        if (el.dataset.kind === 'secret') return el.value ? '••••••' : '(empty)';
        if (el.dataset.kind === 'bool') return el.value === 'true' ? 'On' : 'Off';
        return el.value === '' ? '(empty)' : el.value;
    }
    function displayOrig(el) {
        const o = el.dataset.orig ?? '';
        if (el.dataset.kind === 'secret') return o ? '••••••' : '(empty)';
        if (el.dataset.kind === 'bool') return o === 'true' ? 'On' : 'Off';
        return o === '' ? '(empty)' : o;
    }

    function syncSwitch(hidden) {
        const btn = form.querySelector('[data-switch-for="' + hidden.id + '"]');
        if (!btn) return;
        const on = hidden.value === 'true';
        btn.classList.toggle('on', on);
        btn.setAttribute('aria-checked', on ? 'true' : 'false');
    }

    function refresh() {
        // per-row dots + pending list
        const changed = [];
        const tabsChanged = new Set();
        const rowChanged = new Map();   // a row can hold two fields (Nifty + Bank Nifty): its dot is on if either changed
        fields().forEach((el) => {
            if (el.disabled) return;
            const ch = isChanged(el);
            const row = el.closest('.st-row');
            if (row) rowChanged.set(row, rowChanged.get(row) || ch);
            if (ch) {
                changed.push(el);
                const panel = el.closest('[data-st-panel]');
                if (panel) tabsChanged.add(panel.dataset.stPanel);
            }
        });

        rowChanged.forEach((on, row) => {
            const dot = row.querySelector('.st-label .st-dot');
            if (dot) dot.hidden = !on;
        });

        // tab dots
        $$('.st-tab', form).forEach((t) => {
            const d = t.querySelector('.st-tab-dot');
            if (d) d.hidden = !tabsChanged.has(t.dataset.stTab);
        });

        // header pill
        const pill = $('#stPending');
        if (pill) {
            pill.textContent = changed.length === 0 ? 'No pending changes' : changed.length + ' pending change' + (changed.length > 1 ? 's' : '');
            pill.classList.toggle('has', changed.length > 0);
        }

        // review diff
        const box = $('#stReview'), grid = $('#stReviewGrid'), count = $('#stReviewCount');
        if (box && grid) {
            box.hidden = changed.length === 0;
            grid.textContent = '';
            changed.forEach((el) => {
                const card = document.createElement('div');
                card.className = 'st-diff';
                const top = document.createElement('div');
                top.className = 'st-diff-top';
                const l = document.createElement('b');
                l.textContent = el.dataset.label || el.dataset.k;
                const s = document.createElement('span');
                s.textContent = el.dataset.scope || '';
                top.append(l, s);
                const vals = document.createElement('div');
                vals.className = 'st-diff-vals';
                const from = document.createElement('span');
                from.className = 'st-from';
                from.textContent = displayOrig(el);
                const arrow = document.createElement('span');
                arrow.textContent = '→';
                const to = document.createElement('span');
                to.className = 'st-to';
                to.textContent = display(el);
                vals.append(from, arrow, to);
                card.append(top, vals);
                grid.append(card);
            });
            if (count) count.textContent = changed.length + ' change' + (changed.length > 1 ? 's' : '') + ' · nothing reaches the Worker until you press Save & apply';
        }

        renderCards();
        renderChart();
    }

    // ------------------------------------------------------------------ tabs
    function showTab(name) {
        $$('.st-tab', form).forEach((t) => {
            const on = t.dataset.stTab === name;
            t.classList.toggle('active', on);
            t.setAttribute('aria-selected', on ? 'true' : 'false');
        });
        $$('[data-st-panel]', form).forEach((p) => { p.hidden = p.dataset.stPanel !== name; });
        const hid = $('#activeTab');
        if (hid) hid.value = name;
        if (name === 'Strategies') renderChart();
    }
    $$('.st-tab', form).forEach((t) => t.addEventListener('click', () => showTab(t.dataset.stTab)));

    // ------------------------------------------------------------------ switches / steppers / secrets
    form.addEventListener('click', (e) => {
        const sw = e.target.closest('[data-switch-for]');
        if (sw && !sw.disabled) {
            const hidden = document.getElementById(sw.dataset.switchFor);
            hidden.value = hidden.value === 'true' ? 'false' : 'true';
            syncSwitch(hidden);
            refresh();
            return;
        }

        const st = e.target.closest('[data-step-for]');
        if (st && !st.disabled) {
            const input = document.getElementById(st.dataset.stepFor);
            const dir = parseInt(st.dataset.dir, 10) || 0;
            const step = parseFloat(input.dataset.step) || 1;
            if (input.dataset.kind === 'time') {
                const m = /^(\d{1,2}):(\d{2})/.exec(input.value || '09:00');
                let total = (m ? parseInt(m[1], 10) * 60 + parseInt(m[2], 10) : 540) + dir * step;
                total = ((total % 1440) + 1440) % 1440;
                input.value = pad(Math.floor(total / 60)) + ':' + pad(total % 60);
            } else {
                const cur = parseFloat(input.value);
                const next = (Number.isFinite(cur) ? cur : 0) + dir * step;
                input.value = String(Math.round(next * 1000) / 1000);
            }
            refresh();
            return;
        }

        const sec = e.target.closest('[data-toggle-secret]');
        if (sec) {
            const input = document.getElementById(sec.dataset.toggleSecret);
            const show = input.type === 'password';
            input.type = show ? 'text' : 'password';
            sec.textContent = show ? 'Hide' : 'Show';
        }
    });
    form.addEventListener('input', (e) => { if (e.target.matches('[data-k]')) refresh(); });
    form.addEventListener('change', (e) => { if (e.target.matches('[data-k]')) refresh(); });

    // ------------------------------------------------------------------ reset
    $('#stReset')?.addEventListener('click', () => {
        fields().forEach((el) => {
            if (el.disabled) return;
            el.value = el.dataset.orig ?? '';
            if (el.dataset.kind === 'bool') syncSwitch(el);
        });
        refresh();
    });

    // ------------------------------------------------------------------ strategy cards
    let profile = 'nifty';   // which trailing profile the chart shows

    function renderCards() {
        $$('.st-card', form).forEach((card) => {
            const toggle = card.querySelector('input[data-kind="bool"]');
            card.classList.toggle('is-off', !!toggle && toggle.value !== 'true');
            card.classList.toggle('is-profile', card.dataset.profile === profile && card.dataset.selected === '1');
            $$('[data-stat]', card).forEach((span) => {
                const el = byKey(span.dataset.stat);
                span.textContent = el && el.value !== '' ? el.value + (span.dataset.suffix || '') : '–';
            });
        });
    }

    function pickProfile(p, cardId) {
        profile = p;
        $$('.st-card', form).forEach((c) => { c.dataset.selected = cardId && c.dataset.card === cardId ? '1' : ''; });
        if (!cardId) {
            // chosen via the Nifty / Bank Nifty chips: highlight the first card of that profile
            const first = $$('.st-card', form).find((c) => c.dataset.profile === p);
            if (first) first.dataset.selected = '1';
        }
        $$('.st-prof', form).forEach((b) => b.classList.toggle('active', b.dataset.pick === p));
        $$('[data-profile-params]', form).forEach((c) => { c.hidden = c.dataset.profileParams !== p; });
        const lbl = $('#stProfLabel');
        if (lbl) lbl.textContent = p === 'bn' ? 'Bank Nifty' : 'Nifty';
        renderCards();
        renderChart();
    }
    $$('.st-card-pick', form).forEach((b) => b.addEventListener('click', () => pickProfile(b.dataset.pick, b.closest('.st-card').dataset.card)));
    $$('.st-prof', form).forEach((b) => b.addEventListener('click', () => pickProfile(b.dataset.pick, null)));

    // ------------------------------------------------------------------ trailing-SL chart + what-if
    let gain = 55;
    const gainBox = $('#stGains');
    if (gainBox) {
        const val = document.createElement('b');
        val.className = 'st-mono';
        val.id = 'stGainVal';
        gainBox.append(val);
        [-10, -5, 5, 10].forEach((d) => {
            const b = document.createElement('button');
            b.type = 'button';
            b.textContent = (d > 0 ? '+' : '−') + Math.abs(d);
            b.addEventListener('click', () => { gain = Math.max(0, Math.min(150, gain + d)); renderChart(); });
            gainBox.append(b);
        });
    }

    function params() {
        const sec = profile === 'bn' ? 'BankNifty' : 'Strategy';
        const p = {
            risk: num(sec + ':TrailingInitialRiskPoints', profile === 'bn' ? 30 : 15),
            step: num(sec + ':TrailingStepPoints', profile === 'bn' ? 10 : 5),
            stepSl: num(sec + ':TrailingStepSlPoints', profile === 'bn' ? 5 : 3),
            target: num(sec + ':TrailingTargetPoints', profile === 'bn' ? 50 : 30),
            lock: num(sec + ':TrailingTargetLockPoints', profile === 'bn' ? 25 : 20)
        };
        // After the target both indices have their own step; 0 (or missing) means "same as before the target".
        const postStep = num(sec + ':TrailingPostStepPoints', 0);
        const postStepSl = num(sec + ':TrailingPostStepSlPoints', 0);
        p.postStep = postStep > 0 ? postStep : p.step;
        p.postStepSl = postStepSl > 0 ? postStepSl : p.stepSl;
        return p;
    }

    function renderChart() {
        const svg = $('#stChart');
        if (!svg || svg.closest('[data-st-panel]')?.hidden) return;

        const p = params();
        const W = 640, H = 280;
        const xMax = Math.max(60, Math.ceil((p.target * 2) / 10) * 10);
        const offs = [];
        for (let g = 0; g <= xMax; g++) offs.push(slOffset(p, g));
        const yLo = Math.floor((Math.min(...offs, 0) - 5) / 10) * 10;
        const yHi = Math.ceil((Math.max(...offs, 0) + 10) / 10) * 10;
        const X = (g) => (g / xMax) * W;
        const Y = (v) => ((yHi - v) / (yHi - yLo)) * H;
        const f = (n) => n.toFixed(1);
        const yStep = (yHi - yLo) > 120 ? 40 : 20;
        const xStep = xMax > 80 ? 20 : 10;

        let grid = '', labels = '';
        for (let v = Math.ceil(yLo / yStep) * yStep; v <= yHi; v += yStep) {
            grid += 'M0 ' + f(Y(v)) + 'H' + W;
            labels += '<text x="-8" y="' + f(Y(v) + 4) + '" text-anchor="end">' + (v === 0 ? '0' : sgn(v)) + '</text>';
        }
        for (let g = 0; g <= xMax; g += xStep) {
            grid += 'M' + f(X(g)) + ' 0V' + H;
            labels += '<text x="' + f(X(g)) + '" y="' + (H + 20) + '" text-anchor="middle">' + g + '</text>';
        }
        let line = '';
        offs.forEach((v, g) => { line += (g === 0 ? 'M' : 'L') + f(X(g)) + ' ' + f(Y(v)); });
        const area = line + 'L' + f(X(xMax)) + ' ' + f(Y(0)) + 'L0 ' + f(Y(0)) + 'Z';
        const g0 = Math.min(gain, xMax);
        const curOff = slOffset(p, g0);
        const zeroY = f(Y(0));

        svg.setAttribute('viewBox', '-52 -26 700 340');
        svg.innerHTML =
            '<defs>' +
            '<clipPath id="stClipPos"><rect x="0" y="0" width="' + W + '" height="' + zeroY + '"></rect></clipPath>' +
            '<clipPath id="stClipNeg"><rect x="0" y="' + zeroY + '" width="' + W + '" height="' + H + '"></rect></clipPath>' +
            '</defs>' +
            '<path d="' + grid + '" stroke="#EFEADD" stroke-width="1" fill="none"></path>' +
            '<path d="' + area + '" fill="#17803D" fill-opacity="0.14" stroke="none" clip-path="url(#stClipPos)"></path>' +
            '<path d="' + area + '" fill="#B42318" fill-opacity="0.12" stroke="none" clip-path="url(#stClipNeg)"></path>' +
            '<path d="M0 ' + zeroY + 'H' + W + '" stroke="#5B6672" stroke-width="1.5" stroke-dasharray="5 5" fill="none"></path>' +
            '<path d="M' + f(X(p.target)) + ' 0V' + H + '" stroke="#C77D0A" stroke-width="1.5" stroke-dasharray="3 5" fill="none"></path>' +
            '<path d="' + line + '" stroke="#0E6B63" stroke-width="3" stroke-linejoin="round" fill="none"></path>' +
            '<path d="M' + f(X(g0)) + ' 0V' + H + '" stroke="#14202B" stroke-width="1.5" stroke-dasharray="2 4" fill="none"></path>' +
            '<circle cx="' + f(X(g0)) + '" cy="' + f(Y(curOff)) + '" r="7" fill="#FFFDF8" stroke="#14202B" stroke-width="3"></circle>' +
            labels +
            '<text class="tgt" x="' + f(X(p.target)) + '" y="-10" text-anchor="middle">TARGET +' + p.target + '</text>';

        // insight line
        const before = slOffset(p, Math.max(0, p.target - 1));
        const jump = p.lock - before;
        const ins = $('#stInsight');
        if (ins) {
            ins.textContent = 'Just below the target the stop sits at ' + sgn(before) + ' points; at +' + p.target +
                ' it jumps to ' + sgn(p.lock) + ' (a ' + (jump >= 0 ? '+' : '−') + Math.abs(jump) + ' point leap). ' +
                'Between +' + p.step + ' and +' + Math.max(p.step, p.target - 1) + ' the stop only creeps ' + p.stepSl +
                ' per ' + p.step + ' points, so a trade can run a long way before it is protected.';
        }

        // what-if calculator: an illustrative entry premium (Rs 100 for Nifty, Rs 300 for Bank Nifty)
        const entry = profile === 'bn' ? 300 : 100;
        const lot = profile === 'bn' ? num('BankNifty:LotSize', 30) : num('Strategy:LotSize', 65);
        const pnl = curOff * lot;
        const set = (id, text) => { const el = document.getElementById(id); if (el) el.textContent = text; };
        set('stGainVal', '+' + g0);
        set('stCalcEntryLabel', profile === 'bn' ? 'Entry premium (target)' : 'Entry premium (example)');
        set('stCalcEntry', '₹' + inr(entry));
        set('stCalcPeak', '₹' + inr(entry + g0));
        set('stCalcSl', '₹' + inr(entry + curOff));
        const pnlEl = document.getElementById('stCalcPnl');
        if (pnlEl) {
            pnlEl.textContent = (pnl >= 0 ? '+' : '−') + '₹' + inr(Math.abs(pnl));
            pnlEl.style.color = pnl >= 0 ? 'var(--gain-ink)' : 'var(--loss)';
        }
    }

    // ------------------------------------------------------------------ Telegram test message
    const testBtn = $('#stTestTelegram');
    if (testBtn) {
        const out = $('#stTestResult');
        testBtn.addEventListener('click', async () => {
            const token = form.querySelector('input[name="__RequestVerificationToken"]')?.value;
            testBtn.disabled = true;
            if (out) { out.hidden = true; }
            try {
                const res = await fetch(form.dataset.testTelegramUrl, {
                    method: 'POST',
                    headers: { 'RequestVerificationToken': token || '', Accept: 'application/json' }
                });
                const data = await res.json();
                if (out) {
                    out.textContent = data.message || (data.ok ? 'Test message sent.' : 'Failed.');
                    out.classList.toggle('bad', !data.ok);
                    out.hidden = false;
                }
            } catch {
                if (out) { out.textContent = 'Could not reach the dashboard server.'; out.classList.add('bad'); out.hidden = false; }
            } finally {
                testBtn.disabled = false;
            }
        });
    }

    // ------------------------------------------------------------------ Upstox token card
    const tokenCard = $('#stTokenCard');
    if (tokenCard) {
        const led = $('#stTokenLed'), text = $('#stTokenText'), btn = $('#stTokenBtn');
        btn?.addEventListener('click', () => window.open(tokenCard.dataset.authorizeUrl, '_blank', 'noopener'));

        const check = async () => {
            try {
                const res = await fetch(tokenCard.dataset.statusUrl, { headers: { Accept: 'application/json' } });
                if (!res.ok) throw new Error('bad status');
                const d = await res.json();
                if (d.generated) {
                    const t = d.generatedAt ? new Date(d.generatedAt).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' }) : '';
                    led.className = 'st-led ok';
                    text.textContent = 'Generated today' + (t ? ' · ' + t : '');
                    btn.textContent = 'Generate again';
                } else {
                    led.className = 'st-led bad';
                    text.textContent = 'No token for today yet';
                    btn.textContent = 'Generate token';
                }
            } catch {
                led.className = 'st-led';
                text.textContent = 'Worker unreachable – status unknown';
            }
        };
        check();
        setInterval(check, 60000);
    }

    // ------------------------------------------------------------------ leave-page guard
    let submitting = false;
    form.addEventListener('submit', () => { submitting = true; });
    window.addEventListener('beforeunload', (e) => {
        if (submitting) return;
        if (fields().some((el) => !el.disabled && isChanged(el))) { e.preventDefault(); e.returnValue = ''; }
    });

    // ------------------------------------------------------------------ init
    fields().forEach((el) => { if (el.dataset.kind === 'bool') syncSwitch(el); });
    pickProfile('nifty', null);
    refresh();
})();