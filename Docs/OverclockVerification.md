# GPU Overclock Module — Phase 2 Verification Record

Machine: LITEOS-BETA3 · RTX 4070 SUPER · driver 616.92 · elevated session · date 2026-09-17
Harness: `tools/OcVerify` (compiles the module's REAL services — no fakes — and drives the actual GPU).

## Results

| Spec item | Result |
|---|---|
| §3 Telemetry vs nvidia-smi | PASS — 5/5 reads; temp Δ 0 °C, core Δ 0 MHz, mem Δ 0 MHz; caps core [-1000..1000], mem [-1000..3000], power [45.5..145.5]%, temp [65..88] °C, fan=true |
| §4.2 Apply → confirm sticks | PASS — +15 MHz core applied, readback confirmed, value persisted after Keep |
| §4.2 Countdown expiry reverts | PASS — reverted to the *confirmed* prior working config (+15), not factory defaults |
| §4.2 Manual "Revert now" | PASS — mem +100 reverted to anchor mid-window |
| §4.2 Anchor = pre-batch state | PASS — second-batch revert restored +10 working config, not 0 |
| §4.3 TDR watchdog | **BUG FOUND AND FIXED** — see below; detection pipeline now verified end-to-end |
| §8 Out-of-range writes | **GAP FOUND AND FIXED** — driver *accepts* +10000 MHz silently; controller now clamps every write to the driver-queried range |

## Findings that changed code

1. **TdrWatchdogService event-log reader was dead code.** The classic
   `System.Diagnostics.EventLog.Entries` reader returns nothing on .NET 10 even when
   matching events exist (PowerShell `Get-WinEvent` saw them; the .NET reader did not).
   A real TDR would have gone undetected. Rewritten on `EventLogReader`
   (System.Diagnostics.Eventing.Reader) with newest-first XPath queries — query shape
   validated live against real log data. Classic-provider qualified IDs break
   `EventID=4101` XPath matching, so Display/4101 is ID-filtered in code.
2. **The driver accepts out-of-range clock deltas without error.** A +10,000 MHz write
   succeeded at the API level. UI sliders are range-limited, but profile applies read
   values from files — so `NvApiGpuController` now clamps every core/mem/power/temp
   write to the driver-queried range before issuing it (verified: +10000 → applied +1000).

## TDR attempts (honest record)

Two bounded probes, both through the real safety machine with a 90 s window:

- Core slider-max (+1000 MHz): no reset — card idle at 210 MHz never reaches stressed
  clocks; probe aborted cleanly and reverted.
- Core + memory slider-max (+1000 / +3000 MHz): no reset — VRAM-exercising scanout was
  not enough either. Probe aborted cleanly and reverted.

No escalation beyond driver-allowed maxima was attempted. A real reset will be produced
later by a memory-stress tool (e.g. OCCT VRAM) or an under-load offset run; the watchdog
pipeline itself (reader → arm → tick → fire → auto-revert) is verified via a clearly
labeled synthetic `nvlddmkm` Error event injected into the System log: the armed
watchdog fired and drove a genuine revert to the anchor.

Two synthetic self-test events (provider `nvlddmkm`, text "kaliteConfig OcVerify
self-test") remain in the System event log from this validation — harmless, labeled.

## Phase-2 completion + Phase-3 results (2026-09-17, later session)

| Spec item | Result |
|---|---|
| §9 Fan Curve tracks a real thermal change | PASS — no load tools on the machine, so load was induced with a driver clock lock (`nvidia-smi -lgc 2400`): power 5.2 → 16.7 W. The curve tracked within the polling interval through the whole drift (fan 52 → 48 → 44 → 40% following the commanded curve as temp fell), and after stop the fan handed back to driver auto (0%). |
| §9 Kill the app mid-Curve-mode | PASS — reproduced the bug class for real: a child process forced 100% fan through the real curve service and was hard-killed (no Dispose possible). The fan stayed stuck at 100% — then the new recovery path (`EnsureNoOrphanedFanControl`, liveness marker with pid) detected the orphan on "next start", restored driver control (100% → 0%), and cleared the marker. Recovery is a verified no-op when no orphan exists. |
| §Process(3) Power limit through the safety machine | PASS — 100 → 110% applied, driver readback confirmed (110%), sticks after confirm, expiry reverted to the confirmed anchor. |
| §Process(3) Temp limit through the safety machine | PASS — 84 → 86 °C applied, readback confirmed, expiry reverted to anchor (84 °C); the confirmed power anchor was untouched by the temp batch's revert (batch isolation). |

New code from this session: `FanCurveExecutionService` now maintains a liveness marker
(pid + forced %) while the curve loop runs and clears it on stop; module init calls
`EnsureNoOrphanedFanControl` which restores driver fan control and logs an AutoRevert
entry when a dead process's marker is found — a hard-killed app can no longer leave a
stuck fan. The harness gained `fan`, `orphan`, and `limits` steps.

## Phase-4 + Phase-5 results (2026-09-17, final session)

**Phase 4 (fan control incl. Curve mode) — COMPLETE:**
- Spec §5 re-detect gap fixed: `RefreshAsync` stops the curve loop with a real Auto
  hand-back when the GPU is re-detected (the loop would otherwise force speeds on
  whatever adapter the re-resolved controller returns).
- Integration wiring added: the Drivers page Re-detect button now calls
  `Vm.RefreshAsync`; `GpuOverclockModule.Dispose` is wired on window close (app
  closing → fan restored to Auto, watchdog disarmed).
- UI flows verified end-to-end against the running app via UIA
  (`tools/uia-smoke.ps1`): Drivers nav → risk gate → accept → telemetry live →
  Curve UI appears → back to Auto; fan returned to driver control (0%).
- New unit tests (FanLifecycleTests): stop restores Auto + clears marker; dead-pid
  orphan recovered; LIVE-pid marker never touched — this test caught a real bug
  (own-pid stale/fresh marker ambiguity during double init), fixed via marker
  freshness (loop rewrites marker every tick).

**Phase 5 (profiles + startup reapply) — COMPLETE:**
- Default-profile designation in `ProfileStorageService` (`startup-default.marker`,
  deliberately not `.json` so the loader can't see it as a phantom profile — caught
  by a test), delete-clears-default, `ConfirmedAt` round-trips.
- `SafetyRevertService.ConfirmSilently`: a headless window whose expiry confirms
  instead of reverts (per-batch flag, not sticky); TDR still reverts immediately;
  a subsequent NORMAL batch still reverts on expiry (all unit-tested).
- `StartupApplyService`: re-applies the designated profile through the SAME safety
  machine with a shortened 5 s headless window; refuses profiles without the
  pre-boot-validated `ConfirmedAt` flag (black-screen guard); stamps `ConfirmedAt`
  only when the window closed cleanly without a TDR revert.
- App wiring: `--apply-overclock-startup` arg → background reapply after the driver
  settles; "At startup" toggle per profile + `validated` badge; registering the
  Task Scheduler task requires a designated default first.
- Real-hardware checks: Task Scheduler register→query→unregister cycle PASS;
  headless startup-apply flow PASS (unvalidated refused, validated applied +15 core,
  ConfirmedAt stamped, baseline restored).

The stated spec-6 tradeoff, implemented as designed: startup reapply is never exempt
from the safety machine — it uses a shorter window whose expiry confirms, with the
TDR watchdog armed the whole time, because blocking login on an invisible prompt is
not an option; the pre-boot-validated flag is what makes a profile safe to reapply.

## Phase-6 results (2026-09-17, change-log viewer + export)

**Phase 6 (transparency tooling) — code complete, unit-tested:**
- Change-log viewer gained Result (OK / Reverted / Failed) and Source (Manual /
  Profile apply / Startup apply / Auto revert) filters, a "no matching entries"
  empty state, and an initial population on module init (startup-path entries —
  orphan recovery, headless reapply — were previously invisible until the first
  safety event).
- Filter mapping (ComboBox index → enum) and the combined predicate live in
  `OverclockLogFilter` — pure logic, unit-tested, not embedded in the ViewModel.
- New `OverclockVerificationExporter` builds a Markdown verification record from
  the change-log tail in the same shape as this document: GPU/driver header,
  per-result summary counts, and a chronological table of every audited write
  (control, old → new, source, result, failure reason). Cells are pipe-escaped so
  values can never break the table (unit-tested, including the empty-log case).
- Export from the viewer writes through a save picker: brokered WinUI picker
  first, Win32 common dialog fallback for elevated sessions (the app runs
  elevated — the same pattern BiosManager uses).
- 13 new unit tests (`OverclockLogViewerTests`); full suite 41/41 PASS and the
  app project builds clean on x64. End-to-end UI verification (open viewer →
  filter → export → file contents) still to be exercised on the real machine.

## Remaining before calling the module done (next phases)

- [ ] Real triggered TDR with the fixed watchdog (memory-stress tool or under-load offset)
- [ ] Phase-6 UI walkthrough on hardware: viewer filters, export round-trip (via `tools/uia-smoke.ps1`)
