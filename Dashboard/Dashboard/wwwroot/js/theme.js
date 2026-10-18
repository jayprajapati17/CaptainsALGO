// Light/dark toggle. The saved choice is applied before first paint by the inline script in _Layout.cshtml;
// this file only handles the button. No saved choice means "follow the device".
(function () {
    'use strict';
    const root = document.documentElement;
    const btn = document.getElementById('themeToggle');
    const label = document.getElementById('themeLabel');
    if (!btn) return;

    const isDark = () => (root.getAttribute('data-theme') || (matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light')) === 'dark';

    function paint() {
        const dark = isDark();
        root.setAttribute('data-bs-theme', dark ? 'dark' : 'light');
        label.textContent = dark ? 'Light mode' : 'Dark mode';
    }

    btn.addEventListener('click', () => {
        const next = isDark() ? 'light' : 'dark';
        root.setAttribute('data-theme', next);
        try { localStorage.setItem('ct-theme', next); } catch { /* private window: the choice just won't stick */ }
        paint();
    });

    matchMedia('(prefers-color-scheme: dark)').addEventListener?.('change', paint);
    paint();
})();