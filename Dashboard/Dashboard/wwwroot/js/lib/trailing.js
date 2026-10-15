// Trailing stop-loss formula -- the same one as AlgoWorker/Services/TrailingStop.cs, expressed as an offset from entry.
// p = { risk, step, stepSl, target, lock, postStep, postStepSl }; g = points gained at the peak.
export function slOffset(p, g) {
    if (g >= p.target) {
        const n = p.postStep > 0 ? Math.floor((g - p.target) / p.postStep) : 0;
        return p.lock + p.postStepSl * n;
    }
    const n = p.step > 0 ? Math.floor(g / p.step) : 0;
    return -p.risk + p.stepSl * n;
}
