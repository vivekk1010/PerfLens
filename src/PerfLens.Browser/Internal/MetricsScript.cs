namespace PerfLens.Browser.Internal;

/// <summary>JavaScript injected into measured pages.</summary>
internal static class MetricsScript
{
    /// <summary>
    /// Installed with AddInitScriptAsync so observers exist before any page script runs.
    /// CLS uses the web-vitals session-window definition: shifts less than 1 s apart, window capped at 5 s,
    /// score = largest window.
    /// </summary>
    public const string Init = """
        (() => {
          if (window.__perflens) return;
          const s = window.__perflens = { lcp: null, lcpElement: null, cls: 0, win: 0, winStart: -1, winLast: -1, longTasks: [] };
          try { performance.setResourceTimingBufferSize(2000); } catch (e) {}
          const describe = (el, url) => {
            if (!el) return url ? ('resource ' + String(url).slice(0, 150)) : null;
            let d = (el.tagName || el.nodeName || 'node').toLowerCase();
            if (el.id) d += '#' + el.id;
            if (el.classList && el.classList.length) d += '.' + Array.from(el.classList).slice(0, 3).join('.');
            return d.slice(0, 200);
          };
          const observe = (type, cb) => {
            try {
              new PerformanceObserver(list => {
                for (const e of list.getEntries()) { try { cb(e); } catch (_) {} }
              }).observe({ type, buffered: true });
            } catch (_) {}
          };
          observe('largest-contentful-paint', e => {
            s.lcp = e.startTime || e.renderTime || e.loadTime;
            s.lcpElement = describe(e.element, e.url);
          });
          observe('layout-shift', e => {
            if (e.hadRecentInput) return;
            if (s.winStart >= 0 && e.startTime - s.winLast < 1000 && e.startTime - s.winStart < 5000) {
              s.win += e.value;
            } else {
              s.win = e.value;
              s.winStart = e.startTime;
            }
            s.winLast = e.startTime;
            if (s.win > s.cls) s.cls = s.win;
          });
          observe('longtask', e => { s.longTasks.push({ start: e.startTime, duration: e.duration }); });
        })();
        """;

    /// <summary>Evaluated after the page settled. Returns a JSON string deserialized into <see cref="MetricsSnapshot"/>.</summary>
    public const string Collect = """
        () => {
          const num = v => (typeof v === 'number' && isFinite(v)) ? v : 0;
          const pos = v => Math.max(0, num(v));
          const s = window.__perflens;
          const out = { observers: !!s };
          const nav = performance.getEntriesByType('navigation')[0];
          if (nav) {
            out.navigation = {
              name: nav.name,
              redirect: pos(nav.redirectEnd - nav.redirectStart),
              dns: pos(nav.domainLookupEnd - nav.domainLookupStart),
              connect: pos(nav.connectEnd - nav.connectStart),
              tls: nav.secureConnectionStart > 0 ? pos(nav.connectEnd - nav.secureConnectionStart) : 0,
              server: nav.requestStart > 0 ? pos(nav.responseStart - nav.requestStart) : 0,
              ttfb: pos(nav.responseStart),
              download: nav.responseEnd > 0 ? pos(nav.responseEnd - nav.responseStart) : 0,
              domInteractive: pos(nav.domInteractive),
              domContentLoaded: pos(nav.domContentLoadedEventEnd),
              load: pos(nav.loadEventEnd),
              duration: pos(nav.duration),
              transferSize: pos(nav.transferSize),
              encodedBodySize: pos(nav.encodedBodySize),
              decodedBodySize: pos(nav.decodedBodySize),
              responseStatus: typeof nav.responseStatus === 'number' ? nav.responseStatus : null
            };
          }
          const fcpEntry = performance.getEntriesByType('paint').find(e => e.name === 'first-contentful-paint');
          out.fcp = fcpEntry ? fcpEntry.startTime : null;
          // Firefox/WebKit lack layout-shift and longtask entries: report "unknown", not a perfect 0.
          const supported = (PerformanceObserver && PerformanceObserver.supportedEntryTypes) || [];
          if (s) {
            out.lcp = s.lcp;
            out.lcpElement = s.lcpElement;
            out.cls = supported.includes('layout-shift') ? s.cls : null;
            let tbt = 0;
            for (const t of s.longTasks) {
              // Lighthouse semantics: only the part of a task after FCP counts, minus the 50 ms budget.
              const start = out.fcp === null ? t.start : Math.max(t.start, out.fcp);
              const duration = t.start + t.duration - start;
              if (duration > 50) tbt += duration - 50;
            }
            out.tbt = supported.includes('longtask') ? tbt : null;
            out.longTaskCount = s.longTasks.length;
          }
          out.resources = performance.getEntriesByType('resource').map(e => ({
            name: e.name,
            initiatorType: e.initiatorType,
            startTime: pos(e.startTime),
            duration: pos(e.duration),
            transferSize: pos(e.transferSize),
            encodedBodySize: pos(e.encodedBodySize),
            decodedBodySize: pos(e.decodedBodySize),
            responseStatus: typeof e.responseStatus === 'number' ? e.responseStatus : null
          }));
          try { out.domNodes = document.getElementsByTagName('*').length; } catch (_) { out.domNodes = null; }
          out.jsHeapUsed = (performance.memory && typeof performance.memory.usedJSHeapSize === 'number')
            ? performance.memory.usedJSHeapSize : null;
          return JSON.stringify(out);
        }
        """;

    public const string ExtractLinks = "() => Array.from(document.querySelectorAll('a[href]'), a => a.href)";
}
