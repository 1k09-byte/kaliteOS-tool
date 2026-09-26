<p align="center">
  <img src="Docs/kaliteConfig_icon.png" width="300" height="300">
</p>

# kaliteConfig

`kaliteConfig` is the official post-install utility and core orchestration dashboard for **kaliteOS**. Leveraging a modern WinUI 3 interface, it bridges the gap between low-level system tweaking scripts and consumer-grade UIs, allowing users to safely manage, integrate, and configure the underlying kaliteOS features (services, processes, hardware pipelines, network interfaces, and raw BIOS parameters) immediately after OS installation.

---

## Comprehensive Feature List

### 🛠️ Process Optimizer & Thread Tuner
- **Live Thread Visualization**: Granular real-time tracking of executing process threads with zero-delay UI filtering.
- **Dynamic Priority & Affinity Control**: Bind specific applications to dedicated CPU cores, isolate workloads, and inject system IRQ configurations.
- **Eco-Mode & Priority Boost**: Seamlessly apply Windows Efficiency mode to errant background services, and persistently boost critical application threads to High priority.
- **Automated Keepers**: Enforce background rules (via `ProfileWatcherService`) to ensure custom thread affinities and priority boosts remain locked even if the application restarts.

### 🌐 Network Engine
- **JSON Profile Staging**: Build, stage, export, and smoothly import TCP/IP profiles without manual registry entries.
- **Adapter Configuration**: Fine-tune Network Adapters, manage DNS resolution caching, and orchestrate bandwidth limiters in real-time.

### ⚡ Power & Scheduling Mastery
- **Visual Scheme Designer**: Navigate exhaustive lists of hidden Windows power attributes without delving into `powercfg` GUIDs.
- **Intelligent Parameter Translation**: Translates cryptic hex-value dropdowns (like ACPI and PCIe ASPM) into human-readable labels so you always know what you're modifying.
- **Template Synthesis**: Extract existing active configurations into portable `.pow` payloads.

### 💻 Deep BIOS Manager 
- **Direct CMOS Patching**: Reads active BIOS settings by directly interfacing with physical memory regions utilizing the `SCEWIN_64.exe` architecture.
- **In-App Editing**: Grouped, categorized, and searchable list of every single BIOS toggle (e.g. SMT, CPPC, C-States, Memory Training). 
- **Safe Validation**: Review changes in a pre-flight panel before flashing them immediately back into the motherboard.

### 🎮 GPU Optimization Hub
- **NVIDIA Profile Inspector Hook**: Automatically bundles the latest NVIDIA Inspector capabilities natively in the UI. 
- **Safety Rollbacks**: Reverts any catastrophic overclocking/driver parameter issues seamlessly with predefined safe-states. 

### 📐 Screenshot & Overlay Takeover
- **Native Snipping Tool Replacement**: Employs aggressive registry locks (IFEO) to forcefully override the default Windows Snipping tool and take full ownership of the `PrintScreen` key.
- **Win2D Snip Editor**: Extremely fast Direct2D-accelerated canvas for image markup, text rendering, and high-fidelity PNG imports. 

### 📦 System Integrations & Package Manager
- **Automated WinGet Hook**: Check, fetch, and update applications securely.
- **Dependency Automation**: Installs required runtimes (Visual C++, DirectX) in the background with zero user interruption.
- **Self-Updating Shell**: Built-in GitHub release integration ensures `kaliteConfig` can detect, stream, and overwrite itself with newer versions.

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
