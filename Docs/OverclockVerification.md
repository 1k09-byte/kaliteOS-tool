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

## Remaining before calling the module done (next phases)

- [ ] Real triggered TDR with the fixed watchdog (memory-stress tool or under-load offset)
- [ ] Fan Curve mode tracking a real load within the polling interval
- [ ] Kill the app mid-Curve-mode → fan must not stay stuck (Dispose path restores Auto)
- [ ] GPU re-detect (RefreshAsync) end-to-end in the UI
- [ ] Phase 3: remaining controls on the same safety path (power/temp limits via UI flow)
- [ ] Phase 5: startup reapply via Task Scheduler incl. elevated task validation
