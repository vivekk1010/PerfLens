/* PerfLens report runtime: vanilla JS, no network access. Reads #perflens-data and draws charts with Chart.js. */
(function () {
  'use strict';

  var dataEl = document.getElementById('perflens-data');
  var DATA = {};
  try { DATA = JSON.parse(dataEl ? dataEl.textContent : '{}') || {}; } catch (e) { DATA = {}; }
  var pages = DATA.pages || [];
  var apis = DATA.api || [];
  var bands = DATA.bands || {};
  var hasChart = typeof window.Chart === 'function';

  var C = {
    primary: '#3355d6', ttfb: '#6c8cff', fcp: '#22a699', lcp: '#f2994a', dcl: '#9b51e0', load: '#eb5757',
    good: '#0e8a4f', ni: '#e29a00', poor: '#c62828', grey: '#a0aec0', grid: '#edf1f7', text: '#5b6678'
  };
  var TYPE_COLORS = {
    document: '#3355d6', script: '#f2994a', stylesheet: '#9b51e0', image: '#22a699', font: '#eb5757',
    xhr: '#2d9cdb', fetch: '#56ccf2', media: '#f2c94c', other: '#a0aec0'
  };
  var EXTRA = ['#6fcf97', '#bb6bd9', '#f28c8c', '#219653', '#828282', '#27ae60'];
  var GRADE_COLORS = { A: '#0e8a4f', B: '#5a9e1a', C: '#c98a00', D: '#e0661b', F: '#c62828' };

  // ---------- formatting ----------
  function isNum(v) { return typeof v === 'number' && isFinite(v); }
  function fmtMs(v) {
    if (!isNum(v)) return 'n/a';
    return Math.abs(v) < 1000 ? Math.round(v) + ' ms' : (v / 1000).toFixed(2) + ' s';
  }
  function fmtBytes(v) {
    if (!isNum(v)) return 'n/a';
    if (Math.abs(v) < 1024) return Math.round(v) + ' B';
    var kb = v / 1024;
    return Math.abs(kb) < 1024 ? kb.toFixed(1) + ' KB' : (kb / 1024).toFixed(2) + ' MB';
  }
  function fmtCls(v) { return isNum(v) ? v.toFixed(3) : 'n/a'; }
  function fmtPct(v) { return isNum(v) ? (v > 0 ? '+' : '') + v.toFixed(1) + '%' : 'n/a'; }
  function fmtInt(v) { return isNum(v) ? Math.round(v).toLocaleString('en-US') : 'n/a'; }
  function shortLabel(s, n) { s = String(s == null ? '' : s); n = n || 28; return s.length > n ? s.slice(0, n - 1) + '…' : s; }
  function typeColor(t, i) { return TYPE_COLORS[t] || EXTRA[i % EXTRA.length]; }
  function statusColor(code, i) {
    var c = String(code).charAt(0);
    if (c === '2') return ['#0e8a4f', '#27ae60', '#6fcf97'][i % 3];
    if (c === '3') return '#2d9cdb';
    if (c === '4') return ['#e29a00', '#f2c94c'][i % 2];
    if (c === '5') return ['#c62828', '#eb5757'][i % 2];
    return ['#7f1d1d', '#828282', '#bb6bd9'][i % 3];
  }

  // ---------- Chart.js setup ----------
  /* Draws Good/Poor reference lines. options.plugins.plMarks = { lines: [{value, color, label}], perDataset: {i: [good, poor]} } */
  var marksPlugin = {
    id: 'plMarks',
    afterDatasetsDraw: function (chart, args, opts) {
      if (!opts) return;
      var ctx = chart.ctx, area = chart.chartArea;
      var horizontal = chart.options.indexAxis === 'y';
      var valueScale = horizontal ? chart.scales.x : chart.scales.y;
      if (!valueScale) return;
      ctx.save();
      ctx.setLineDash([5, 4]);
      ctx.lineWidth = 1.5;
      (opts.lines || []).forEach(function (l) {
        var p = valueScale.getPixelForValue(l.value);
        if (!isFinite(p) || p < (horizontal ? area.left : area.top) || p > (horizontal ? area.right : area.bottom)) return;
        ctx.strokeStyle = l.color;
        ctx.beginPath();
        if (horizontal) { ctx.moveTo(p, area.top); ctx.lineTo(p, area.bottom); } else { ctx.moveTo(area.left, p); ctx.lineTo(area.right, p); }
        ctx.stroke();
        if (l.label) {
          ctx.fillStyle = l.color;
          ctx.font = '11px system-ui, sans-serif';
          ctx.textAlign = 'right';
          ctx.fillText(l.label, horizontal ? p - 4 : area.right - 4, horizontal ? area.top + 12 : p - 4);
        }
      });
      ctx.setLineDash([]);
      ctx.lineWidth = 2.5;
      var per = opts.perDataset || {};
      Object.keys(per).forEach(function (di) {
        var meta = chart.getDatasetMeta(Number(di));
        if (!meta || meta.hidden) return;
        var limits = per[di];
        var values = chart.data.datasets[Number(di)].data;
        meta.data.forEach(function (bar, bi) {
          if (!bar || !isFinite(bar.x) || !isNum(values[bi])) return;
          var half = (bar.width || 12) / 2 + 2;
          [[limits[0], C.good], [limits[1], C.poor]].forEach(function (pair) {
            var y = chart.scales.y.getPixelForValue(pair[0]);
            if (!isFinite(y) || y < area.top || y > area.bottom) return;
            ctx.strokeStyle = pair[1];
            ctx.beginPath(); ctx.moveTo(bar.x - half, y); ctx.lineTo(bar.x + half, y); ctx.stroke();
          });
        });
      });
      ctx.restore();
    }
  };

  if (hasChart) {
    Chart.register(marksPlugin);
    Chart.defaults.font.family = 'system-ui, -apple-system, "Segoe UI", Roboto, sans-serif';
    Chart.defaults.font.size = 12;
    Chart.defaults.color = C.text;
    Chart.defaults.borderColor = C.grid;
    Chart.defaults.maintainAspectRatio = false;
    Chart.defaults.animation.duration = 350;
    Chart.defaults.plugins.legend.labels.boxWidth = 12;
  }

  function make(id, config) {
    var el = document.getElementById(id);
    if (!el || !hasChart) return null;
    if (el.dataset.drawn) return null;
    el.dataset.drawn = '1';
    try { return new Chart(el, config); } catch (e) { if (window.console) console.warn('PerfLens chart ' + id + ' failed: ' + e.message); return null; }
  }
  function msAxis(extra) {
    var a = { beginAtZero: true, ticks: { callback: function (v) { return fmtMs(v); } } };
    for (var k in extra || {}) a[k] = extra[k];
    return a;
  }
  function tip(fn) { return { callbacks: { label: function (c) { return (c.dataset.label ? c.dataset.label + ': ' : '') + fn(c.raw, c); } } }; }
  function measured() { return pages.filter(function (p) { return p.measured; }); }

  // ---------- header gauge ----------
  function drawGauge() {
    if (!isNum(DATA.score)) return;
    var s = Math.max(0, Math.min(100, DATA.score));
    var col = GRADE_COLORS[DATA.grade] || (s >= 90 ? C.good : s >= 60 ? C.ni : C.poor);
    make('chart-score-gauge', {
      type: 'doughnut',
      data: { labels: ['Score', ''], datasets: [{ data: [s, 100 - s], backgroundColor: [col, '#e8ecf3'], borderWidth: 0 }] },
      options: { rotation: -90, circumference: 180, cutout: '72%', responsive: false,
        plugins: { legend: { display: false }, tooltip: { enabled: false } } }
    });
  }

  // ---------- overview ----------
  function drawOverview() {
    var ps = measured();
    if (!ps.length) return;
    var labels = ps.map(function (p) { return shortLabel(p.name); });
    var stat = DATA.statistic || 'p75';

    make('chart-vitals', {
      type: 'bar',
      data: { labels: labels, datasets: [
        { label: 'TTFB', data: ps.map(function (p) { return p.ttfb; }), backgroundColor: C.ttfb },
        { label: 'FCP', data: ps.map(function (p) { return p.fcp; }), backgroundColor: C.fcp },
        { label: 'LCP', data: ps.map(function (p) { return p.lcp; }), backgroundColor: C.lcp }
      ] },
      options: {
        scales: { y: msAxis({ title: { display: true, text: stat } }) },
        plugins: {
          tooltip: tip(fmtMs),
          plMarks: { perDataset: { 0: bands.ttfb_ms || [800, 1800], 1: bands.fcp_ms || [1800, 3000], 2: bands.lcp_ms || [2500, 4000] } }
        }
      }
    });

    var phaseDefs = [
      ['dns', 'DNS', '#56ccf2'], ['connect', 'Connect', '#2d9cdb'], ['tls', 'TLS', '#3355d6'], ['other', 'Redirect / queue', '#cfd8e3'],
      ['server', 'Server', '#9b51e0'], ['download', 'Download', '#22a699'], ['dom', 'DOM processing', '#f2994a'], ['loadRest', 'Until load event', '#eb5757']
    ];
    make('chart-phases', {
      type: 'bar',
      data: { labels: labels, datasets: phaseDefs.map(function (d) {
        return { label: d[1], backgroundColor: d[2], data: ps.map(function (p) { return p.phases ? p.phases[d[0]] : null; }) };
      }) },
      options: { indexAxis: 'y', scales: { x: msAxis({ stacked: true }), y: { stacked: true } }, plugins: { tooltip: tip(fmtMs) } }
    });

    var types = [];
    ps.forEach(function (p) { Object.keys(p.byType || {}).forEach(function (t) { if (types.indexOf(t) < 0) types.push(t); }); });
    make('chart-weight', {
      type: 'bar',
      data: { labels: labels, datasets: types.map(function (t, i) {
        return { label: t, backgroundColor: typeColor(t, i), data: ps.map(function (p) { return (p.byType || {})[t] || 0; }) };
      }) },
      options: { scales: { x: { stacked: true }, y: { stacked: true, beginAtZero: true, ticks: { callback: function (v) { return fmtBytes(v); } } } },
        plugins: { tooltip: tip(fmtBytes) } }
    });

    make('chart-requests', {
      type: 'bar',
      data: { labels: labels, datasets: [{ label: 'Requests (' + stat + ')', data: ps.map(function (p) { return p.requests; }), backgroundColor: C.primary }] },
      options: { scales: { y: { beginAtZero: true } }, plugins: { legend: { display: false }, tooltip: tip(fmtInt) } }
    });

    var clsB = bands.cls || [0.1, 0.25], tbtB = bands.tbt_ms || [200, 600];
    function rateColor(v, b) { return !isNum(v) ? C.grey : v <= b[0] ? C.good : v <= b[1] ? C.ni : C.poor; }
    make('chart-cls', {
      type: 'bar',
      data: { labels: labels, datasets: [{ label: 'CLS', data: ps.map(function (p) { return p.cls; }),
        backgroundColor: ps.map(function (p) { return rateColor(p.cls, clsB); }) }] },
      options: { scales: { y: { beginAtZero: true, suggestedMax: clsB[1] * 1.2, ticks: { callback: function (v) { return fmtCls(v); } } } },
        plugins: { legend: { display: false }, tooltip: tip(fmtCls),
          plMarks: { lines: [{ value: clsB[0], color: C.good, label: 'Good ' + clsB[0] }, { value: clsB[1], color: C.poor, label: 'Poor ' + clsB[1] }] } } }
    });
    make('chart-tbt', {
      type: 'bar',
      data: { labels: labels, datasets: [{ label: 'TBT', data: ps.map(function (p) { return p.tbt; }),
        backgroundColor: ps.map(function (p) { return rateColor(p.tbt, tbtB); }) }] },
      options: { scales: { y: msAxis({ suggestedMax: tbtB[1] * 1.2 }) },
        plugins: { legend: { display: false }, tooltip: tip(fmtMs),
          plMarks: { lines: [{ value: tbtB[0], color: C.good, label: 'Good ' + fmtMs(tbtB[0]) }, { value: tbtB[1], color: C.poor, label: 'Poor ' + fmtMs(tbtB[1]) }] } } }
    });

    var scored = pages.filter(function (p) { return isNum(p.score); });
    make('chart-scores', {
      type: 'bar',
      data: { labels: scored.map(function (p) { return shortLabel(p.name); }), datasets: [{ label: 'Score',
        data: scored.map(function (p) { return p.score; }),
        backgroundColor: scored.map(function (p) { return GRADE_COLORS[p.grade] || C.grey; }) }] },
      options: { scales: { y: { beginAtZero: true, max: 100 } },
        plugins: { legend: { display: false }, tooltip: tip(function (v, c) { return Math.round(v) + ' (grade ' + scored[c.dataIndex].grade + ')'; }),
          plMarks: { lines: [{ value: 90, color: C.good, label: 'A' }, { value: 60, color: C.ni, label: 'C' }] } } }
    });
  }

  // ---------- per page (lazy: drawn when the card is opened) ----------
  function drawPage(idx) {
    var p = null;
    for (var i = 0; i < pages.length; i++) if (pages[i].idx === idx) { p = pages[i]; break; }
    if (!p || !p.measured) return;
    var its = (p.iterations || []).filter(function (it) { return it.ok; });
    var labels = its.map(function (it) { return '#' + it.index + (it.warmup ? ' (warm-up)' : ''); });
    var series = [['ttfb', 'TTFB', C.ttfb], ['fcp', 'FCP', C.fcp], ['lcp', 'LCP', C.lcp], ['dcl', 'DOMContentLoaded', C.dcl], ['load', 'Load', C.load]];
    make('pc-iter-' + idx, {
      type: 'line',
      data: { labels: labels, datasets: series.map(function (s) {
        return {
          label: s[1], data: its.map(function (it) { return it[s[0]]; }), borderColor: s[2], backgroundColor: s[2], tension: 0.2, spanGaps: true,
          pointRadius: 4,
          pointBackgroundColor: its.map(function (it) { return it.warmup ? '#ffffff' : s[2]; }),
          pointBorderColor: its.map(function (it) { return it.warmup ? C.grey : s[2]; }),
          segment: { borderDash: function (ctx) { return its[ctx.p0DataIndex] && its[ctx.p0DataIndex].warmup ? [4, 4] : undefined; },
                     borderColor: function (ctx) { return its[ctx.p0DataIndex] && its[ctx.p0DataIndex].warmup ? C.grey : undefined; } }
        };
      }) },
      options: { interaction: { mode: 'index', intersect: false }, scales: { y: msAxis() }, plugins: { tooltip: tip(fmtMs) } }
    });

    var types = Object.keys(p.byType || {}).filter(function (t) { return p.byType[t] > 0; });
    if (types.length) {
      make('pc-types-' + idx, {
        type: 'doughnut',
        data: { labels: types, datasets: [{ data: types.map(function (t) { return p.byType[t]; }), backgroundColor: types.map(typeColor), borderWidth: 1 }] },
        options: { cutout: '55%', plugins: { legend: { position: 'right' }, tooltip: tip(fmtBytes) } }
      });
    }
  }

  // ---------- API ----------
  function drawApi() {
    apis.forEach(function (a, i) {
      if (a.hasLatency) {
        make('api-lat-' + i, {
          type: 'bar',
          data: { labels: ['min', 'p50', 'p75', 'p90', 'p95', 'p99', 'max'], datasets: [{ label: 'Latency', data: a.latency,
            backgroundColor: ['#6fcf97', '#27ae60', '#56ccf2', '#2d9cdb', '#f2994a', '#eb5757', '#c62828'] }] },
          options: { scales: { y: msAxis() }, plugins: { legend: { display: false }, tooltip: tip(fmtMs) } }
        });
      }
      var codes = Object.keys(a.status || {});
      if (codes.length) {
        make('api-status-' + i, {
          type: 'doughnut',
          data: { labels: codes, datasets: [{ data: codes.map(function (c) { return a.status[c]; }), backgroundColor: codes.map(statusColor), borderWidth: 1 }] },
          options: { cutout: '55%', plugins: { legend: { position: 'right' }, tooltip: tip(fmtInt) } }
        });
      }
      var tl = a.timeline || [];
      if (tl.length) {
        make('api-time-' + i, {
          type: 'bar',
          data: { labels: tl.map(function (t) { return t.s + 's'; }), datasets: [
            { type: 'bar', label: 'Successful req/s', data: tl.map(function (t) { return Math.max(0, t.req - t.err); }), backgroundColor: '#9fb3f5', stack: 'r', yAxisID: 'y', order: 2 },
            { type: 'bar', label: 'Errors/s', data: tl.map(function (t) { return t.err; }), backgroundColor: C.poor, stack: 'r', yAxisID: 'y', order: 2 },
            { type: 'line', label: 'p50 latency', data: tl.map(function (t) { return t.p50; }), borderColor: C.good, backgroundColor: C.good, yAxisID: 'y1', pointRadius: 0, tension: 0.2, order: 1 },
            { type: 'line', label: 'p95 latency', data: tl.map(function (t) { return t.p95; }), borderColor: C.lcp, backgroundColor: C.lcp, yAxisID: 'y1', pointRadius: 0, tension: 0.2, order: 1 }
          ] },
          options: {
            interaction: { mode: 'index', intersect: false },
            scales: {
              x: { stacked: true },
              y: { stacked: true, beginAtZero: true, title: { display: true, text: 'requests / s' } },
              y1: msAxis({ position: 'right', grid: { drawOnChartArea: false }, title: { display: true, text: 'latency' } })
            },
            plugins: { tooltip: tip(function (v, c) { return c.dataset.yAxisID === 'y1' ? fmtMs(v) : fmtInt(v); }) }
          }
        });
      }
      var h = a.histogram || [];
      if (h.length) {
        make('api-hist-' + i, {
          type: 'bar',
          data: { labels: h.map(function (b) { return fmtMs(b.lo) + '–' + fmtMs(b.hi); }), datasets: [{ label: 'Requests', data: h.map(function (b) { return b.n; }),
            backgroundColor: C.primary, barPercentage: 1, categoryPercentage: 0.95 }] },
          options: { scales: { y: { beginAtZero: true } }, plugins: { legend: { display: false }, tooltip: tip(fmtInt) } }
        });
      }
    });
  }

  function drawRegressions() {
    var rs = DATA.regressions || [];
    if (!rs.length) return;
    make('chart-regressions', {
      type: 'bar',
      data: { labels: rs.map(function (r) { return shortLabel(r.label, 48); }), datasets: [{ label: 'Delta %', data: rs.map(function (r) { return r.delta; }),
        backgroundColor: rs.map(function (r) { return r.state === 'regressed' ? C.poor : r.state === 'improved' ? C.good : C.grey; }) }] },
      options: { indexAxis: 'y', scales: { x: { ticks: { callback: function (v) { return v + '%'; } } } },
        plugins: { legend: { display: false }, tooltip: tip(fmtPct), plMarks: { lines: [{ value: 0, color: C.text }] } } }
    });
  }

  // ---------- interactivity ----------
  function $all(sel, root) { return Array.prototype.slice.call((root || document).querySelectorAll(sel)); }

  function initPageCards() {
    $all('details.page-card').forEach(function (d) {
      d.addEventListener('toggle', function () { if (d.open) drawPage(Number(d.dataset.idx)); });
      if (d.open) drawPage(Number(d.dataset.idx));
    });
    var container = document.getElementById('page-cards');
    $all('[data-sort-pages]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var mode = btn.dataset.sortPages;
        $all('[data-sort-pages]').forEach(function (b) { b.classList.toggle('active', b === btn); });
        var cards = $all('details.page-card', container);
        cards.sort(function (a, b) {
          if (mode === 'name') return a.dataset.name.localeCompare(b.dataset.name);
          return Number(a.dataset.score) - Number(b.dataset.score) || a.dataset.name.localeCompare(b.dataset.name);
        });
        cards.forEach(function (c) { container.appendChild(c); });
      });
    });
    $all('[data-expand]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var open = btn.dataset.expand === 'open';
        $all('details.page-card').forEach(function (d) { d.open = open; if (open) drawPage(Number(d.dataset.idx)); });
      });
    });
    if (location.hash && /^#page-\d+$/.test(location.hash)) {
      var target = document.getElementById(location.hash.slice(1));
      if (target) target.open = true;
    }
    $all('a[href^="#page-"]').forEach(function (a) {
      a.addEventListener('click', function () {
        var t = document.getElementById(a.getAttribute('href').slice(1));
        if (t && t.tagName === 'DETAILS') { t.open = true; drawPage(Number(t.dataset.idx)); }
      });
    });
  }

  function initThresholds() {
    $all('[data-th-filter]').forEach(function (btn) {
      btn.addEventListener('click', function () {
        var onlyFailed = btn.dataset.thFilter === 'failed';
        $all('[data-th-filter]').forEach(function (b) { b.classList.toggle('active', b === btn); });
        $all('#threshold-table tbody tr').forEach(function (tr) { tr.hidden = onlyFailed && tr.dataset.passed === 'true'; });
      });
    });
  }

  function initFindings() {
    var table = document.getElementById('finding-table');
    if (!table) return;
    var search = document.getElementById('finding-search');
    var cat = document.getElementById('finding-category');
    var count = document.getElementById('finding-count');
    var rows = $all('tbody tr', table);
    function apply() {
      var active = {};
      $all('.sev-toggle').forEach(function (b) { active[b.dataset.sev] = b.getAttribute('aria-pressed') === 'true'; });
      var q = (search && search.value || '').trim().toLowerCase();
      var c = cat ? cat.value : '';
      var shown = 0;
      rows.forEach(function (tr) {
        var ok = active[tr.dataset.sev] !== false && (!c || tr.dataset.cat === c) && (!q || tr.textContent.toLowerCase().indexOf(q) >= 0);
        tr.hidden = !ok;
        if (ok) shown++;
      });
      if (count) count.textContent = shown + ' of ' + rows.length + ' shown';
    }
    $all('.sev-toggle').forEach(function (b) {
      b.addEventListener('click', function () { b.setAttribute('aria-pressed', b.getAttribute('aria-pressed') === 'true' ? 'false' : 'true'); apply(); });
    });
    if (search) search.addEventListener('input', apply);
    if (cat) cat.addEventListener('change', apply);
    apply();
  }

  function initSortableTables() {
    $all('table.sortable').forEach(function (table) {
      $all('th[data-sort]', table).forEach(function (th) {
        th.addEventListener('click', function () {
          var idx = Array.prototype.indexOf.call(th.parentNode.children, th);
          var numeric = th.dataset.sort === 'num';
          var dir = th.classList.contains('desc') ? 1 : -1;
          $all('th', table).forEach(function (h) { h.classList.remove('asc', 'desc'); });
          th.classList.add(dir === 1 ? 'asc' : 'desc');
          var tbody = table.tBodies[0];
          var rs = $all('tr', tbody);
          rs.sort(function (a, b) {
            var ca = a.children[idx], cb = b.children[idx];
            var va = numeric ? Number(ca.dataset.v || ca.textContent) : ca.textContent.trim().toLowerCase();
            var vb = numeric ? Number(cb.dataset.v || cb.textContent) : cb.textContent.trim().toLowerCase();
            return (va < vb ? -1 : va > vb ? 1 : 0) * dir;
          });
          rs.forEach(function (r) { tbody.appendChild(r); });
        });
      });
    });
  }

  function initNavHighlight() {
    if (!('IntersectionObserver' in window)) return;
    var links = {};
    $all('.topnav a[href^="#"]').forEach(function (a) { links[a.getAttribute('href').slice(1)] = a; });
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) {
        if (e.isIntersecting && links[e.target.id]) {
          Object.keys(links).forEach(function (k) { links[k].classList.remove('active'); });
          links[e.target.id].classList.add('active');
        }
      });
    }, { rootMargin: '-60px 0px -70% 0px' });
    $all('section.section[id]').forEach(function (s) { io.observe(s); });
  }

  function initPrint() {
    window.addEventListener('beforeprint', function () {
      $all('details').forEach(function (d) { d.open = true; });
      pages.forEach(function (p) { drawPage(p.idx); });
      $all('#finding-table tbody tr, #threshold-table tbody tr').forEach(function (tr) { tr.hidden = false; });
    });
  }

  function safe(fn) { try { fn(); } catch (e) { if (window.console) console.warn('PerfLens: ' + e.message); } }
  safe(drawGauge);
  safe(drawOverview);
  safe(drawApi);
  safe(drawRegressions);
  safe(initPageCards);
  safe(initThresholds);
  safe(initFindings);
  safe(initSortableTables);
  safe(initNavHighlight);
  safe(initPrint);
  window.PerfLensReport = { data: DATA, drawPage: drawPage };
})();
