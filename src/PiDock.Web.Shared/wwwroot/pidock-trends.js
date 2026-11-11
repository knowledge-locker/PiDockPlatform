// The chart remains server-rendered; the cursor reads only its visible plotted points.
document.addEventListener('pointermove', event => {
    const svg = event.target.closest?.('svg[data-pidock-trend]');
    if (!svg) return;
    const bounds = svg.getBoundingClientRect();
    const x = Math.max(48, Math.min(952, (event.clientX - bounds.left) / bounds.width * 1000));
    let best = null, distance = Infinity;
    for (const line of svg.querySelectorAll('polyline')) {
        for (let index = 0; index < line.points.numberOfItems; index++) {
            const point = line.points.getItem(index), delta = Math.abs(point.x - x);
            if (delta < distance) { best = point; distance = delta; }
        }
    }
    const output = svg.closest('.trend-track').querySelector('output');
    const cursor = svg.querySelector('.trend-cursor');
    if (!best || distance > 18) { output.textContent = 'No sample near this time'; cursor.style.display = 'none'; return; }
    const time = Number(svg.dataset.from) + (best.x - 48) / 904 * (Number(svg.dataset.to) - Number(svg.dataset.from));
    const value = (160 - best.y) / 140 * Number(svg.dataset.max);
    cursor.setAttribute('x1', best.x); cursor.setAttribute('x2', best.x); cursor.style.display = '';
    output.textContent = new Date(time).toISOString().replace('T', ' ').slice(0, 19) + ' UTC · ~' + value.toFixed(2) + ' ' + svg.dataset.unit;
});
