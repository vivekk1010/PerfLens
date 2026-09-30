# PerfLens — Technical Details & User Guide

This is the complete reference for installing, configuring and running PerfLens, and for interpreting its results. For the product rationale see the [PRD](PRD.md); for internals see [ARCHITECTURE.md](ARCHITECTURE.md).

## Contents

1. [Installation](#1-installation)
2. [Quick start with DemoShop](#2-quick-start-with-demoshop)
3. [Command-line reference](#3-command-line-reference)
4. [Configuration reference](#4-configuration-reference)
5. [Authentication recipes](#5-authentication-recipes)
6. [Crawl tuning](#6-crawl-tuning)
7. [Device, network and CPU emulation](#7-device-network-and-cpu-emulation)
8. [Baseline workflow](#8-baseline-workflow)
9. [API load profiles](#9-api-load-profiles)
10. [Metric definitions](#10-metric-definitions)
11. [Statistics](#11-statistics)
12. [Scoring](#12-scoring)
13. [Findings catalog](#13-findings-catalog)
14. [Thresholds and CI gating](#14-thresholds-and-ci-gating)
15. [Output files reference](#15-output-files-reference)
16. [Reading the HTML report](#16-reading-the-html-report)
17. [Troubleshooting](#17-troubleshooting)
18. [Accuracy tips](#18-accuracy-tips)
19. [Security considerations](#19-security-considerations)
20. [FAQ](#20-faq)

---

## 1. Installation

### 1.1 Script (Linux, WSL2, macOS)

```bash
git clone https://github.com/vivekk1010/PerfLens.git
cd PerfLens
./scripts/install.sh              # add --with-deps on a fresh Linux machine
perflens doctor
```

What `install.sh` does:

1. Uses an existing .NET 10 SDK if found; otherwise installs it to `~/.dotnet` with Microsoft's `dotnet-install.sh`.
2. Publishes `src/PerfLens.Cli` in Release mode to `~/.local/lib/perflens`.
3. Writes a wrapper at `~/.local/bin/perflens` (make sure `~/.local/bin` is on your `PATH`).
4. Installs Playwright browsers (Chromium by default) into the Playwright cache (`~/.cache/ms-playwright` on Linux).
5. On Ubuntu 20.04, the installer and the generated wrapper export `PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu22.04-x64` (see [§17.1](#171-ubuntu-2004)).

Re-running the script is safe; it updates the existing installation.

| Option | Effect |
|---|---|
| `--prefix DIR` | Install root instead of `~/.local` (CLI in `DIR/lib/perflens`, wrapper `DIR/bin/perflens`). Default can also come from `PERFLENS_PREFIX` |
| `--browsers LIST` | `chromium`, `firefox`, `webkit`, `all`, or a comma-separated list (default `chromium`) |
| `--with-deps` | Also install the OS libraries browsers need (uses `sudo`) |
| `--skip-browsers` | Do not install Playwright browsers |
| `--skip-build` | Do not publish the CLI (reuse an existing install) |
| `--update-profile` | Append `PATH` / `DOTNET_ROOT` settings to your shell profile |
| `--uninstall` | Remove the wrapper, published CLI and profile block (the SDK and browser cache are left in place) |
| `--dry-run` | Print the actions without changing anything |
| `-h`, `--help` | Show help |

`NO_COLOR` disables coloured installer output.

### 1.2 Manual install (any OS, including Windows)

```bash
# Prerequisite: .NET 10 SDK  (https://dotnet.microsoft.com/download)
dotnet publish src/PerfLens.Cli -c Release -o ./out
./out/perflens install-browsers chromium --with-deps     # Windows: .\out\perflens.exe ...
./out/perflens doctor
```

Or run from source without publishing:

```bash
dotnet run --project src/PerfLens.Cli -- run -c configs/demoshop.yaml
```

### 1.3 Browsers

```bash
perflens install-browsers              # chromium
perflens install-browsers all          # chromium, firefox, webkit
perflens install-browsers chromium --with-deps
```

Chromium gives the complete metric set. Firefox and WebKit are useful for cross-engine comparisons but lack CLS/long-task APIs and throttling (and WebKit lacks LCP) (see [§10.7](#107-engine-support)).

### 1.4 `perflens doctor`

Prints the .NET runtime and OS, shows the Playwright host override if one is active, launches headless Chromium and runs a script in a page, and checks that the temp directory is writable. Each failed check prints a fix (e.g. `perflens install-browsers chromium`, plus `--with-deps` for Linux libraries). Exits 0 when all checks pass, 3 otherwise.

## 2. Quick start with DemoShop

DemoShop (`samples/PerfLens.DemoShop`) is a small ASP.NET Core app with deliberate problems, used for demos and end-to-end tests:

| Route | Deliberate issue | Expect |
|---|---|---|
| `/` | Baseline, reasonably fast | Good vitals |
| `/slow` | Server delay before responding | `PAGE_SLOW_SERVER`, poor TTFB |
| `/heavy` | Large uncompressed images and JavaScript, layout shift | `PAGE_HEAVY`, `LARGE_IMAGE`, `LARGE_SCRIPT`, `UNCOMPRESSED_TEXT`, CLS finding |
| `/jank` | Long main-thread tasks | High TBT, `VITAL_POOR_TBT` / `VITAL_NI_TBT` |
| `/errors` | Console errors, a 404 resource, a flaky API call | `CONSOLE_ERRORS`, `FAILED_REQUESTS`, `API_CALL_FAILURES` |
| `/login` → `/dashboard` | Login-protected area | Measured after scripted login |
| `/missing-page` | Linked but does not exist | `BROKEN_LINK` (with crawl) |
| `/api/slow`, `/api/flaky`, other `/api/...` | Slow and intermittently failing APIs | `API_SLOW_P95`, `API_ERROR_RATE` |

Run everything with one script:

```bash
./scripts/run-demo.sh                   # builds + starts DemoShop, waits for /health, runs configs/demoshop.yaml, stops DemoShop
./scripts/run-demo.sh --quick           # fast smoke test using configs/demoshop-quick.json
./scripts/run-demo.sh -- --iterations 5 --label nightly --open   # extra args go to `perflens run`
```

`run-demo.sh` options: `--quick`, `-c|--config FILE`, `-p|--port PORT` (default `$DEMOSHOP_PORT` or 5080; the next free port is used if taken), `--no-build`. It uses `perflens` from `PATH` if installed, otherwise `dotnet run --project src/PerfLens.Cli`. Reports go to `perflens-results/demoshop/` (or `perflens-results/demoshop-quick/`).

Or step by step:

```bash
./scripts/start-demoshop.sh             # foreground on http://127.0.0.1:5080 (Ctrl+C to stop)
                                        # options: -p|--port, --host, --release, --no-build
# in another terminal:
perflens run -c configs/demoshop.yaml --open    # --open opens report.html when done
```

The demo configs read `DEMOSHOP_URL` (default `http://127.0.0.1:5080`) and the demo login password from `DEMOSHOP_PASSWORD`, which DemoShop reads too (both have demo defaults).

The DemoShop run is expected to finish with **exit code 1**: its thresholds are deliberately breached.

Your own application:

```bash
perflens quick https://staging.example.com --pages /,/pricing,/docs --iterations 5
perflens quick https://staging.example.com --crawl --max-pages 30
perflens init perflens.yaml       # then edit and:
perflens validate -c perflens.yaml
perflens run -c perflens.yaml
```

## 3. Command-line reference

```text
perflens run -c|--config <file> [--url <baseUrl>] [-o|--output <dir>] [--baseline <results.json>] [--label <text>]
             [--iterations <n>] [--headed] [--no-api] [--no-crawl] [--formats html,json,md,csv] [--open] [-v|--verbose]
perflens quick <url> [--pages /,/about] [--crawl] [--max-pages <n>] [--iterations <n>] [--api <path>]... [-o <dir>] [--headed] [-v]
perflens init [path] [--force]
perflens validate -c <file>
perflens report <results.json> [-o <dir>] [--formats ...]
perflens install-browsers [chromium|firefox|webkit|all] [--with-deps]
perflens doctor
perflens --version | --help
```

### 3.1 `run` options

| Option | Overrides | Description |
|---|---|---|
| `-c`, `--config <file>` | — | Required. YAML (`.yml`/`.yaml`) or JSON config |
| `--url <baseUrl>` | `target.baseUrl` | Point the same config at another environment |
| `-o`, `--output <dir>` | `output.directory` | Exact output directory: files are written directly into it, **without** a timestamped subfolder |
| `--baseline <file>` | `output.baselinePath` | Previous `results.json` to compare against |
| `--label <text>` | `output.runLabel` | Free-text label shown in reports (e.g. commit SHA, branch) |
| `--iterations <n>` | `browser.iterations` | Measured iterations per page (per-page `iterations` still wins) |
| `--headed` | `browser.headless: false` | Show the browser window (debugging) |
| `--no-api` | `api.enabled: false` | Skip API load tests |
| `--no-crawl` | `crawl.enabled: false` | Skip crawling |
| `--formats <list>` | `output.formats` | Comma-separated subset of `html,json,md,csv` |
| `--open` | — | Open `report.html` in the default browser afterwards |
| `-v`, `--verbose` | — | Show debug-level progress |

### 3.2 `quick`

Builds a configuration in memory with default thresholds:

- `target.baseUrl = <url>`; pages from `--pages` (comma-separated paths). With neither `--pages` nor `--crawl`, the URL itself is the only page (`quick https://app/pricing` measures `/pricing` on that origin).
- `--crawl` enables the crawl; `--max-pages <n>` sets `crawl.maxPages`.
- `--iterations <n>` (default **3** for `quick`).
- Each `--api <path>` (repeatable) adds a GET endpoint and enables API load with a light profile: closed model, 5 virtual users, 10 s + 2 s warm-up.
- Also accepts `-o`, `--formats`, `--label`, `--headed`, `--open`, `-v`.

Use `init` to graduate to a config file.

### 3.3 Other commands

| Command | Description |
|---|---|
| `init [path] [--force]` | Write a commented starter config (default `perflens.yaml`); refuses to overwrite without `--force` |
| `validate -c <file>` | Load, substitute env vars and validate; print resolved summary (pages, crawl, auth type, browser settings, endpoints). Secrets are not printed |
| `report <results.json> [-o <dir>] [--formats] [--open]` | Re-render reports from saved results |
| `install-browsers [engine] [--with-deps]` | Install Playwright browsers; `--with-deps` installs OS libraries |
| `doctor` | Environment self-check |

`perflens <command> --help` prints usage. Set `PERFLENS_DEBUG=1` to print full stack traces for unexpected errors.

### 3.4 Exit codes

| Code | Meaning |
|---|---|
| `0` | Success: thresholds passed, or `thresholds.failOnThresholdBreach` is false |
| `1` | A threshold failed or the score is below `thresholds.minScore`, and `failOnThresholdBreach` is true |
| `2` | Invalid configuration or command-line arguments (including unknown options and missing environment variables) |
| `3` | Unexpected runtime error (e.g. output cannot be written); also returned by `doctor` and `install-browsers` when they fail |
| `130` | Cancelled with Ctrl+C |

If the browser cannot be launched (e.g. not installed), `run` does not abort: it records a warning and marks every page unavailable, so the availability thresholds fail and the exit code is normally `1`. Run `perflens doctor` to diagnose.

## 4. Configuration reference

### 4.1 File format rules

- **Format by extension:** `.yml`/`.yaml` → YAML, anything else → JSON. JSON may contain `//` and `/* */` comments and trailing commas.
- **Property names** are case-insensitive; this guide uses camelCase (`target.baseUrl`).
- **Enum values** are names, case-insensitive (`chromium`, `Cold`, `open`, `Fast3G`).
- **Paths vs URLs:** page paths, crawl start paths, login URL and endpoint URLs may be relative to `target.baseUrl` (`/products`) or absolute (`https://...`).
- **Environment variables:** `${ENV:NAME}`, `${NAME}` or `${NAME:-default}` anywhere in the file. If any referenced variable is unset and has no default, loading fails and lists all missing names. Values are JSON-escaped, so quotes and backslashes in secrets are safe.
  - **YAML:** substitution happens per scalar *before* typing. An unquoted placeholder is typed after substitution, so `iterations: ${ENV:N:-5}` becomes a number; a quoted scalar (`"${ENV:PIN}"`) always stays a string. Placeholders in YAML comments are ignored.
  - **JSON:** substitution happens on the raw text. Put placeholders inside quotes for strings (`"${ENV:TOKEN}"`) and unquoted for numbers/booleans (`"iterations": ${N:-5}`). Placeholders inside JSON comments are substituted too, so an unset variable mentioned in a comment still fails the load.
- **YAML scalar typing:** unquoted `true`/`false` become booleans, numbers become numbers (except values with a leading zero such as `007`, kept as text), and empty, `~` or `null` become null. **String properties accept numbers/booleans** (e.g. `value: 1234` for a PIN) — they are converted to text.
- **Validation** collects every error and reports them together:

```text
Invalid configuration:
  - target.baseUrl is required.
  - api.profile.durationSeconds must be >= 1.
```

### 4.2 Root

| Property | Type | Default | Description |
|---|---|---|---|
| `name` | string | `"PerfLens run"` | Run name shown in reports |
| `description` | string? | — | Free text shown in reports |
| `target` | object | — | [§4.3](#43-target) Application under test (required: `baseUrl`) |
| `auth` | object | type None | [§4.4](#44-auth) |
| `pages` | list | `[]` | [§4.5](#45-pages) Pages to measure |
| `crawl` | object | disabled | [§4.6](#46-crawl) |
| `browser` | object | Chromium, headless, 5 iterations | [§4.7](#47-browser) |
| `api` | object | disabled | [§4.8](#48-api) |
| `thresholds` | object | see §4.9 | [§4.9](#49-thresholds) |
| `output` | object | see §4.10 | [§4.10](#410-output) |

At least one of `pages` (non-empty), `crawl.enabled: true` or `api.enabled: true` is required. If only `api` is enabled (API-only run), browser settings are not validated and no browser is launched.

### 4.3 `target`

| Property | Type | Default | Description |
|---|---|---|---|
| `target.baseUrl` | string | `""` (**required**) | Absolute `http`/`https` URL of the app, e.g. `https://staging.example.com` |
| `target.headers` | map string→string | `{}` | Extra HTTP headers sent with every browser request and API load request |
| `target.cookies` | list of cookie | `[]` | Cookies added to browser contexts and matching API requests |
| `target.cookies[].name` | string | `""` | Cookie name |
| `target.cookies[].value` | string | `""` | Cookie value (supports `${ENV:...}`) |
| `target.cookies[].domain` | string? | host of `baseUrl` | Cookie domain; subdomains match |
| `target.cookies[].path` | string | `"/"` | Cookie path prefix |
| `target.ignoreHttpsErrors` | bool | `false` | Accept invalid/self-signed TLS certificates (browser and load) |
| `target.userAgent` | string? | browser default | Override the User-Agent for browser and load requests; takes precedence over a `browser.device` descriptor's UA |

### 4.4 `auth`

| Property | Type | Default | Description |
|---|---|---|---|
| `auth.type` | enum `None` \| `Headers` \| `Login` \| `StorageState` | `None` | Authentication method |
| `auth.headers` | map string→string | `{}` | For `Headers`: e.g. `Authorization: Bearer ${ENV:TOKEN}`. Required when type is `Headers` |
| `auth.login` | object? | — | For `Login`: scripted UI login, performed once before measuring. `steps` required |
| `auth.login.url` | string | `"/login"` | Login page, relative or absolute |
| `auth.login.steps` | list of step | `[]` | Ordered actions (below) |
| `auth.login.steps[].action` | enum `Fill` \| `Click` \| `Press` \| `WaitForSelector` \| `WaitForUrl` \| `Wait` | — | Step type |
| `auth.login.steps[].selector` | string? | — | CSS/Playwright selector for `Fill`, `Click`, `Press`, `WaitForSelector` |
| `auth.login.steps[].value` | string? | — | `Fill`: text; `Press`: key (e.g. `Enter`); `WaitForUrl`: URL regex; `Wait`: milliseconds. Supports `${ENV:...}` |
| `auth.login.successUrlPattern` | string? | — | Regex the URL must match after the steps |
| `auth.login.successSelector` | string? | — | Selector that must be visible after the steps |
| `auth.login.timeoutMs` | int | `30000` | Timeout for each login step / success check |
| `auth.storageStatePath` | string? | — | For `StorageState`: path to a Playwright storage state JSON. Required when type is `StorageState` |
| `auth.shareCookiesWithApi` | bool | `true` | Send the browser session's cookies with same-origin API load requests |

### 4.5 `pages`

| Property | Type | Default | Description |
|---|---|---|---|
| `pages[].name` | string | the `path` | Display name; must be unique (case-insensitive). Used to match baselines and per-page thresholds |
| `pages[].path` | string | `"/"` | Path relative to `baseUrl` or absolute URL. Required (non-empty) |
| `pages[].waitForSelector` | string? | — | Selector that must be visible before the page counts as ready (SPAs) |
| `pages[].waitUntil` | enum? `Load` \| `DomContentLoaded` \| `NetworkIdle` \| `Commit` | `browser.waitUntil` | Navigation ready event for this page |
| `pages[].iterations` | int? | `browser.iterations` | Measured iterations for this page (≥ 1) |
| `pages[].thresholds` | page thresholds? | — | Per-page overrides; any property left null falls back to `thresholds.page` ([§4.9](#49-thresholds)) |

### 4.6 `crawl`

| Property | Type | Default | Description |
|---|---|---|---|
| `crawl.enabled` | bool | `false` | Discover pages by following links |
| `crawl.startPaths` | list string | `["/"]` | Seeds, relative to `baseUrl` |
| `crawl.maxDepth` | int | `2` | Link depth from seeds (0 = seeds only). ≥ 0 |
| `crawl.maxPages` | int | `20` | Maximum pages visited by the crawl. ≥ 1 |
| `crawl.include` | list regex | `[]` | If non-empty, only URLs matching at least one pattern are kept |
| `crawl.exclude` | list regex | `["(?i)logout", "(?i)signout", "(?i)delete"]` | URLs matching any pattern are skipped. **Setting this list replaces the defaults** — re-add them |
| `crawl.sameOriginOnly` | bool | `true` | Only follow links with the same scheme, host and port as `baseUrl` |
| `crawl.ignoreQueryString` | bool | `true` | Treat `/p?a=1` and `/p?a=2` as the same page |
| `crawl.ignoreFragments` | bool | `true` | Treat `/p#x` and `/p` as the same page |

Regexes are .NET regular expressions matched against the absolute URL; invalid patterns are validation errors.

### 4.7 `browser`

| Property | Type | Default | Description |
|---|---|---|---|
| `browser.engine` | enum `Chromium` \| `Firefox` \| `Webkit` | `Chromium` | Browser engine |
| `browser.headless` | bool | `true` | Headless mode (`--headed` sets false) |
| `browser.iterations` | int | `5` | Measured iterations per page (≥ 1) |
| `browser.warmupIterations` | int | `1` | Unmeasured loads per page before measuring (≥ 0). Recorded with `isWarmup: true` |
| `browser.cacheMode` | enum `Cold` \| `Warm` | `Cold` | `Cold`: new browser context per iteration (empty cache). `Warm`: one context per page |
| `browser.viewportWidth` | int | `1366` | Viewport width in CSS px |
| `browser.viewportHeight` | int | `768` | Viewport height in CSS px |
| `browser.device` | string? | — | Playwright device name (e.g. `"Pixel 7"`, `"iPhone 13"`); overrides viewport, UA, scale, touch |
| `browser.networkProfile` | enum `None` \| `Fast3G` \| `Slow4G` \| `Regular4G` \| `Custom` | `None` | Network throttling (Chromium) |
| `browser.customNetwork` | object? | — | Required when `networkProfile: Custom` |
| `browser.customNetwork.latencyMs` | double | `150` | Added round-trip latency |
| `browser.customNetwork.downloadKbps` | double | `1600` | Download throughput (kilobits/s) |
| `browser.customNetwork.uploadKbps` | double | `750` | Upload throughput (kilobits/s) |
| `browser.cpuThrottlingRate` | double | `1` | CPU slowdown factor (≥ 1; 4 ≈ mid-tier mobile). Chromium only |
| `browser.navigationTimeoutMs` | int | `30000` | Timeout for navigation and ready condition |
| `browser.settleMs` | int | `1500` | Wait after ready so LCP, CLS and long tasks settle |
| `browser.waitUntil` | enum `Load` \| `DomContentLoaded` \| `NetworkIdle` \| `Commit` | `Load` | Default ready event |
| `browser.captureScreenshots` | bool | `true` | One screenshot per page into `screenshots/` |
| `browser.captureApiCalls` | bool | `true` | Record XHR/fetch calls (needed for `api.useDiscoveredEndpoints`) |
| `browser.topResources` | int | `15` | Largest resources kept per iteration |

### 4.8 `api`

| Property | Type | Default | Description |
|---|---|---|---|
| `api.enabled` | bool | `false` | Run API load tests |
| `api.endpoints` | list endpoint | `[]` | Endpoints to load-test (below) |
| `api.useDiscoveredEndpoints` | bool | `false` | Also test same-origin, successful GET XHR/fetch calls seen during page runs. Requires pages or crawl |
| `api.maxDiscoveredEndpoints` | int | `10` | Cap on discovered endpoints |
| `api.profile` | load profile | see below | Default load profile for all endpoints |

When `api.enabled` is true, either `endpoints` must be non-empty or `useDiscoveredEndpoints` must be true.

**Endpoint** (`api.endpoints[]`):

| Property | Type | Default | Description |
|---|---|---|---|
| `name` | string | `"METHOD url"` | Display name; matches per-endpoint thresholds and baselines |
| `url` | string | `""` (**required**) | Relative to `baseUrl` or absolute |
| `method` | string | `"GET"` | HTTP method |
| `headers` | map | `{}` | Endpoint headers (override target/auth headers with the same name) |
| `body` | string? | — | Request body (for POST/PUT/PATCH) |
| `contentType` | string | `"application/json"` | Content-Type of `body` |
| `expectedStatus` | list int | `[]` | Status codes counted as success; empty = any 2xx/3xx |
| `profile` | load profile? | `api.profile` | Per-endpoint override (whole object) |
| `thresholds` | API thresholds? | — | Per-endpoint overrides; null properties fall back to `thresholds.api` |

**Load profile** (`api.profile`, `api.endpoints[].profile`):

| Property | Type | Default | Description |
|---|---|---|---|
| `mode` | enum `Closed` \| `Open` | `Closed` | Closed: fixed concurrent virtual users. Open: fixed arrival rate |
| `concurrency` | int | `10` | Virtual users (closed; ≥ 1) |
| `ratePerSecond` | double | `20` | Requests per second (open; > 0) |
| `durationSeconds` | int | `20` | Measured duration (≥ 1) |
| `warmupSeconds` | int | `3` | Unrecorded warm-up before measuring (≥ 0) |
| `timeoutMs` | int | `10000` | Per-request timeout (≥ 1) |
| `maxRequests` | int? | — | Optional hard cap on measured requests |

### 4.9 `thresholds`

| Property | Type | Default | Description |
|---|---|---|---|
| `thresholds.pageStatistic` | string | `"p75"` | Statistic for page thresholds, ratings and page score: `min`, `max`, `mean` (`avg`), `median` (`p50`), `p75`, `p90`, `p95`, `p99` |
| `thresholds.page` | page thresholds | below | Global page limits |
| `thresholds.api` | API thresholds | below | Global API limits |
| `thresholds.failOnThresholdBreach` | bool | `true` | Exit code 1 when any threshold fails or `minScore` is not met |
| `thresholds.minScore` | double? | — | Minimum overall score (0–100); not enforced when null |

**Page thresholds** (`thresholds.page`, `pages[].thresholds`). Set a property to `null` to disable it.

| Property | Type | Default | Compared metric |
|---|---|---|---|
| `ttfbMs` | double? | `800` | `ttfb_ms` |
| `fcpMs` | double? | `1800` | `fcp_ms` |
| `lcpMs` | double? | `2500` | `lcp_ms` |
| `cls` | double? | `0.1` | `cls` |
| `tbtMs` | double? | `200` | `tbt_ms` |
| `loadMs` | double? | `4000` | `load_ms` |
| `pageWeightKb` | double? | `3000` | `transfer_kb` |
| `requestCount` | double? | `120` | `requests` |
| `maxConsoleErrors` | int? | `0` | `console_errors` (console errors + uncaught page errors) |

**API thresholds** (`thresholds.api`, `api.endpoints[].thresholds`):

| Property | Type | Default | Compared value |
|---|---|---|---|
| `p50Ms` | double? | — | Latency p50 ≤ limit |
| `p95Ms` | double? | `800` | Latency p95 ≤ limit (also the API score's latency reference) |
| `p99Ms` | double? | `1500` | Latency p99 ≤ limit |
| `errorRatePct` | double? | `1` | Error rate % ≤ limit |
| `minRps` | double? | — | Throughput ≥ limit |

Note: in `pages[].thresholds` and `api.endpoints[].thresholds`, properties you omit fall back to the global value; properties with defaults in the table only apply at the global level.

### 4.10 `output`

| Property | Type | Default | Description |
|---|---|---|---|
| `output.directory` | string | `"perflens-results"` | Output root |
| `output.formats` | list string | `["html","json","md","csv"]` | Any of `html`, `json`, `md`, `csv` |
| `output.timestampedSubfolder` | bool | `true` | Write into `<directory>/<yyyyMMdd-HHmmss>/`; `false` writes into `directory` itself. Ignored when `-o` is given (always exact) |
| `output.baselinePath` | string? | — | Baseline `results.json` for regression comparison |
| `output.regressionTolerancePct` | double | `10` | Relative change (%) required before a change counts as a regression |
| `output.runLabel` | string? | — | Label shown in reports |

### 4.11 Complete example

```yaml
name: Shop staging
description: Nightly benchmark of the storefront
target:
  baseUrl: https://staging.shop.example.com
  headers:
    X-Perf-Test: perflens
  ignoreHttpsErrors: false
auth:
  type: Login
  login:
    url: /login
    steps:
      - { action: Fill, selector: "#email", value: "${ENV:SHOP_USER}" }
      - { action: Fill, selector: "#password", value: "${ENV:SHOP_PASSWORD}" }
      - { action: Click, selector: "button[type=submit]" }
    successUrlPattern: "/dashboard"
  shareCookiesWithApi: true
pages:
  - { name: Home, path: / }
  - { name: Search, path: "/search?q=shoes", waitForSelector: ".result-card" }
  - name: Dashboard
    path: /dashboard
    thresholds: { lcpMs: 3000, tbtMs: 300 }
crawl:
  enabled: true
  maxDepth: 2
  maxPages: 25
  exclude: ["(?i)logout", "(?i)signout", "(?i)delete", "/cart/(add|remove)"]
browser:
  engine: Chromium
  iterations: 7
  warmupIterations: 1
  cacheMode: Cold
api:
  enabled: true
  useDiscoveredEndpoints: true
  maxDiscoveredEndpoints: 5
  profile: { mode: Closed, concurrency: 10, durationSeconds: 30, warmupSeconds: 5 }
  endpoints:
    - { name: Products, url: /api/products }
    - name: Search API
      url: /api/search?q=shoes
      profile: { mode: Open, ratePerSecond: 50, durationSeconds: 30 }
      thresholds: { p95Ms: 400 }
thresholds:
  pageStatistic: p75
  minScore: 70
  failOnThresholdBreach: true
output:
  directory: perflens-results
  regressionTolerancePct: 10
```

`configs/template.yaml` documents every option inline; `configs/api-only.yaml` shows an API-only run.

## 5. Authentication recipes

### 5.1 Form login

```yaml
auth:
  type: Login
  login:
    url: /account/login
    steps:
      - { action: Fill, selector: "input[name=username]", value: "${ENV:APP_USER}" }
      - { action: Fill, selector: "input[name=password]", value: "${ENV:APP_PASSWORD}" }
      - { action: Press, selector: "input[name=password]", value: Enter }
      - { action: WaitForUrl, value: "/home" }
    successSelector: "[data-testid=user-menu]"
    timeoutMs: 30000
```

The login runs once, in its own context. Its storage state (cookies + local storage) is applied to every measured context, so cold-cache iterations stay logged in. Measurement starts after login, so login time is never part of page metrics.

### 5.2 Multi-step / SSO login

Add steps for each screen, using `WaitForSelector` between them:

```yaml
steps:
  - { action: Fill, selector: "#identifier", value: "${ENV:APP_USER}" }
  - { action: Click, selector: "#next" }
  - { action: WaitForSelector, selector: "#password" }
  - { action: Fill, selector: "#password", value: "${ENV:APP_PASSWORD}" }
  - { action: Click, selector: "#signin" }
  - { action: WaitForUrl, value: "https://app\\.example\\.com/.*" }
```

### 5.3 Bearer token / API key

```yaml
auth:
  type: Headers
  headers:
    Authorization: "Bearer ${ENV:APP_TOKEN}"
```

Headers are added to every browser request (including third-party requests the page makes — use a token scoped to the app) and every API load request.

### 5.4 MFA, CAPTCHA or complex SSO: storage state

Log in manually once and save the state with Playwright, then point PerfLens at the file:

```bash
npx playwright codegen --save-storage=auth.json https://app.example.com/login   # log in, then close
```

```yaml
auth:
  type: StorageState
  storageStatePath: ./auth.json
```

Storage state files contain live session cookies — treat them as secrets, keep them out of git, and refresh them when sessions expire.

### 5.5 Session cookie only

```yaml
target:
  cookies:
    - { name: session, value: "${ENV:SESSION_COOKIE}" }
```

### 5.6 Verifying login

Run with `--headed -v` to watch the login. If login fails (a step times out or the success check does not pass), PerfLens logs `Login failed at <step>: … Continuing unauthenticated.`, records it as a run warning, and measures the pages without a session — protected pages then usually show a `finalUrl` of the login page. A missing `storageStatePath` file behaves the same way. Always set `successUrlPattern` or `successSelector` so a failed login is detected rather than silently measuring the login page. Secret values are masked in login error messages.

## 6. Crawl tuning

- **Scope with `include`.** `include: ["/docs/"]` keeps the crawl in a section.
- **Protect with `exclude`.** Add every link that changes state when fetched: logout, delete, cart add/remove, unsubscribe, language/currency switches, "mark as read". Remember that setting `exclude` replaces the defaults.
- **Control size.** `maxPages` bounds cost: each page costs `(warmupIterations + iterations)` navigations plus one crawl visit. 20 pages × 6 loads × ~3 s ≈ 6 minutes.
- **Query strings.** With `ignoreQueryString: true` (default), `/product?id=1` and `/product?id=2` collapse to one page. Set it to `false` if query strings select genuinely different pages — then use `maxPages` and `include` to avoid thousands of variants.
- **Configured + crawled.** Configured pages keep their names and settings; the crawl adds other pages (named by path + query, e.g. `/products/3`; `source: crawled`; duplicates get ` (2)`), skipping URLs that are already configured.
- **What gets followed.** For each link: http(s) only → not a file download (e.g. `.pdf`, `.zip`, images, media, office documents, `.js`, `.css`, `.json`, fonts) → same origin (if `sameOriginOnly`) → not matching `exclude` → matching `include` (if set) → not seen before (canonical form) → within `maxDepth`. Start paths are always visited. Links are only extracted from HTML responses.
- **Crawl navigation** uses one browser context (authenticated) and `browser.waitUntil` (`NetworkIdle` is treated as `Load`); crawl visits are not measured.
- **SPAs.** The crawl reads `a[href]` after the page is ready; router links rendered as anchors are found. Links generated only on click are not — list them under `pages`.
- **Broken links** are URLs that returned status ≥ 400 or failed to load, listed with the page that linked to them. Links skipped because of `maxPages`/`maxDepth` are counted in `crawl.skippedLinks`.

## 7. Device, network and CPU emulation

| Goal | Settings |
|---|---|
| Desktop, unthrottled (default) | `viewportWidth: 1366`, `viewportHeight: 768` |
| Mobile, similar to Lighthouse mobile | `device: "Pixel 7"`, `networkProfile: Slow4G`, `cpuThrottlingRate: 4` |
| Mid-range mobile on 3G | `device: "Pixel 7"`, `networkProfile: Fast3G`, `cpuThrottlingRate: 4` |
| Specific link | `networkProfile: Custom`, `customNetwork: { latencyMs: 100, downloadKbps: 5000, uploadKbps: 1000 }` |

An unknown `device` name is reported as a warning and the configured viewport is used instead.

- `device` accepts any Playwright device descriptor name (`"iPhone 13"`, `"Pixel 7"`, `"Galaxy S9+"`, `"iPad Pro 11"`, …) and sets viewport, device scale factor, user agent, mobile and touch. It works on every engine (WebKit is the most faithful for iPhones).
- `networkProfile` and `cpuThrottlingRate` use the Chrome DevTools Protocol, so they apply only to Chromium; on other engines PerfLens warns and runs unthrottled. Presets:

  | Profile | Latency (RTT) | Download | Upload |
  |---|---|---|---|
  | `Fast3G` | 562.5 ms | 1474.56 kbps (1.44 Mbps) | 675 kbps |
  | `Slow4G` | 150 ms | 1638.4 kbps (1.6 Mbps) | 750 kbps |
  | `Regular4G` | 20 ms | 4096 kbps (4 Mbps) | 3072 kbps (3 Mbps) |
  | `Custom` | `customNetwork.latencyMs` | `customNetwork.downloadKbps` | `customNetwork.uploadKbps` |

  `Slow4G` matches Lighthouse's mobile network settings. Throttling is applied per page before navigation.
- Throttling multiplies variance; increase `iterations` (≥ 7) when throttled.
- Throttling adds on top of your real network: measuring over a slow VPN with `Fast3G` is slower than Fast3G.

## 8. Baseline workflow

1. Run on the reference version (e.g. `main`) and keep its `results.json`.
2. Run the candidate with `--baseline path/to/results.json` (or `output.baselinePath`).
3. The analysis matches pages and endpoints **by name** (case-insensitive) and records a `RegressionEntry` for each compared metric, with `deltaPct = (current − baseline) / |baseline| × 100`. A metric **regresses** when it moves in the bad direction by more than `output.regressionTolerancePct` (default 10%) **and** the absolute change exceeds a guard, so noise on tiny values (2 ms → 3 ms = +50%) is not flagged. **Improved** is the mirror condition.

   | Scope | Metrics (page ones at `thresholds.pageStatistic`) | Absolute guard | Worse when |
   |---|---|---|---|
   | page | `ttfb_ms`, `fcp_ms`, `lcp_ms`, `tbt_ms`, `load_ms` | 20 ms | higher |
   | page | `transfer_kb` | 20 KB | higher |
   | page | `requests` | 2 requests | higher |
   | page | `cls` | 0.02 | higher |
   | api | `p50_ms`, `p95_ms`, `p99_ms` | 20 ms | higher |
   | api | `error_rate_pct` | 0.5 percentage points | higher |
   | api | `rps` | 1 rps | lower |

   Metrics whose baseline value is 0 are skipped, as are endpoints that are unreachable in either run. Scores are not compared.
4. Each regression becomes a `REGRESSION` finding (High when |Δ| ≥ 2 × tolerance, else Medium) and appears in the report's baseline section and `summary.md`. If the baseline file cannot be read, the run continues without it and records a warning.

Regressions do not change the exit code on their own; gate with thresholds and `minScore`. Compare like with like: same machine type, browser version, config, cache mode and throttling. `results.json` records the environment so differences are visible.

## 9. API load profiles

### 9.1 Closed vs open

| Question you are asking | Use |
|---|---|
| "How fast does it respond with N simultaneous clients?" | `mode: Closed`, `concurrency: N` |
| "Can it sustain X requests per second, and what is latency at that rate?" | `mode: Open`, `ratePerSecond: X` |
| "What is the latency users see when traffic arrives independently?" | `Open` — it includes queueing (no coordinated omission) |
| Quick smoke check of an endpoint | `Closed`, `concurrency: 1–5`, `durationSeconds: 10` |

In the closed model throughput is an *output* (roughly concurrency ÷ latency). In the open model throughput is an *input*: if the server cannot keep up, latency grows because each request's latency is measured from its scheduled send time. The open model caps outstanding requests at 10,000; arrivals beyond that are counted under `errorTypes.dropped` (they are not part of `totalRequests` or the error rate).

In the closed model a virtual user that hits a connection or TLS error pauses 50 ms before its next request, so an unreachable server does not produce millions of instant failures.

### 9.2 Choosing numbers

- **Warm-up** 3–10 s to establish connections and warm server caches.
- **Duration** ≥ 20 s for stable p95; ≥ 60 s for p99 or to see degradation over time.
- **Concurrency / rate:** start from production peak (from analytics or APM), measure at 1×, then 2× for headroom. Do not exceed what the environment is sized for.
- **Timeouts:** keep `timeoutMs` above your SLO so slow responses are measured, not cut off.
- `maxRequests` caps cost on metered or rate-limited APIs.

### 9.3 Examples

```yaml
api:
  enabled: true
  profile: { mode: Closed, concurrency: 20, durationSeconds: 30, warmupSeconds: 5 }
  endpoints:
    - { name: Catalog, url: /api/products }
    - { name: Product, url: /api/products/42 }
    - name: Create order (staging only!)
      url: /api/orders
      method: POST
      body: '{"productId":42,"qty":1}'
      expectedStatus: [201]
      profile: { mode: Open, ratePerSecond: 5, durationSeconds: 20, maxRequests: 100 }
```

Endpoints run one after another, never simultaneously, and after all page measurement.

## 10. Metric definitions

All times are milliseconds relative to the navigation's `startTime` (0) of the `PerformanceNavigationTiming` entry, unless stated otherwise. Keys are those used in `results.json`, CSVs and `MetricNames`.

### 10.1 Navigation timing (`NavigationTiming`)

| Field / key | Formula |
|---|---|
| `redirectMs` | `redirectEnd − redirectStart` |
| `dnsMs` / `dns_ms` | `domainLookupEnd − domainLookupStart` |
| `connectMs` / `connect_ms` | `connectEnd − connectStart` (includes TLS) |
| `tlsMs` / `tls_ms` | `connectEnd − secureConnectionStart` when `secureConnectionStart > 0`, else 0 |
| `serverMs` / `server_ms` | `responseStart − requestStart` (server think time + one round trip) |
| `ttfbMs` / `ttfb_ms` | `responseStart` |
| `downloadMs` / `download_ms` | `responseEnd − responseStart` |
| `domInteractiveMs` / `dom_interactive_ms` | `domInteractive` |
| `domContentLoadedMs` / `dom_content_loaded_ms` | `domContentLoadedEventEnd` |
| `loadEventMs` / `load_ms` | `loadEventEnd` |
| `documentTransferBytes` | `transferSize` of the navigation entry |

On a reused connection (warm cache), DNS/connect/TLS are 0.

### 10.2 Vitals (`WebVitals`)

| Metric | Definition |
|---|---|
| FCP (`fcp_ms`) | `startTime` of the `first-contentful-paint` paint entry |
| LCP (`lcp_ms`) | `startTime` of the **last** `largest-contentful-paint` entry observed before collection (`renderTime` or `loadTime`). `lcpElement` describes the element (tag, id/class, image URL) |
| CLS (`cls`) | Layout-shift entries with `hadRecentInput = false` are grouped into session windows (next shift < 1 s after the previous, window ≤ 5 s). CLS = the largest window's sum of `value` |
| Long tasks (`long_tasks`) | Count of `longtask` entries (tasks > 50 ms) |
| TBT (`tbt_ms`) | Σ over long tasks starting at or after FCP of `max(0, duration − 50)`, up to the end of the settle window |

TBT here is measured until settle, not until Time to Interactive as in Lighthouse, so values are comparable across PerfLens runs but may differ slightly from Lighthouse.

### 10.3 Other page metrics

| Key | Definition |
|---|---|
| `wall_clock_ms` | Wall time of the navigation call until the ready condition (includes PerfLens/browser overhead; useful as a sanity check) |
| `requests` | All resources including the document |
| `transfer_kb` | Σ transfer bytes / 1024 (bytes over the network: headers + compressed body; 0 for cache hits) |
| `decoded_kb` | Σ decoded body bytes / 1024 (after decompression) |
| `failed_requests` | Resources with status ≥ 400 or a network failure |
| `dom_nodes` | `document.getElementsByTagName('*').length` after settle |
| `js_heap_mb` | `performance.memory.usedJSHeapSize` / 1024² (Chromium) |
| `console_errors` | Console messages of type `error` + uncaught page errors |

### 10.4 Resources

- **Transfer vs decoded.** `transferBytes` is what crossed the network; `decodedBytes` is what the browser processed. A large ratio decodedBytes ÷ transferBytes means good compression; roughly equal for text resources means no compression.
- **Sources.** Every request Playwright sees (including failures and iframe sub-resources) is matched by URL, in start order, to its Resource Timing entry; the main document uses the navigation entry. Resource Timing entries with no matching Playwright request are added too. Redirect hops are not counted separately.
- **Cached.** A resource counts in `cachedRequests` when Resource Timing reports `transferSize == 0` and `decodedBodySize > 0` (served from HTTP or memory cache). Analysis size checks fall back to decoded size when transfer size is 0.
- **Third-party.** Origin (scheme, host, port) differs from `target.baseUrl`.
- **Types.** `document`, `script`, `stylesheet`, `image`, `font`, `xhr`, `fetch`, `media`, `other` (from the Playwright resource type, then the Resource Timing initiator type, then the file extension).
- **Cross-origin caveat.** Without a `Timing-Allow-Origin` header, browsers report 0 sizes for cross-origin resources. PerfLens then uses the response's `Content-Length` (as transfer size, and as decoded size when the response is not encoded). Resources with neither remain 0.
- **Top resources.** The `browser.topResources` largest by transfer size (ties by duration), with status, sizes, duration, start, `content-encoding`, `cache-control` and `content-type`.

### 10.5 In-page API calls

XHR/fetch requests observed during the load: method, URL (secrets redacted), **normalized URL** (numeric, GUID and ≥ 24-hex-char path segments replaced by `{id}`, query removed — e.g. `/api/products/42?x=1` → `/api/products/{id}`), status, duration (request start → response end), bytes (encoded body size, else `Content-Length`), failed (network failure or status ≥ 400). Page summaries group calls by method + normalized URL.

### 10.6 API load metrics

| Metric | Definition |
|---|---|
| Latency | Closed: actual send → response body fully read. Open: **intended** send time → response body fully read. Includes failed requests |
| `totalRequests` | Measured requests sent during the measured phase (warm-up excluded) |
| `successfulRequests` | Responses whose status is in `expectedStatus` (or 2xx/3xx when empty). Redirects are not followed |
| `failedRequests` | `total − successful`: timeouts, connection/TLS errors, unexpected status, other errors |
| `errorRatePct` | failed ÷ total × 100 |
| `durationSeconds` | Configured `durationSeconds`, or the time until `maxRequests` completed |
| `rps` | total ÷ `durationSeconds` |
| `bytesReceived` | Response body bytes (after decompression) |
| `statusCodes` | Count per HTTP status code |
| `errorTypes` | Count per `timeout`, `connection_error`, `tls_error`, `unexpected_status`, `other`, plus `dropped` (open model only; not in totals) |
| Timeline | One point per second of the measured phase, grouped by **completion** time: requests, errors, p50, p95, max latency |
| Histogram | 20 equal-width buckets from min to p99.9; values above p99.9 fall in the last bucket, so counts sum to `totalRequests` |

### 10.7 Engine support

| Metric | Chromium | Firefox | WebKit |
|---|---|---|---|
| Navigation timing, FCP, resources, API calls, errors | ✔ | ✔ | ✔ |
| LCP | ✔ | recent versions | — |
| CLS (`layout-shift`), long tasks / TBT (`longtask`) | ✔ | reported as 0 | reported as 0 |
| JS heap | ✔ | — | — |
| Network / CPU throttling | ✔ | — (warning) | — (warning) |
| Device emulation | ✔ | ✔ | ✔ |

A metric whose API is missing entirely (LCP, JS heap) is recorded as missing, and the page score renormalizes its weights over the metrics present. **CLS and TBT, however, read as 0 on engines without `layout-shift` / `longtask` support**, which inflates their ratings and page scores. Use Chromium for vitals and scoring; use Firefox/WebKit for timing and weight comparisons.

## 11. Statistics

Per page and metric, over **measured** iterations only (warm-ups and failed iterations excluded):

| Statistic | Definition |
|---|---|
| `count` | Number of values |
| `min`, `max` | Extremes |
| `mean` | Arithmetic mean |
| `median` | 50th percentile |
| `p75`, `p90`, `p95`, `p99` | Percentiles, linear interpolation |
| `stdDev` | Sample standard deviation: √(Σ(x − mean)² / (n − 1)); 0 when n = 1 |
| `coV` | Coefficient of variation = stdDev ÷ mean (0 when mean = 0) |

**Percentile method** (linear interpolation between closest ranks, the same as Excel `PERCENTILE.INC` and NumPy's default):

```text
sorted ascending x[0..n-1]
rank = p / 100 × (n − 1)
lo = floor(rank), hi = ceil(rank)
P = x[lo] + (x[hi] − x[lo]) × (rank − lo)
```

Example: 5 LCP values `[1000, 1100, 1200, 1500, 3000]`, p75 → rank = 3 → 1500; p90 → rank = 3.6 → 1500 + 0.6 × 1500 = 2400.

With few iterations, high percentiles approach the maximum (with 5 values, p95 and p99 are interpolations between the two largest values). Use ≥ 10 iterations when you gate on p90/p95.

**Reading CoV:** < 0.1 stable; 0.1–0.25 moderate noise; > 0.25 noisy — add iterations or fix the environment. High CoV raises `HIGH_VARIANCE`.

## 12. Scoring

### 12.1 Page score (0–100)

Lighthouse-style: each metric (at `thresholds.pageStatistic`) is mapped to 0–1 by a log-normal curve with two control points — the value scoring 0.9 (p10) and the value scoring 0.5 (median):

```text
score(x) = ½ · erfc( ln(x / median) · erfc⁻¹(0.2) / −ln(p10 / median) ),  erfc⁻¹(0.2) ≈ 0.9062
```

| Metric | Scores 0.9 at | Scores 0.5 at | Weight |
|---|---|---|---|
| FCP | 934 ms | 1600 ms | 15 |
| LCP | 1200 ms | 2400 ms | 25 |
| TBT | 150 ms | 350 ms | 30 |
| CLS | 0.1 | 0.25 | 25 |
| TTFB | 400 ms | 1000 ms | 5 |

Page score = weighted mean over metrics present × 100 (weights renormalized when metrics are missing). Pages with no measured iteration score 0.

### 12.2 API score (0–100)

```text
score = 100 − min(60, errorRatePct × 6) − min(40, max(0, p95 / p95Limit − 1) × 40)
```

`p95Limit` = the endpoint's `thresholds.p95Ms` or global `thresholds.api.p95Ms`; without a limit there is no latency penalty. Unreachable endpoints score 0.

Examples: 0% errors and p95 at the limit → 100. 2% errors, p95 = 1.5 × limit → 100 − 12 − 20 = 68.

### 12.3 Overall score and grade

- Overall = 0.7 × mean(page scores) + 0.3 × mean(API scores); if only one side exists, that side's mean.
- Grade: **A** ≥ 90, **B** ≥ 75, **C** ≥ 60, **D** ≥ 40, **F** < 40.

### 12.4 Core Web Vitals ratings

At `thresholds.pageStatistic`: Good if value ≤ good limit; Poor if value > poor limit; otherwise Needs improvement.

| Metric | Good ≤ | Poor > |
|---|---|---|
| FCP | 1.8 s | 3.0 s |
| LCP | 2.5 s | 4.0 s |
| CLS | 0.1 | 0.25 |
| TBT | 200 ms | 600 ms |
| TTFB | 0.8 s | 1.8 s |

Ratings (bands) and thresholds (your limits) are separate: ratings drive `VITAL_*` findings and the vitals matrix; thresholds drive pass/fail.

## 13. Findings catalog

Each finding has `ruleId`, `severity` (Info, Low, Medium, High, Critical), `category` (Performance, Reliability, Network, BestPractice, Regression, Stability), `title`, `detail`, `target` (page name, endpoint name or URL), `recommendation`, and optionally `metric` and `value`. Findings are sorted by severity (highest first), then target and rule ID.

"stat" below means the configured `thresholds.pageStatistic` (default p75) over measured iterations. Page rules run only for pages that were measured successfully at least once. The size and count limits are built-in heuristics, independent of your thresholds.

### 13.1 Page rules

| RuleId | Category | Severity | Trigger | Recommendation (summary) |
|---|---|---|---|---|
| `PAGE_UNAVAILABLE` | Reliability | Critical | Page error, or no measured iteration succeeded (navigation error/timeout, main document status ≥ 400, missing ready selector) | Open the URL manually; check auth, ready selector, timeout and server errors |
| `PAGE_ITERATION_FAILURES` | Reliability | High | Some (not all) measured iterations failed | Investigate intermittent timeouts, 5xx and flaky selectors — often capacity problems |
| `VITAL_POOR_FCP`, `VITAL_POOR_LCP`, `VITAL_POOR_CLS`, `VITAL_POOR_TBT`, `VITAL_POOR_TTFB` | Performance | High | Vital rated Poor at stat ([§12.4](#124-core-web-vitals-ratings)) | Metric-specific: render-blocking resources (FCP); hero image/font priority, compressed images (LCP); reserve space for media/ads, font-display (CLS); split long tasks, defer JS (TBT); caching, CDN, no redirect chains (TTFB) |
| `VITAL_NI_FCP` … `VITAL_NI_TTFB` | Performance | Medium | Vital rated Needs improvement | As above |
| `PAGE_SLOW_SERVER` | Performance | Medium; High > 1500 ms | `server_ms` at stat > 600 ms | Profile the request server-side: caching, queries, synchronous downstream calls, early flush of HTML head |
| `PAGE_HEAVY` | Performance | Medium; High > 5000 KB | `transfer_kb` at stat > 2000 KB | Compress/resize images, lazy-load media, code-split, remove unused libraries |
| `PAGE_TOO_MANY_REQUESTS` | Network | Low; Medium > 150 | `requests` at stat > 80 | Bundle, sprites/inline SVG, defer third-party tags, lazy-load |
| `LARGE_IMAGE` | Performance | Low; Medium when ≥ 3 large images or one > 1 MB | Any image > 300 KB | Resize to displayed size, WebP/AVIF with `srcset`, quality ~75–85, lazy-load |
| `LARGE_SCRIPT` | Performance | Medium | Median script transfer per load > 500 KB, or any single script > 250 KB | Code-split by route, tree-shake/minify, lazy-load features, audit dependencies |
| `UNCOMPRESSED_TEXT` | Network | Medium | Non-cached document/script/stylesheet (or XHR/fetch with a text content type) > 10 KB without gzip/br/zstd/deflate `content-encoding` | Enable Brotli or gzip on the server, CDN or proxy |
| `MISSING_CACHE_HEADERS` | BestPractice | Low | Same-origin script/stylesheet/image/font with no `cache-control`, `no-store` or `max-age=0` | Fingerprinted assets with `public, max-age=31536000, immutable`; ETag revalidation otherwise |
| `CONSOLE_ERRORS` | Reliability | Medium | Max console + page errors per measured load > 0 | Fix the errors (up to three examples in the finding) |
| `FAILED_REQUESTS` | Reliability | Medium; High ≥ 5 per load | Max failed sub-resource requests per load > 0 | Remove missing assets, fix failing endpoints, check CORS/CSP |
| `HIGH_VARIANCE` | Stability | Low | CoV > 0.3 for LCP, load or TTFB (≥ 3 samples) | More iterations, quieter machine/network; check server variability |
| `SLOW_API_CALL` | Performance | Medium | In-page XHR/fetch route with p95 duration > 1000 ms | Optimize the endpoint, or load its data so it does not block rendering |
| `API_CALL_FAILURES` | Reliability | High | In-page XHR/fetch route with any failure | Check server logs; handle API errors gracefully in the page |
| `N_PLUS_ONE_API` | Performance | Low | Same method + normalized route called > 5 times per page load on average | Batch into a list/bulk endpoint or include data in the parent response |
| `BROKEN_LINK` | Reliability | High | Crawled URL returned ≥ 400 or failed to load (one finding per link, referrer in detail) | Fix or remove the link, or redirect |

### 13.2 API load rules

Per load-tested endpoint, using its effective thresholds (per-endpoint overrides, else global).

| RuleId | Category | Severity | Trigger | Recommendation (summary) |
|---|---|---|---|---|
| `API_UNREACHABLE` | Reliability | Critical | Endpoint error or zero successful requests (other API rules are skipped) | Verify URL, method, auth headers/cookies, `expectedStatus`, service health |
| `API_ERROR_RATE` | Reliability | High; Critical > 5% | Error rate > `errorRatePct` (1% if unset) | Check server errors under concurrency: pool exhaustion, timeouts, rate limiting |
| `API_SLOW_P95` | Performance | Medium; High > 2 × limit | p95 > `p95Ms` (1000 ms if unset) | Profile under load; queries, caching, pool sizing |
| `API_TAIL_LATENCY` | Stability | Medium | p99 > 4 × p50 and p99 > 200 ms | Lock contention, GC pauses, cold caches, slow downstream calls, queueing |
| `API_THROUGHPUT_LOW` | Performance | Medium | `minRps` set and RPS below it | Scale out, remove serialization points, confirm the load generator was not the bottleneck |
| `API_TIMEOUTS` | Reliability | Medium; High > 1% of requests | Any `timeout` errors | Find what blocks requests; align client timeouts with SLAs |
| `API_DEGRADATION_UNDER_LOAD` | Stability | Medium | Request-weighted p95 of the last third of the timeline > 2 × the first third, and +50 ms or more (needs ≥ 6 timeline seconds and ≥ 10 requests per third) | Look for leaks, queue build-up, pool exhaustion, GC pressure; run a soak test |

### 13.3 Other rules

| RuleId | Category | Severity | Trigger | Recommendation (summary) |
|---|---|---|---|---|
| `REGRESSION` | Regression | Medium; High when \|Δ\| ≥ 2 × tolerance | A metric regressed against the baseline ([§8](#8-baseline-workflow)) | Review changes since the baseline run (code, dependencies, infrastructure, data volume) |
| `ANALYSIS_RULE_ERROR` | Reliability | Info | A rule threw an exception; the other findings are unaffected | Report it as a PerfLens bug |

## 14. Thresholds and CI gating

- Every non-null page threshold is checked for every page at `pageStatistic` (`maxConsoleErrors` uses the **max** over iterations); every API threshold for every endpoint (`p50Ms`/`p95Ms`/`p99Ms` against those latency percentiles, `errorRatePct`, `minRps` as a minimum). Per-page/per-endpoint overrides (matched by name) replace the global value property by property. A metric with no data (e.g. LCP on WebKit) is not checked.
- Pages that could not be measured and unreachable endpoints fail an `availability` check.
- Each check becomes a row in `thresholds.csv` and the report: scope, target, metric, statistic, actual, limit, passed.
- The run **passes** when all thresholds pass and (`minScore` is null or the overall score ≥ `minScore`).
- With `failOnThresholdBreach: true` (default) a failed run exits `1`; with `false` it exits `0` and the report still shows failures (useful for "report-only" pipelines).
- Disable a threshold by setting it to `null`, e.g. `thresholds: { page: { maxConsoleErrors: null } }`.

### 14.1 GitHub Actions example

```yaml
name: performance
on:
  pull_request:
  push:
    branches: [main]

jobs:
  perflens:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Install PerfLens
        run: |
          ./scripts/install.sh --with-deps --browsers chromium
          echo "$HOME/.local/bin" >> "$GITHUB_PATH"

      - name: Download baseline from main
        if: github.event_name == 'pull_request'
        env:
          GH_TOKEN: ${{ github.token }}
        run: |
          id=$(gh run list --workflow performance --branch main --status success -L 1 --json databaseId -q '.[0].databaseId')
          if [ -n "$id" ]; then gh run download "$id" -n perflens-report -D baseline || true; fi

      - name: Run PerfLens
        env:
          APP_PASSWORD: ${{ secrets.APP_PASSWORD }}
        run: |
          args=(-c perf/perflens.yaml -o perflens-out --label "${GITHUB_SHA::7}")
          [ -f baseline/results.json ] && args+=(--baseline baseline/results.json)
          perflens run "${args[@]}"

      - name: Job summary
        if: always()
        run: cat perflens-out/summary.md >> "$GITHUB_STEP_SUMMARY" || true

      - name: Upload report
        if: always()
        uses: actions/upload-artifact@v4
        with:
          name: perflens-report
          path: perflens-out/
```

Because `-o` is given, files land directly in `perflens-out/` (no timestamped subfolder). The `main` run's artifact becomes the baseline for pull requests. For stable numbers use a dedicated or larger runner, and point the config at a stable test environment (`--url` can switch environments).

To post `summary.md` as a PR comment instead, use `gh pr comment ${{ github.event.pull_request.number }} --body-file perflens-out/summary.md` (needs `pull-requests: write` permission).

## 15. Output files reference

Default location: `perflens-results/<yyyyMMdd-HHmmss>/` (`output.directory`, `output.timestampedSubfolder`).

| File | Format key | Contents |
|---|---|---|
| `report.html` | `html` | Self-contained interactive report |
| `results.json` | `json` | Complete run data; input for `--baseline` and `perflens report` |
| `summary.md` | `md` | Short Markdown summary for PRs / job summaries |
| `pages.csv` | `csv` | One row per page |
| `iterations.csv` | `csv` | One row per page iteration |
| `api.csv` | `csv` | One row per load-tested endpoint |
| `findings.csv` | `csv` | One row per finding |
| `thresholds.csv` | `csv` | One row per threshold check |
| `screenshots/*.png` | — | One screenshot per page (file names are slugs of the page name, e.g. `root.png`, `products.png`) |

### 15.1 `results.json`

camelCase properties, enums as camelCase strings, null values omitted. Top level:

| Property | Description |
|---|---|
| `schemaVersion` | Currently `1` |
| `runId`, `name`, `label`, `baseUrl` | Identity |
| `startedAt`, `finishedAt` | ISO 8601 timestamps |
| `environment` | `toolVersion`, `machineName`, `operatingSystem`, `dotnetVersion`, `processorCount`, `browserName`, `browserVersion` |
| `settings` | Human-readable key settings (iterations, cache mode, throttling, device, load profile) |
| `pages[]` | `name`, `url`, `source`, `depth`, `screenshotPath`, `error`, `iterations[]`, `summary` |
| `pages[].iterations[]` | `index`, `isWarmup`, `coldCache`, `success`, `error`, `httpStatus`, `finalUrl`, `wallClockMs`, `timing`, `vitals`, `resources`, `topResources[]`, `apiCalls[]`, `consoleErrors[]`, `pageErrors[]`, `domNodes`, `jsHeapUsedBytes` |
| `pages[].summary` | `measuredIterations`, `failedIterations`, `metrics` (key → stats), `apiCalls[]`, `score` |
| `crawl` | `visited[]`, `brokenLinks[]` (`url`, `depth`, `referrer`, `status`, `error`), `skippedLinks`, `durationMs` |
| `api.endpoints[]` | `name`, `method`, `url`, `source`, `profileDescription`, `totalRequests`, `successfulRequests`, `failedRequests`, `errorRatePct`, `rps`, `durationSeconds`, `bytesReceived`, `latency`, `histogram[]`, `timeline[]`, `statusCodes`, `errorTypes`, `error` |
| `analysis` | `score`, `grade`, `passed`, `thresholds[]`, `findings[]`, `vitalRatings`, `regressions[]`, `baselineRunId`, `highlights[]` |
| `warnings[]` | Run-level problems that did not abort the run |

Stats objects: `count`, `min`, `max`, `mean`, `median`, `p75`, `p90`, `p95`, `p99`, `stdDev`, `coV`.

### 15.2 CSV files

Comma-separated with a header row; fields containing commas, quotes or newlines are quoted (quotes doubled); numbers use invariant culture (`.` decimal separator, up to 3 decimals); timestamps are ISO 8601; missing values are empty.

| File | Columns |
|---|---|
| `pages.csv` | `page`, `url`, `source`, `depth`, `score`, `grade`, `measured_iterations`, `failed_iterations`, `error`, then `<metric>_median`, `<metric>_p75`, `<metric>_p95` for each metric key in this order: `ttfb_ms`, `fcp_ms`, `lcp_ms`, `cls`, `tbt_ms`, `long_tasks`, `dom_interactive_ms`, `dom_content_loaded_ms`, `load_ms`, `wall_clock_ms`, `dns_ms`, `connect_ms`, `tls_ms`, `server_ms`, `download_ms`, `requests`, `transfer_kb`, `decoded_kb`, `failed_requests`, `dom_nodes`, `js_heap_mb`, `console_errors` |
| `iterations.csv` | `page`, `index`, `warmup`, `cold_cache`, `success`, `error`, `http_status`, `final_url`, `wall_clock_ms`, `redirect_ms`, `dns_ms`, `connect_ms`, `tls_ms`, `server_ms`, `ttfb_ms`, `download_ms`, `dom_interactive_ms`, `dom_content_loaded_ms`, `load_ms`, `document_transfer_bytes`, `fcp_ms`, `lcp_ms`, `cls`, `tbt_ms`, `long_tasks`, `lcp_element`, `requests`, `transfer_bytes`, `decoded_bytes`, `failed_requests`, `third_party_requests`, `cached_requests`, `dom_nodes`, `js_heap_used_bytes`, `console_errors`, `page_errors`, `api_calls` (warm-ups included, flagged by `warmup`) |
| `api.csv` | `name`, `method`, `url`, `source`, `profile`, `total_requests`, `successful_requests`, `failed_requests`, `error_rate_pct`, `rps`, `duration_s`, `bytes_received`, `latency_min_ms`, `latency_mean_ms`, `latency_p50_ms`, `latency_p75_ms`, `latency_p90_ms`, `latency_p95_ms`, `latency_p99_ms`, `latency_max_ms`, `latency_stddev_ms`, `status_codes`, `error_types`, `error` |
| `findings.csv` | `severity`, `category`, `rule_id`, `target`, `title`, `detail`, `recommendation`, `metric`, `value` |
| `thresholds.csv` | `scope`, `target`, `metric`, `statistic`, `actual`, `limit`, `passed` |

## 16. Reading the HTML report

Open `report.html` in any modern browser; it works offline. Screenshots are embedded while their total is ≤ 15 MB; above that they are linked from `screenshots/`, so keep the output folder together.

1. **Header** — run name, label, base URL, timestamps, environment and settings; the **score gauge** with grade, pass/fail badge and highlights.
2. **Overview charts** — vitals per page (FCP/LCP/TBT/TTFB bars with dashed good/poor lines), navigation phase stacked bars (where time goes: DNS → connect → TLS → server → download → DOM processing), page weight by resource type, request counts, CLS and TBT, page scores.
3. **Vitals matrix** — pages × vitals, coloured Good / Needs improvement / Poor.
4. **Page cards** — per page: stats table (min/median/p75/p95/max/CoV), iteration trend lines (spot warm-up effects or outliers), resource doughnut by type, top resources (size, encoding, cache-control), in-page API calls, console/page errors, screenshot.
5. **API section** — per endpoint: percentile bars, per-second timeline (RPS, errors, p50/p95/max), latency histogram, status codes and error types.
6. **Thresholds** — every check with actual vs limit.
7. **Findings** — sortable table with severity/category filters; each with recommendation.
8. **Baseline** — deltas per metric, regressions highlighted red, improvements green.
9. **Crawl** — visited pages, skipped count, broken links with referrers.
10. **Glossary** — metric definitions.

How to read it: start with failed thresholds and Critical/High findings; for a slow page, look at the phase bars (server time vs download vs rendering) and the LCP element; check CoV before trusting small differences.

## 17. Troubleshooting

### 17.1 Ubuntu 20.04

Playwright ships no Ubuntu 20.04 browser builds. The CLI (before any browser work), `install.sh` and the installed `perflens` wrapper set `PLAYWRIGHT_HOST_PLATFORM_OVERRIDE=ubuntu22.04-x64` automatically (unless you already set it), so the 22.04 builds are used; they run on 20.04. If you install browsers some other way (e.g. `playwright.ps1`), export the variable yourself first.

### 17.2 Missing browser libraries

Errors such as `error while loading shared libraries: libnss3.so` or "Host system is missing dependencies" mean OS packages are missing:

```bash
perflens install-browsers chromium --with-deps     # uses sudo apt-get
```

### 17.3 Browser not installed

`Could not launch Chromium browser: Executable doesn't exist at ~/.cache/ms-playwright/...` → run `perflens install-browsers` (add the engine name for Firefox/WebKit). The run itself continues, marks every page unavailable and usually exits 1; `perflens doctor` confirms the fix.

### 17.4 WSL notes

- Run in headless mode (default). `--headed` needs WSLg (Windows 11) or an X server.
- Keep the repo and output on the Linux filesystem (`~/...`), not `/mnt/c`, for speed.
- `--open` uses the Linux opener; if nothing happens, open the file from Windows Explorer via `\\wsl$\...`.
- A target running on Windows `localhost` is reachable from WSL2 via `localhost` with mirrored networking, otherwise via the Windows host IP.

### 17.5 Login failures

- Run `perflens run -c ... --headed -v` and watch the steps.
- Check selectors in DevTools; prefer stable attributes (`[data-testid=...]`, `name=`).
- Add `WaitForSelector` before steps on screens that render late; raise `auth.login.timeoutMs`.
- Set `successUrlPattern` / `successSelector` so failures are detected.
- MFA/CAPTCHA: use `StorageState` ([§5.4](#54-mfa-captcha-or-complex-sso-storage-state)) or header tokens.
- Missing env var → load fails listing names: export them (`export APP_PASSWORD=...`).

### 17.6 Timeouts

- `Timeout 30000ms exceeded` on navigation: raise `browser.navigationTimeoutMs`, or use `waitUntil: DomContentLoaded` for pages whose `load` never finishes (long-polling, streaming). Avoid `NetworkIdle` on pages with analytics beacons or websockets.
- `waitForSelector` timeouts: verify the selector appears for the measured user.
- API `timeout` errors: raise `profile.timeoutMs` if above SLO, or reduce load.

### 17.7 Corporate proxies and self-signed certificates

- Self-signed / internal CA: `target.ignoreHttpsErrors: true` (browser and load). Prefer trusting the CA in the OS store.
- Proxies: the load engine uses the system proxy settings (`HTTPS_PROXY`/`HTTP_PROXY`/`NO_PROXY` on Linux). Measuring through a proxy adds its latency to every metric; prefer running inside the network.
- Install-time downloads (.NET SDK, browsers) honour `HTTPS_PROXY`.

### 17.8 Numbers look wrong

- LCP missing → WebKit/older Firefox, or the page has no contentful element. CLS/TBT shown as n/a on Firefox/WebKit → the engine lacks those APIs, so they are left out of the score ([§10.7](#107-engine-support)).
- TTFB high only on the first page → warm-up too low; server cold start.
- `transfer_kb` near 0 in warm mode → resources served from cache (expected).
- Very different from Lighthouse → different throttling (Lighthouse mobile simulates Slow 4G + 4× CPU), TBT window, and a single run vs percentiles.

### 17.9 Unexpected errors

Exit code 3 with a one-line `error:` message means an unexpected failure. Re-run with `PERFLENS_DEBUG=1` to print the full stack trace, and include it when reporting an issue.

## 18. Accuracy tips

1. **Dedicated machine.** Close other apps; avoid shared CI runners for gating, or loosen thresholds there.
2. **Warm-up ≥ 1** so DNS, TLS and server caches don't distort the first iteration.
3. **Iterations ≥ 5**; ≥ 10 for p90/p95 gates or throttled runs.
4. **Pick cache mode deliberately.** Cold = first visit (worst case, default); Warm = repeat visit. Don't compare across modes.
5. **Avoid VPNs and proxies**; run close to the target (same region) unless you want user-like latency — then be consistent.
6. **Stable target.** A quiet staging environment; don't benchmark during deployments or batch jobs.
7. **Keep the environment constant** between baseline and candidate: machine type, browser version, config.
8. **Watch CoV.** Differences smaller than the noise are not real.
9. **Separate page and load runs** if the load profile is heavy — PerfLens already runs API load after pages, but server recovery may take time before a next run.

## 19. Security considerations

- **Secrets** belong in environment variables referenced as `${ENV:NAME}`; never commit them in config files. In CI use secret stores.
- **Outputs**: header and cookie values are not written to results. Secret-looking query parameters (`token`, `access_token`, `api_key`/`api-key`, `key`, `secret`, `password`, `pwd`, `sig`, `signature`, `auth`) are redacted as `***` in report URLs. Screenshots can still show sensitive data from logged-in pages — share reports accordingly.
- **Storage state files** hold live sessions; protect and rotate them.
- **Auth headers** go to every browser request, including third-party requests the page makes; prefer cookie-based login or tokens scoped to the app.
- **Report safety**: every untrusted string is HTML-encoded and embedded data is escaped, so malicious page content cannot run script in the report.
- **Target safety**: the crawler is GET-only with default exclusions; discovered endpoint load is opt-in, GET-only and same-origin; load against configured non-GET endpoints only happens if you configure it.
- **Permission**: only benchmark and load-test systems you are authorized to test.

## 20. FAQ

**Do I need the application's source code or repository access?**
No. PerfLens is black-box. It needs the UI URL, credentials/login steps or headers for protected areas, and optionally API paths. All metrics come from browser performance APIs, Playwright network events and HTTP requests to the deployed endpoints. Repo access only helps with optional extras: running PerfLens in the app's own CI, correlating with server-side traces/APM, or BenchmarkDotNet micro-benchmarks of specific code.

**Can it test single-page applications (React, Angular, Vue, Blazor)?**
Yes. Configure each client route as a page path (full navigation to the route) and use `waitForSelector` so the page counts as ready only when the real content is rendered. Client-side route transitions after the first load are not measured separately.

**Is it safe to run against production?**
Page measurement is equivalent to a few users browsing: the crawl is GET-only, same-origin and excludes logout/signout/delete links by default (add your own exclusions). Load tests are different: they deliberately stress the system. Run them in production only with agreement, modest profiles (`concurrency`, `ratePerSecond`, `maxRequests`), outside peak hours, and never with state-changing endpoints.

**How is this different from Lighthouse?**
Lighthouse deeply audits one page per run. PerfLens measures many pages (configured + crawled, including logged-in ones), repeats each for statistics, adds API load testing, thresholds, baselines and one combined report. They complement each other (Lighthouse integration is on the roadmap).

**Why are my numbers different from PageSpeed Insights?**
PSI's lab data uses simulated mobile throttling on Google's machines; its field data comes from real Chrome users. PerfLens measures from your machine under your settings. Use `device`, `networkProfile` and `cpuThrottlingRate` to approximate mobile.

**How long does a run take?**
Roughly `pages × (warmupIterations + iterations) × (page load + settleMs)` plus crawl and `Σ(warmupSeconds + durationSeconds)` per endpoint. 10 pages × 6 loads × 3 s ≈ 3 min, plus API time.

**Does it support Windows?**
Yes, with the .NET 10 SDK: publish or `dotnet run` the CLI and use `perflens install-browsers`. The install script targets Linux/WSL/macOS.

**Can I measure an app on localhost?**
Yes, e.g. `perflens quick http://localhost:5000`. Remember that local numbers ignore real network latency.

**Can I add my own rules or report formats?**
Yes — see [ARCHITECTURE.md § Extensibility points](ARCHITECTURE.md#11-extensibility-points).
