# Idle CPU / memory report — branch `perf/hybrid-native`

Date: 2026-10-23. Build: Release x64, `net10.0-windows10.0.22621.0`. Machine: 20+ logical
cores, Windows 11. The app self-sets Idle priority and Disabled priority boosting at launch.

## Method

- `perf/measure-process.ps1`, `perf/measure-idle-state.ps1` — per-second process CPU
  (`TotalProcessorTime` delta), private bytes, thread and handle counts.
- `perf/navigation-cycles.ps1` — UIA-driven navigation through all pages (leak check).
- `perf/collect-trace.ps1` + `perf/tools/dotnet-trace.exe` — EventPipe CPU traces
  (window visible, then minimized). `dotnet-trace report topN` reports **thread-time
  (wall clock)**, not CPU: blocked/parked stacks (thread-pool waits, message loops)
  dominate its output, so it is only used here to confirm what does *not* burn CPU.
  Attribution of actual CPU was done from process CPU deltas around scenario changes.
- App runs elevated (`requireAdministrator`); tracing was run from an elevated shell.

## Baseline (before this branch's fixes)

| Scenario | avg CPU | private |
|---|---|---|
| Idle, window visible | 0.25–0.44 % | ~168 MB |
| Idle, window minimized | 0.11–0.23 % | ~167 MB |
| 50 navigation cycles | leak-free (261.1 → 262.1 MB private, handles 1889 → 1882) | |

## Findings and fixes (this commit)

1. **GPU telemetry loop never stopped.** `GpuTelemetryPollingService` (1 Hz
   `ReadTelemetry()` driver queries) was started by `OverclockViewModel.InitializeAsync`
   on every visit to the Graphics page. `Teardown()` (page `Unloaded`) detached handlers
   but never stopped the loop, so one visit left the driver being polled every second for
   the rest of the app's life — including while minimized. `Teardown()` now calls
   `Telemetry.StopAsync()`; the next `Loaded → InitializeAsync` restarts it.
   `Telemetry.Update` has no other subscriber, so nothing else depended on the loop.
2. **TDR watchdog polled the System log every 1.5 s with two full scans.**
   `TdrWatchdogService.OnTick` ran two reverse `EventLogReader` XPath scans over the
   last 24 h of the System log per tick while armed. Now: one merged provider query
   (`nvlddmkm` or `Display`), bounded by `timediff` to the arm window, 5 s cadence
   (first check still at 1.5 s). Detection latency stays inside the safety confirmation
   countdown that consumes it. Same newest-event result as before; the public
   `TryReadLastTdrTimeUtc()` keeps its 24 h semantics for tools.
3. **Watchdog re-arm bug.** `Arm()` created the timer once with `??=`; after a
   `Disarm()` parked it (`Change(Infinite, Infinite)`), every later `Arm()` in the same
   session left it parked — the watchdog silently stopped watching for driver resets
   after the first confirmation cycle. `Arm()` now re-`Change()`s the timer.

Checked and deliberately not changed: `ForegroundWatcher` (event-driven
`SetWinEventHook`, blocked in `GetMessage` — shows up in wall-time reports but costs no
CPU), `ProfileWatcherService` keeper (20 s sweep; primary path is the WMI
`Win32_ProcessStartTrace` watcher), `ForegroundSuspendService` 1 s tick (only runs when
the user enables auto-suspend), `GameProcessWatcherService` (WMI event-driven with a
2.5 s polling fallback only when WMI is unavailable).

## After

| Scenario | avg CPU | private |
|---|---|---|
| Idle, window visible, settled | **0.013 %** | 352 MB |
| Idle, window minimized | **0.023 %** | 282 MB |
| Idle right after fresh start (still settling) | 0.417 % | 278 MB |
| Idle right after visiting + leaving Graphics | 1.009 % → settles to 0.013 % | — |

The post-page-visit measurement window catches one-time work (page JIT, NVAPI
capability queries, auto-apply WMI startup); a minute later idle sits at 0.013 % and a
CPU trace shows no telemetry frames. Samples: `perf/after-*.csv`.

## Native layer evaluation

Windows SDK 10.0.26100.7705 was installed (headers + libs under
`C:\Program Files (x86)\Windows Kits\10`, version `10.0.26100.0`). Candidates that were
"not evaluated" on the earlier pass:

- **Vendor telemetry (NVAPI / ADLX):** both vendor SDKs expose telemetry only as
  polled sampling; there is no push/event API to subscribe to for per-second metrics.
  The managed 1 Hz loop is already the minimal native shape, and it now only runs while
  the page that displays the data is on screen. No native rewrite justified.
- **Driver-reset detection:** an `EventLogWatcher` push subscription is possible, but
  with the query bounded to the arm window the polled cost is negligible and the code
  stays simpler. Not warranted at the measured 0.013 % idle.
- **Process watching:** already event-driven (WMI process traces); native
  `WTSEnumerateProcesses`/ETW would add code for no measurable idle win.

Conclusion: the measured idle CPU no longer contains a component that a native
implementation would remove. The SDK is installed if a later change needs it; compiling
C++ prototypes would additionally require MSVC Build Tools, which was not installed
because no remaining candidate justifies it.

## Verification

- `dotnet build` (app, Release x64): clean, 0 warnings.
- Idle re-measured with the same scripts as the baselines (table above).
- `tools/OcVerify` does not build — **pre-existing** (31 `CS0246` errors with this
  branch's changes stashed as well; its project references have bit-rotted). The
  watchdog's public API used by that tool is unchanged.
- The watchdog re-arm fix is logic-reviewed, not runtime-triggered (exercising it would
  require deliberately provoking a GPU driver reset).
