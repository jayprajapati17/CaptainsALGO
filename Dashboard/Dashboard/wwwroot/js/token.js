// Upstox daily token widget (Task 9). Loaded globally (see _Layout.cshtml), so it
// shows/updates on every page, not just Live.
//
// Flow (per the design doc, section 3a):
//   1. On load, GET /api/token/status from the Worker -- if already generated today,
//      show a disabled label with the time; else show an enabled "Generate Token" button.
//   2. Clicking the button opens /auth/upstox/authorize in a NEW TAB -- this is a full
//      browser navigation to the Worker (login/OTP happens on Upstox's own page), not a
//      fetch, so CORS doesn't apply to it.
//   3. The Worker's OAuth callback saves the token to the DB, then broadcasts
//      "TokenGenerated" over PositionHub -- this widget listens for that and flips to the
//      disabled state immediately, with no page refresh and no polling needed.
(function () {
    'use strict';

    const widget = document.getElementById('tokenWidget');
    if (!widget) return;

    const statusUrl = widget.dataset.statusUrl;
    const authorizeUrl = widget.dataset.authorizeUrl;
    const hubUrl = widget.dataset.hubUrl;

    function render(state, extra) {
        if (state === 'checking') {
            widget.innerHTML = '<span class="ct-sub">Checking token...</span>';
        } else if (state === 'generated') {
            const time = extra ? new Date(extra).toLocaleTimeString('en-IN', { hour: '2-digit', minute: '2-digit' }) : '';
            widget.innerHTML =
                `<span class="token-pill token-pill-ok" title="Today's Upstox token has been generated">` +
                `Token OK${time ? ' \u2022 ' + time : ''}</span>`;
        } else if (state === 'missing') {
            widget.innerHTML = '<span class="token-pill token-pill-missing">Token missing for today</span><button type="button" class="ct-btn" id="generateTokenBtn">Generate Token</button>';
            document.getElementById('generateTokenBtn').addEventListener('click', () => {
                window.open(authorizeUrl, '_blank', 'noopener');
            });
        } else {
            widget.innerHTML = '<span class="ct-sub" style="color:var(--loss-ink)" title="Worker Service unreachable">Token status unknown</span>';
        }
    }

    async function checkStatus() {
        try {
            const res = await fetch(statusUrl, { headers: { Accept: 'application/json' } });
            if (!res.ok) throw new Error('bad status');
            const data = await res.json();
            render(data.generated ? 'generated' : 'missing', data.generatedAt);
        } catch {
            render('error');
        }
    }

    render('checking');
    checkStatus();

    // Real-time flip the moment the Worker's OAuth callback finishes (design doc point 6) --
    // no need to poll or wait for the user to switch back to this tab.
    if (window.signalR) {
        const connection = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl)
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        connection.on('TokenGenerated', d => render('generated', d.generatedAt));
        connection.onreconnected(checkStatus); // catch anything generated while disconnected
        connection.start().catch(() => { /* status still shown from the initial fetch above */ });
    }

    // Safety net in case the SignalR event is ever missed (e.g. hub momentarily down) --
    // also covers a login completed in a tab that then gets closed without this one reconnecting.
    setInterval(checkStatus, 60000);
})();