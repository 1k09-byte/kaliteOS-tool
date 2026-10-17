# Personalize progress

Branch: `feature/personalize`. Baseline build: 14 warnings, 0 errors.

| Phase | Status |
|---|---|
| 0 Setup (branch, baseline, facts) | committed |
| 1 Plan (PERSONALIZE-PLAN.md) | committed |
| 2 Core infrastructure (interop, HKCU registry, snapshot, applier, capability, presets) | committed |
| 3 Services (wallpaper, cursor, colors, shell tweaks, visual effects) | committed |
| 4 UI page, nav item, DI registration | committed |
| 5 Profiles (save/apply/export/import) | not started |
| 6 Hardening and static checks | not started |
| 7 PERSONALIZE-REPORT.md | not started |

Phase 2 files live in `src/kaliteConfig/Services/Personalize/`:
- `Native/NativeMethods.cs`: hand-written P/Invoke (no CsWin32 in this project)
- `Native/HkcuRegistry.cs`: HKCU-only registry access (refuses HKLM)
- `Native/SystemNotifier.cs`: WM_SETTINGCHANGE and DWM color broadcasts (bounded timeout)
- `SettingDefinition.cs`: `SettingDef`, `SettingResult`
- `SnapshotStore.cs`: original-value snapshots in `%LOCALAPPDATA%\kaliteConfig\personalize\snapshot.json`
- `SettingApplier.cs`: apply, verify, roll back, revert
- `WindowsCapabilityService.cs`: real build (RtlGetVersion), packaging, policy locks, high contrast
- `PersonalizationPresets.cs`: accent swatch data and ABGR conversion
