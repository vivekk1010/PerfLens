# PerfLens — Product Requirements Document

| | |
|---|---|
| **Product** | PerfLens — whole-application web performance benchmarking CLI |
| **Author / owner** | Vivek Kaushik |
| **Version covered** | 1.0 |
| **Status** | Implemented (v1.0); roadmap items are proposals |
| **License** | MIT |
| **Repository** | [github.com/vivekk1010/PerfLens](https://github.com/vivekk1010/PerfLens) |
| **Related documents** | [ARCHITECTURE.md](ARCHITECTURE.md), [DETAILS.md](DETAILS.md), [README](../README.md) |

---

## 1. Overview

PerfLens is an open-source .NET 10 command-line tool that measures the performance of a **whole deployed web application**: many pages (configured and discovered by crawling), logged-in areas, and the HTTP APIs behind them. It gives you repeatable numbers with statistics you can defend, not a single snapshot.

For each run PerfLens:

1. Opens every page in a real browser (Chromium, Firefox or WebKit through Playwright) several times.
2. Collects navigation timing, Core Web Vitals, resource weight, API calls and errors on every visit.
3. Optionally load-tests the application's APIs with a built-in HTTP load generator.
4. Analyses the results: statistics, Core Web Vitals ratings, scores, thresholds, rule-based findings and regressions against a baseline.
5. Writes a self-contained HTML report with charts, plus `results.json`, a Markdown summary and CSV files.
6. Returns an exit code that CI pipelines can use as a quality gate.

**PerfLens is black-box.** It needs the application's UI URL, plus credentials, login steps or headers for protected areas, and optionally API endpoint paths. It does **not** need the application's source code or repositories. Every metric comes from:

- browser platform APIs: Navigation Timing, Paint Timing, Largest Contentful Paint, Layout Instability, Long Tasks and Resource Timing;
- Playwright network, console and page-error events;
- HTTP requests sent to the deployed endpoints.

Access to the source repository is only useful for optional extras, for example running PerfLens inside the application's own CI pipeline, correlating results with server-side traces/APM, or writing BenchmarkDotNet micro-benchmarks for specific code paths. None of those are needed to measure an application.

## 2. Problem statement

Teams often need to answer "how fast is our application, where is it slow, and did the last release make it worse?" across the **whole product**, not only the home page. Existing tools each cover part of this:

| Tool | Strength | Gap for whole-app benchmarking |
|---|---|---|
| Lighthouse / PageSpeed Insights | Excellent single-page audit and lab Web Vitals | One page per run; no built-in multi-iteration statistics, crawl, API load or cross-page report; awkward for authenticated pages at scale |
| BenchmarkDotNet | Rigorous in-process micro-benchmarks | Measures .NET code in-process; does not measure a deployed app or a browser |
| k6 / JMeter / NBomber / Gatling | Mature API load testing | No real-browser page metrics (LCP, CLS, TBT, resource weight); page tests need scripted code per page |
| Synthetic monitoring SaaS | Continuous dashboards | Paid and hosted, usually per-URL scripts, often cannot reach internal staging |
| Real User Monitoring | Real-world data | Needs code/snippet deployed in the app and real traffic; not usable before release |

The result is either hand-built scripts per page, one-off Lighthouse screenshots that cannot be compared, or no measurement at all. PerfLens fills the gap with one configuration file, one command and one shareable report covering pages and APIs, with statistics and CI gating.

## 3. Goals and non-goals

### 3.1 Goals

- **G1 — Whole-app coverage without per-page code.** Measure any number of pages from a list of paths and/or a same-origin crawl, including authenticated areas.
- **G2 — Evidence-based numbers.** Multiple iterations with warm-up, robust statistics (median, p75, p90, p95, p99, standard deviation, coefficient of variation) and explicit measurement conditions (cache mode, throttling, device).
- **G3 — Pages and APIs in one run.** Real-browser page metrics and HTTP API load results in the same report and the same score.
- **G4 — Actionable output.** Findings with severity, category and recommendation; Core Web Vitals ratings; plain-English highlights.
- **G5 — CI-ready.** Thresholds, minimum score, baseline regression detection, machine-readable JSON, Markdown summary and deterministic exit codes.
- **G6 — Shareable report.** One self-contained, offline HTML file you can attach to a ticket or email.
- **G7 — Black-box.** Requires only URLs and credentials, never source access.
- **G8 — Easy install.** One script on Linux/WSL/macOS; runs on Windows with the .NET SDK.

### 3.2 Non-goals

- **Not a functional test tool.** PerfLens does not assert business behaviour, fill forms other than login, or replace Playwright/Selenium test suites.
- **Not a large-scale distributed load generator.** The load engine runs from one machine and suits tens to low thousands of requests per second. Large-scale or geographically distributed load belongs to dedicated tools (k6 Cloud, Gatling, Azure Load Testing).
- **Not an APM or RUM product.** No agents in the application, no server-side tracing, no production traffic sampling.
- **No source-code access required, no code instrumentation.**
- **Not a security scanner.** It does not probe for vulnerabilities.
- **Not a Lighthouse replacement for SEO/accessibility audits.** Only performance is in scope for v1.0.

## 4. Personas

| Persona | Context | What they need from PerfLens |
|---|---|---|
| **Priya — Performance engineer** | Owns performance for a product line; runs investigations before releases | Accurate, repeatable numbers; control over iterations, cache, throttling and load profile; raw data (JSON/CSV) for further analysis |
| **Daniel — Dev team lead** | Wants to know whether a feature branch makes the app slower | One command in CI, clear pass/fail, regression list against `main`, short Markdown summary in the PR |
| **Mei — QA engineer** | Adds non-functional checks to the release checklist | No code to write; config file with pages and a login; readable HTML report to attach to the release ticket |
| **Sam — SRE / release manager** | Decides go/no-go; cares about API latency and error rate under load | API p95/p99, error rate, throughput, degradation under load, thresholds, exit codes |
| **Alex — Forward-deployed engineer (FDE)** | Assesses a customer's application, often without access to its source | Point at a URL, log in, crawl, produce a professional report with findings and recommendations the same day |

## 5. Use cases and user stories

Each story has acceptance criteria (AC). IDs are referenced by functional requirements.

### US-01 — Quick assessment of an unknown app
*As Alex, I want to run PerfLens against a URL with no config file, so that I can get a first report in minutes.*

- AC1: `perflens quick https://app.example.com` measures that URL (3 iterations by default) and writes a report.
- AC2: `--pages /,/about` measures the listed paths; `--crawl --max-pages 20` adds same-origin crawled pages.
- AC3: `--api /api/products` adds API load tests for the listed paths.
- AC4: The output folder contains `report.html`, `results.json`, `summary.md` and CSV files.

### US-02 — Repeatable configured benchmark
*As Priya, I want to describe pages, auth, browser settings and thresholds in one YAML file, so that every run uses identical conditions.*

- AC1: `perflens init` writes a commented starter config.
- AC2: `perflens validate -c perflens.yaml` reports every configuration error at once, with the property path (e.g. `api.profile.durationSeconds must be >= 1.`).
- AC3: The run records its settings (iterations, cache mode, throttling, load profile, tool/browser versions) in `results.json` and the report.

### US-03 — Measure logged-in pages
*As Mei, I want PerfLens to log in through the UI once and reuse the session, so that I can measure pages behind authentication.*

- AC1: Login steps (Fill, Click, Press, WaitForSelector, WaitForUrl, Wait) run once before measurement.
- AC2: Success is verified with `successUrlPattern` and/or `successSelector`; a failed login is reported as a run warning and the run continues unauthenticated.
- AC3: Passwords come from environment variables (`${ENV:APP_PASSWORD}`) and never appear in outputs.
- AC4: The authenticated state is reused for every measured browser context, including cold-cache contexts.

### US-04 — Discover pages automatically
*As Alex, I want PerfLens to crawl the site, so that I do not need to know every URL.*

- AC1: The crawl follows `a[href]` links breadth-first, same-origin only, up to `crawl.maxDepth` and `crawl.maxPages`.
- AC2: Links matching `crawl.exclude` (default: logout, signout, delete) are never visited.
- AC3: Links returning HTTP status ≥ 400 are listed as broken links with their referrer.

### US-05 — Understand why a page is slow
*As Priya, I want a per-page breakdown, so that I know whether the server, network, render or JavaScript is the bottleneck.*

- AC1: Each page shows navigation phases (DNS, connect, TLS, server, download, DOM interactive, DOMContentLoaded, load), FCP, LCP (with element), CLS, TBT and long tasks.
- AC2: Each page shows resource counts and bytes by type, top largest resources with encoding and cache headers, and XHR/fetch calls with timings.
- AC3: Findings name the page and the resource or API call responsible, with a recommendation.

### US-06 — Load-test APIs
*As Sam, I want to load-test key APIs at a fixed concurrency or arrival rate, so that I know their latency and error rate under load.*

- AC1: Closed model (N virtual users) and open model (fixed requests/second) are supported, with warm-up and duration.
- AC2: Results include p50/p75/p90/p95/p99/max latency, histogram, per-second timeline, status codes, error types and throughput.
- AC3: In open model, latency is measured from the intended send time (no coordinated omission).
- AC4: The browser session's cookies can be reused for API calls (`auth.shareCookiesWithApi`).

### US-07 — Gate a pull request
*As Daniel, I want the CI job to fail when performance breaks agreed limits, so that regressions are caught before merge.*

- AC1: The process exits with code 1 when any threshold fails or the score is below `thresholds.minScore`, and `thresholds.failOnThresholdBreach` is true.
- AC2: `summary.md` is suitable for a PR comment or GitHub job summary.
- AC3: Configuration or argument errors exit with code 2; unexpected runtime errors exit with code 3. A browser that cannot launch does not crash the run: pages are reported unavailable, so the availability thresholds fail.

### US-08 — Detect regressions against main
*As Daniel, I want to compare a run with a previous run, so that I see what got slower.*

- AC1: `--baseline previous/results.json` compares key metrics per page and endpoint.
- AC2: A change counts as a regression only when it exceeds `output.regressionTolerancePct` **and** a minimum absolute change, so noise on tiny values is ignored.
- AC3: Regressions appear as `REGRESSION` findings and in a baseline-delta table; improvements are shown too.

### US-09 — Simulate mobile users
*As Priya, I want to emulate a mobile device, slow network and slow CPU, so that I can measure what mobile users experience.*

- AC1: `browser.device` accepts Playwright device names (e.g. `Pixel 7`).
- AC2: `browser.networkProfile` supports Fast3G, Slow4G, Regular4G and Custom; `browser.cpuThrottlingRate` slows the CPU (Chromium).
- AC3: The report states which emulation was active.

### US-10 — Re-render and share
*As Mei, I want to regenerate reports from saved results, so that I can produce a different format without re-running.*

- AC1: `perflens report results.json -o out --formats html,md` rewrites the requested formats.
- AC2: `report.html` opens offline with all charts and no external requests.

### US-11 — Install and diagnose
*As any user, I want a one-command install and a self-check, so that setup problems are obvious.*

- AC1: `./scripts/install.sh` installs the .NET 10 SDK if missing, publishes the CLI, adds a `perflens` wrapper and installs Playwright browsers.
- AC2: `perflens doctor` checks .NET, browser installation and a headless launch, and explains how to fix failures.
- AC3: On Ubuntu 20.04, PerfLens applies the Playwright host-platform override automatically.

### US-12 — Discover API endpoints from the UI
*As Alex, I want PerfLens to load-test the APIs the pages actually call, so that I do not need API documentation.*

- AC1: With `api.useDiscoveredEndpoints: true`, successful same-origin GET XHR/fetch calls seen during page runs are selected, most frequent first, up to `api.maxDiscoveredEndpoints`.
- AC2: Non-GET calls and third-party hosts are never selected.
- AC3: Endpoints already configured are not duplicated.

## 6. Functional requirements

### 6.1 Configuration (FR-01 – FR-08)

| ID | Requirement | Stories |
|---|---|---|
| FR-01 | Load configuration from YAML (`.yml`/`.yaml`) or JSON (any other extension). JSON allows comments and trailing commas. Property names are case-insensitive; camelCase is the documented form. Enum values are case-insensitive names. | US-02 |
| FR-02 | Substitute `${ENV:NAME}`, `${NAME}` and `${NAME:-default}` from environment variables before deserialising. Fail with a list of all unset variables that have no default. | US-03 |
| FR-03 | Validate the whole configuration and report **all** errors in one message with property paths. | US-02 |
| FR-04 | Require `target.baseUrl` as an absolute http(s) URL, and at least one of `pages`, `crawl.enabled` or `api.enabled`. | US-02 |
| FR-05 | Default a page's `name` to its `path` and an endpoint's `name` to `"METHOD url"`; reject duplicate page names. | US-02 |
| FR-06 | Provide `perflens init` to write a commented starter config and `perflens validate` to print a resolved summary. | US-02 |
| FR-07 | Allow CLI overrides for base URL, output directory, baseline, label, iterations, headed mode, disabling API/crawl and formats. | US-01, US-07 |
| FR-08 | Provide `perflens quick <url>` that builds an in-memory configuration from arguments. | US-01 |

### 6.2 Authentication (FR-10 – FR-15)

| ID | Requirement | Stories |
|---|---|---|
| FR-10 | Support `auth.type`: `None`, `Headers`, `Login`, `StorageState`. | US-03 |
| FR-11 | `Headers`: send `auth.headers` with every browser request and API load request. | US-03 |
| FR-12 | `Login`: navigate to `auth.login.url`, run steps (`Fill`, `Click`, `Press`, `WaitForSelector`, `WaitForUrl`, `Wait`) within `auth.login.timeoutMs`, then verify `successUrlPattern` and/or `successSelector`. | US-03 |
| FR-13 | Capture the authenticated storage state once and apply it to every browser context, including new cold-cache contexts. | US-03 |
| FR-14 | `StorageState`: load a Playwright storage state JSON file from `auth.storageStatePath`. | US-03 |
| FR-15 | When `auth.shareCookiesWithApi` is true, pass session cookies to same-origin API load requests. `target.headers` and `target.cookies` apply to all requests. | US-06 |

### 6.3 Page discovery (FR-20 – FR-25)

| ID | Requirement | Stories |
|---|---|---|
| FR-20 | Measure configured `pages` in order; paths are relative to `target.baseUrl` or absolute URLs. | US-02 |
| FR-21 | When `crawl.enabled`, crawl breadth-first from `crawl.startPaths`, following `a[href]` links with GET navigations only. | US-04 |
| FR-22 | Restrict the crawl to the base URL's origin when `crawl.sameOriginOnly` (default). | US-04 |
| FR-23 | Apply `crawl.include` (keep only matching URLs when non-empty) and `crawl.exclude` (skip matching URLs) regular expressions; enforce `maxDepth` and `maxPages`. | US-04 |
| FR-24 | De-duplicate URLs by canonical form (lower-case host, optional removal of query string and fragment, trailing slash removed). | US-04 |
| FR-25 | Record visited URLs, broken links (status ≥ 400 or navigation error, with referrer), skipped link count and crawl duration. Crawled pages are measured like configured pages, skipping ones already configured. | US-04 |

### 6.4 Page measurement (FR-30 – FR-42)

| ID | Requirement | Stories |
|---|---|---|
| FR-30 | Launch the configured engine (`chromium`, `firefox`, `webkit`), headless by default. | US-05 |
| FR-31 | Per page: run `browser.warmupIterations` unmeasured loads, then N measured iterations (`pages[].iterations` or `browser.iterations`). | US-05 |
| FR-32 | `cacheMode: Cold` uses a new browser context per iteration; `Warm` reuses one context per page. | US-05 |
| FR-33 | Measure pages sequentially (one page, one iteration at a time) to avoid interference. | US-05 |
| FR-34 | Install PerformanceObservers for LCP, layout shifts and long tasks before any page script runs. | US-05 |
| FR-35 | Wait for the ready condition (`waitUntil`, optional `waitForSelector`) then `browser.settleMs` before collecting. | US-05 |
| FR-36 | Collect navigation phases, FCP, LCP (+ element), CLS, TBT, long-task count, DOM node count and JS heap (where supported). | US-05 |
| FR-37 | Collect resources: total count, transfer and decoded bytes, by type, failed, third-party and cached counts, and the top `browser.topResources` resources with status, size, duration, encoding, cache-control and content type. | US-05 |
| FR-38 | When `browser.captureApiCalls`, record XHR/fetch calls with method, URL, normalised URL (`/api/products/{id}`), status, duration and bytes. | US-05, US-12 |
| FR-39 | Record console errors and uncaught page errors. | US-05 |
| FR-40 | Apply `browser.device`, `networkProfile`/`customNetwork` and `cpuThrottlingRate`. Network and CPU throttling use the Chrome DevTools Protocol and apply to Chromium only; other engines log a warning. | US-09 |
| FR-41 | When `browser.captureScreenshots`, save one screenshot per page to `screenshots/`. | US-05 |
| FR-42 | A failed iteration or page is recorded with its error and never aborts the run. | US-05 |

### 6.5 API load (FR-50 – FR-58)

| ID | Requirement | Stories |
|---|---|---|
| FR-50 | Load-test each endpoint in `api.endpoints` (method, URL, headers, body, content type, expected status) sequentially, one endpoint at a time. | US-06 |
| FR-51 | Closed model: `concurrency` virtual users send requests back-to-back. | US-06 |
| FR-52 | Open model: send at `ratePerSecond` on a fixed schedule regardless of response time; measure latency from the intended send time. | US-06 |
| FR-53 | Run `warmupSeconds` (not recorded) then `durationSeconds` of measurement; stop early at `maxRequests`; apply `timeoutMs` per request. | US-06 |
| FR-54 | Record latency statistics, histogram, per-second timeline (requests, errors, p50, p95, max), status codes, error types (`timeout`, `connection_error`, `unexpected_status`, `tls_error`, `other`, and `dropped` for open-model arrivals beyond the in-flight cap), bytes, RPS and error rate. | US-06 |
| FR-55 | Count success as a status in `expectedStatus`, or any 2xx/3xx when empty. | US-06 |
| FR-56 | Merge headers in the order `target` → `auth` → endpoint (later wins) and cookies from `target.cookies` plus shared session cookies. | US-06 |
| FR-57 | Per-endpoint `profile` overrides `api.profile`. | US-06 |
| FR-58 | With `api.useDiscoveredEndpoints`, add up to `api.maxDiscoveredEndpoints` same-origin, successful GET calls observed during page runs. | US-12 |

### 6.6 Analysis (FR-60 – FR-68)

| ID | Requirement | Stories |
|---|---|---|
| FR-60 | Summarise each metric per page over measured iterations only (warm-ups and failed iterations excluded): count, min, max, mean, median, p75, p90, p95, p99, standard deviation, CoV. | US-05 |
| FR-61 | Rate FCP, LCP, CLS, TBT and TTFB as Good / Needs improvement / Poor at `thresholds.pageStatistic` (default p75). | US-05 |
| FR-62 | Compute a 0–100 page score (Lighthouse-style log-normal curves), API score, overall score (70% pages / 30% API) and grade A–F. | US-07 |
| FR-63 | Evaluate thresholds: global `thresholds.page`/`thresholds.api`, overridden per page/endpoint; unavailable pages and unreachable endpoints fail. | US-07 |
| FR-64 | Produce findings from a rule catalog, each with rule ID, severity, category, title, detail, target, recommendation, metric and value. | US-05 |
| FR-65 | Compare with a baseline `results.json` using tolerance % plus an absolute-change guard; emit `REGRESSION` findings and delta entries. | US-08 |
| FR-66 | Aggregate XHR/fetch calls per page by method and normalised URL. | US-05 |
| FR-67 | Generate plain-English highlights (e.g. slowest page, worst vital, top issue). | US-05 |
| FR-68 | Set `passed = false` when any threshold fails or the score is below `thresholds.minScore`. | US-07 |

### 6.7 Reporting (FR-70 – FR-76)

| ID | Requirement | Stories |
|---|---|---|
| FR-70 | Write `report.html`: single self-contained offline file with inline Chart.js and data; no network access required. | US-10 |
| FR-71 | Write `results.json` with the full run (schema version, environment, settings, pages, iterations, crawl, API, analysis). It is also the baseline input. | US-08 |
| FR-72 | Write `summary.md` with score, pass/fail, threshold failures, top findings and regressions. | US-07 |
| FR-73 | Write CSVs: `pages.csv`, `iterations.csv`, `api.csv`, `findings.csv`, `thresholds.csv`. | US-02 |
| FR-74 | HTML-encode every untrusted string (URLs, titles, console messages, element descriptions) in the report. | — |
| FR-75 | Redact secret-looking query-string values in URLs written to reports. | — |
| FR-76 | `perflens report` re-renders any format from a saved `results.json`. | US-10 |

### 6.8 CI integration (FR-80 – FR-83)

| ID | Requirement | Stories |
|---|---|---|
| FR-80 | Exit codes: 0 success, 1 threshold/min-score failure (when enforced), 2 invalid config/arguments, 3 unexpected runtime error, 130 cancelled (Ctrl+C). | US-07 |
| FR-81 | Non-interactive by default; headless; progress lines suitable for CI logs; `--verbose` for detail. | US-07 |
| FR-82 | Support `--label` to tag runs (e.g. commit SHA). | US-08 |
| FR-83 | Documented GitHub Actions workflow: install, run, upload report artifact, publish `summary.md`, baseline from `main`. | US-07 |

### 6.9 Installation (FR-90 – FR-94)

| ID | Requirement | Stories |
|---|---|---|
| FR-90 | `scripts/install.sh` installs .NET 10 SDK to `~/.dotnet` if missing, publishes the CLI to `~/.local/lib/perflens`, creates `~/.local/bin/perflens`, and installs Playwright browsers. | US-11 |
| FR-91 | Installer options: `--with-deps`, `--browsers`, `--prefix`, `--dry-run`, `--uninstall` (plus `--skip-browsers`, `--skip-build`, `--update-profile`). Re-running updates an existing install. | US-11 |
| FR-92 | `perflens install-browsers [chromium|firefox|webkit|all] [--with-deps]`. | US-11 |
| FR-93 | `perflens doctor` checks prerequisites and a headless launch. | US-11 |
| FR-94 | Automatically set `PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu22.04-x64` on Ubuntu 20.04 unless the user already set it. | US-11 |

## 7. Non-functional requirements

### 7.1 Accuracy and repeatability (NFR-01 – NFR-06)

- **NFR-01** Warm-up iterations and failed iterations are excluded from statistics.
- **NFR-02** Pages are measured sequentially; API load runs after all page measurement, so load never distorts page metrics.
- **NFR-03** Cold-cache mode uses a fresh browser context per iteration so every iteration starts with an empty HTTP cache.
- **NFR-04** Percentiles use linear interpolation between closest ranks, which is well-defined for small sample sizes; CoV is reported so users can judge stability, and high variance raises a `HIGH_VARIANCE` finding.
- **NFR-05** Open-model latency is measured from the scheduled send time to avoid coordinated omission.
- **NFR-06** Every run records its measurement conditions (tool version, OS, CPU count, browser version, cache mode, throttling, device, load profile).

### 7.2 Safety (NFR-10 – NFR-15)

- **NFR-10** The crawler only performs GET navigations of `a[href]` links. It never submits forms or clicks buttons.
- **NFR-11** Links matching logout, signout or delete are excluded by default; users can add patterns.
- **NFR-12** The crawler stays on the target's origin by default.
- **NFR-13** Discovered endpoint load tests are **opt-in**, GET-only and same-origin only. PerfLens never load-tests third-party hosts it discovered.
- **NFR-14** Secrets are supplied only through environment-variable substitution in the config (`${ENV:NAME}`). There are no `PERFLENS_*` configuration variables; the only such variables are diagnostics/installer switches (`PERFLENS_DEBUG=1` for stack traces, `PERFLENS_PREFIX` for the installer).
- **NFR-15** Secret-looking query parameters (`token`, `access_token`, `api_key`, `key`, `secret`, `password`, `pwd`, `sig`, `signature`, `auth`) are redacted to `***` in report URLs. Header and cookie values are never written to outputs.

### 7.3 Performance overhead (NFR-20 – NFR-22)

- **NFR-20** Browser instrumentation is limited to PerformanceObservers and a single evaluation after settle. It does not use tracing or screenshots during measurement.
- **NFR-21** Screenshots are taken once per page, outside the measured window.
- **NFR-22** The load generator uses pooled connections (`SocketsHttpHandler`) and low-allocation recording so the client is not the bottleneck at the supported scale.

### 7.4 Portability (NFR-30 – NFR-32)

- **NFR-30** Runs on Linux (including Ubuntu 20.04 via override), WSL2, macOS and Windows with .NET 10.
- **NFR-31** Headless by default; `--headed` for debugging on desktops.
- **NFR-32** The installer is a POSIX shell script. On Windows, use `dotnet run`/`dotnet publish` directly.

### 7.5 Offline report (NFR-40)

- **NFR-40** `report.html` embeds Chart.js 4, styles, data and screenshots (up to 15 MB total; larger sets are linked from `screenshots/`), and makes no network requests.

### 7.6 Extensibility (NFR-50 – NFR-52)

- **NFR-50** Modules communicate through interfaces in `PerfLens.Core` (`IPageBenchmarkRunner`, `IApiLoadRunner`, `IAnalysisEngine`, `IReportWriter`, `IProgressReporter`).
- **NFR-51** `results.json` carries `schemaVersion` so readers can evolve.
- **NFR-52** Finding rules have stable IDs; new rules can be added without changing the result model.

### 7.7 Usability (NFR-60 – NFR-61)

- **NFR-60** Error messages name the property or argument and the fix.
- **NFR-61** A first report from `perflens quick` takes minutes on a typical site.

## 8. Metrics definitions (summary)

Full formulas are in [DETAILS.md § Metric definitions](DETAILS.md#10-metric-definitions).

| Metric | Key | Definition (summary) |
|---|---|---|
| TTFB | `ttfb_ms` | `responseStart` of the navigation entry (from navigation start) |
| Server | `server_ms` | `responseStart − requestStart` |
| DNS / Connect / TLS | `dns_ms` / `connect_ms` / `tls_ms` | Domain lookup, TCP connect, TLS handshake durations |
| Download | `download_ms` | `responseEnd − responseStart` for the document |
| DOM interactive / DCL / Load | `dom_interactive_ms`, `dom_content_loaded_ms`, `load_ms` | Navigation Timing milestones |
| FCP | `fcp_ms` | `first-contentful-paint` paint entry |
| LCP | `lcp_ms` | Last `largest-contentful-paint` entry before collection |
| CLS | `cls` | Largest session window of layout shifts without recent input |
| TBT | `tbt_ms` | Σ max(0, long-task duration − 50 ms) for tasks after FCP |
| Long tasks | `long_tasks` | Count of tasks > 50 ms |
| Requests / weight | `requests`, `transfer_kb`, `decoded_kb` | All resources including the document |
| Failed requests | `failed_requests` | Resources with status ≥ 400 or network failure |
| DOM nodes / JS heap | `dom_nodes`, `js_heap_mb` | Element count; `performance.memory.usedJSHeapSize` (Chromium) |
| Console errors | `console_errors` | Console `error` messages + uncaught page errors |
| API latency | — | Request start (or intended start in open model) to response end |

## 9. Success metrics for the product

| Metric | Target (first 12 months) |
|---|---|
| Time from clone to first report (`install.sh` + `perflens quick`) | < 10 minutes on a clean Ubuntu/WSL machine |
| Repeatability: median LCP CoV on a stable staging app, 10 iterations, cold cache | < 10% |
| Report opens offline in Chrome, Edge, Firefox and Safari | 100% |
| Documented config options covered by `configs/template.yaml` | 100% |
| Tests passing in CI (unit, browser, E2E against DemoShop) | 100% on `main` |
| GitHub adoption signals | Stars, forks, external issues/PRs; at least 3 external teams using it in CI |
| Findings precision (share of findings users consider valid) | > 80% in user feedback |

## 10. Release plan and roadmap

### v1.0 (current)

Everything in §6: configured + crawled pages, auth (headers/login/storage state), real-browser metrics with cold/warm cache, device/network/CPU emulation, closed/open API load, discovered endpoints, analysis with scores/findings/thresholds/baseline, HTML/JSON/Markdown/CSV reports, CLI commands, installer, DemoShop and tests.

### Candidate later releases

| Theme | Description |
|---|---|
| Lighthouse integration | Optional Lighthouse run per page (Chromium) to add its audits and opportunities alongside PerfLens statistics |
| INP via scripted interactions | Measure Interaction to Next Paint using configured interactions (click/type) with Event Timing |
| User journeys / flows | Multi-step flows (search → product → cart) measured as a sequence with per-step metrics |
| Distributed load agents | Coordinate several load agents for higher throughput and multi-region latency |
| OpenTelemetry export | Export metrics/spans to OTLP so results appear in existing observability stacks and can be correlated with server traces |
| Trend dashboard | Store many `results.json` files and chart trends across runs/commits |
| LLM narrative summaries | Optional natural-language explanation of findings, grounded in `results.json` |
| Additional reporters | JUnit XML for CI test tabs, SARIF for code-scanning style annotations |

## 11. Risks and mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Measurement noise (shared CI runners, VPNs, background load) | False regressions, flaky gates | Warm-up, multiple iterations, p75 default, CoV + `HIGH_VARIANCE` finding, tolerance + absolute guard for regressions, guidance on dedicated runners |
| Crawler triggers side effects via GET links (e.g. `/cart/remove?id=1`) | Data changes in the target | GET-only, default exclude patterns, `include`/`exclude` regexes, docs advise staging and reviewing the crawl list |
| Load test harms a production system | Outage | Load is opt-in (`api.enabled`), discovered endpoints opt-in and GET-only, explicit duration/rate caps, docs warn about production |
| Secrets leak into reports | Credential exposure | Env substitution only, header/cookie values not serialised, URL secret redaction |
| Login flows with MFA/CAPTCHA/SSO | Cannot authenticate | `StorageState` auth (capture once manually), header/token auth, cookies |
| Browser version changes shift metrics | Baseline mismatch | Browser version recorded in results; compare only runs with the same environment |
| Playwright host support (e.g. Ubuntu 20.04) | Browser install fails | Automatic host override, `--with-deps`, `doctor` |
| Metric differences between engines | Confusing comparisons between engines | Chromium is the default; docs state which metrics are Chromium-only (CLS, long tasks/TBT, JS heap, throttling; LCP also in recent Firefox); unsupported CLS/TBT are reported as missing (not 0) and excluded from scores; throttling on other engines logs a warning |
| XSS through page content in the report | Report compromises viewer | All untrusted strings HTML-encoded; data embedded as escaped JSON; tests cover injection |

## 12. Open questions

1. Should warm-cache mode also prime service workers, or disable them for consistency?
2. Should crawled pages get per-pattern thresholds (e.g. regex-matched page groups)?
3. Should the API score weight be configurable instead of the fixed 70/30 split?
4. What is the best default baseline strategy in CI: last green `main` run, or a pinned release run?
5. Should PerfLens support Soft Navigations for SPA route changes once browsers stabilise the API?
6. Should `quick` persist the generated config so users can graduate to `run`?

## 13. Glossary

| Term | Meaning |
|---|---|
| **Black-box** | Measuring from outside, using only URLs and credentials, without source or instrumentation |
| **Iteration** | One measured page load |
| **Warm-up** | An unmeasured load that primes DNS, connections and server caches |
| **Cold / warm cache** | Fresh browser context per iteration / one context reused for a page's iterations |
| **Core Web Vitals** | Google's user-centric metrics; PerfLens reports LCP and CLS, plus FCP, TBT and TTFB as lab proxies (INP needs interactions — roadmap) |
| **FCP / LCP** | First / Largest Contentful Paint |
| **CLS** | Cumulative Layout Shift |
| **TBT** | Total Blocking Time; lab proxy for interactivity |
| **TTFB** | Time To First Byte |
| **p75** | 75th percentile; the Core Web Vitals assessment statistic |
| **CoV** | Coefficient of variation = standard deviation / mean |
| **Closed / open model** | Load with fixed concurrency / fixed arrival rate |
| **Coordinated omission** | Under-reporting latency because a slow server also slows the client's sending schedule |
| **Virtual user (VU)** | One concurrent request loop in the closed model |
| **Baseline** | A previous `results.json` used for regression comparison |
| **Finding** | A rule-based observation with severity and recommendation |
| **Storage state** | Playwright's serialised cookies and local storage for an authenticated session |
| **CDP** | Chrome DevTools Protocol |
