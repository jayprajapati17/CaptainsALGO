// Small display helpers shared by the Dashboard scripts.
export const sgn = (v) => (v > 0 ? '+' : v < 0 ? '−' : '') + Math.abs(v);
export const inr = (v) => Math.round(v).toLocaleString('en-IN');
export const pad = (n) => String(n).padStart(2, '0');
