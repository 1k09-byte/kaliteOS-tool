<p align="center">
  <img src="Docs/kaliteConfig_icon.png" width="180" height="180">
</p>

# kaliteConfig

`kaliteConfig` is the official post-install utility and core orchestration dashboard for **kaliteOS**. Leveraging a modern WinUI 3 interface, it bridges the gap between low-level system tweaking scripts and consumer-grade UIs, allowing users to safely manage, integrate, and configure the underlying kaliteOS features (services, processes, hardware pipelines, network interfaces, and raw BIOS parameters) immediately after OS installation.

---

## 🛠️ Exhaustive Feature Breakdown

### 💻 Deep BIOS Manager 
- **Direct CMOS Patching**: Read, modify, and apply raw BIOS settings actively in Windows. Interfaces directly with physical memory using embedded `SCEWIN_64.exe` execution pipelines.
- **Categorized In-App Editing**: Search and modify historically inaccessible toggles natively ranging from Memory Training rules, C-States, CPPC, and SMT parameters translated into categorized buckets (CPU, PCIe, Power, Security).
- **Safety Pre-Flight Validation**: Automatic warning flags for potentially dangerous variable overrides before flashing the CMOS.
- **Live Profile Import / Export**: Save, load, and version-control distinct BIOS configurations via standard `.txt` profiles.

### 🧵 Advanced Process Optimizer & Thread Tuner
- **Live Thread Visualization**: Granular tracking mapping of all dynamically executing threads for running processes with zero-delay telemetry mapping.
- **Dynamic Thread Priority & Binding**: Drill into individual applications, forcing distinct execution threads to high priorities or segregating them to isolated CPU cores to stop OS-level contention.
- **One-Click Eco-Mode Toggles**: Throttle runaway network and telemetry services by instantly suppressing their execution threads into Windows Efficiency mode alongside deep Priority Boost switches mapped visually per-thread.
- **Persistent Keepers**: The embedded `ProfileWatcherService` guarantees your custom core affinities and priority boost rules persist effortlessly, even if the target application re-launches.

### ⚡ Power & Scheduling Configurator
- **Visual Scheme Designer**: Break out of standard control panels. Unveil, edit, and navigate an exhaustive list of hidden Windows `powercfg` GUID parameters dynamically mapped into the application interface.
- **Worded Value Translations**: Effortlessly manage complex hex-string properties like deep PCIe ASPM controls and ACPI sleep bindings translated dynamically into readable drop-down English configurations. 
- **Template Synthesis**: Export, inject, and backup `.pow` payload power schemes cleanly.

### 🎮 GPU Optimization Hub
- **NVIDIA Profile Inspector Integration**: Bundle the raw capabilities of the NVIDIA Profile Inspector seamlessly behind a gorgeous WinUI 3 dashboard interface.
- **Direct Global Overrides**: Manage `global_profile.nip` profiles alongside advanced framerate limitation and telemetry prevention overlays perfectly optimized without jumping out to legacy tools.
- **Rollback Safeties**: Instantly pull strings back to safe-state defaults to revert catastrophic driver tuning.

### 📊 Benchmark View & Telemetry
- **PresentMon & CapFrameX Synchronization**: Natively ingest deeply accurate CSV telemetry logic.
- **Performance Overlays**: Trace real-time hardware latencies and frametimes dynamically over workloads to quantify tweaks.

### 🌐 Advanced Network Engine
- **Hardware TCP/IP Profiles**: JSON-based profile staging mapped cleanly. Modify bandwidth limiters, and DNS resolution caching entirely locally.
- **Deep Adapter Configuration**: Access properties embedded deep inside registry driver parameter mapping for your network card (e.g. interrupt moderation, checksum offloads).

### 📐 Snip Overlay Takeover
- **Native Snipping Tool Aggression**: Bypasses the default Windows Snipping logic utilizing Image File Execution Options (IFEO) registry hooks to force take over the `PrintScreen` key.
- **High-Performance Canvas**: Win2D/Direct2D accelerated image editor, text markdown injections, sticker/layer mechanics all available the split-second you capture a screen.
- **Persistent Asset Gallery**: Track, archive, and manage historical cuts immediately natively.

### 📦 Application Integrations & Runtimes
- **Automated WinGet Hook**: Fast fetch, search, and deployment algorithms utilizing natively linked WinGet CMD processes.
- **Background Runtimes Manager**: Seamlessly stream Microsoft Visual C++ redistributables and DirectX libraries silently to stop interactive permission lockups during `kaliteOS` deployment.
- **Card-Driven Uninstaller**: Tear down bulky software remnants inside a modernized premium card-view that actively maps software components and registry uninstall sequences faster than Control Panel interfaces. 

### ⚙️ OS Subsystem Toggles
- **Reserved CPU Sets**: Restrict Windows OS telemetry and baseline threads to specific, poorly performing E-Cores so main gaming P-Cores remain utterly untouched (`ReservedCpuSetsPage`).
- **Hardware Interrupt Tuning (MSI/IRQs)**: Force distinct PCIE device processing queues onto unburdened logical processors via `AffinityPage`.
- **Centralized OS Hub**: Toggle classic Windows privacy tools, notification handlers, and visual telemetry rules cleanly in `WindowsSettingsHubPage`.
- **Automated OTA Updating**: Intelligent Github Release pipeline fetcher that tracks, evaluates, downloads, and seamlessly self-updates the `kaliteConfig` application framework through our proprietary Markdown parser mechanics.

## 🌟 Credits & Acknowledgments

A massive thank you to the following individuals who helped make this project possible:

- **tinodin** - Inspired the core WinUI 3 architecture and the vision to build this tool! 🔥 
  [GitHub (AutoOS)](https://github.com/tinodin/AutoOS) | [Discord Community](https://discord.gg/prmaYcsDwy)
- **jackpot71** - Provided phenomenal ideas, ongoing guidance, and is the creator of the brilliant Hypertune! 🚀
  [GitHub (Hypertune)](https://github.com/JACKPOT71/Hypertune) | [Discord Community](https://discord.gg/XhtSQBE4jS)
- **herothefoxking.** - An incredibly educated mind who helped endlessly with strict logic! 🧠 🦊
  [Discord Community](https://discord.gg/vTYNb8re9g)

## License

This software is strictly proprietary. All rights reserved. 
You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense, reverse-engineer, or sell copies of the software in any form, in whole or in part, without the express written permission of the copyright holder. See the `LICENSE` file for details.
