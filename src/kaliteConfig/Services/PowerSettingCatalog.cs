// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are provided freely for end-users
// to download and use. However, the source code remains strictly proprietary. 
// You may not copy, reproduce, modify, merge, reverse-engineer, publish, distribute, 
// sublicense, or sell copies of the source code in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Services;

/// <summary>
/// Worded options for the power settings Windows hides behind raw numbers.
///
/// WHY THIS EXISTS. Every hidden setting in the advanced power dialog is
/// enumerated fine, but its option names are NOT: measured on this build,
/// PowerReadPossibleFriendlyName returned an empty string for all 174
/// settings of the Balanced plan, so a setting like "Lid close action"
/// arrived as the bare index 1 and the UI had to show "1". The OS simply does
/// not publish these names to applications, so they have to be supplied here.
///
/// EVERY GUID BELOW IS MEASURED, NOT GUESSED. The Balanced plan was enumerated
/// through the app's own Native/PowrProf.cs, each setting's friendly name was
/// read back with PowerReadFriendlyNameSetting, and its ValueMin/ValueMax were
/// read out of
/// HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\{sub}\{set}.
/// The unit is encoded alongside the GUID in the comment next to each block.
///
/// A GUID key is also immune to the display language changing, which a name
/// match is not, so it is always tried first. <see cref="NameHints"/> and
/// <see cref="ExactNames"/> exist purely as a fallback for machines that
/// publish a setting GUID we do not know.
///
/// SCOPE DISCIPLINE. Only settings whose meaning is unambiguous in English get
/// an entry. Where Windows exposes an opaque bitfield or an undocumented
/// internal value the setting is deliberately left out rather than given a
/// guess: a wrong word is worse than a number, because a number at least looks
/// like a number. Settings left out still keep their raw number box, so
/// nothing becomes unreachable.
///
/// A "Never" sentinel is 0 or 0xFFFFFFFF depending on the setting; both are
/// folded into one word so nobody has to know which.
/// </summary>
public static class PowerSettingCatalog
{
    /// <summary>One worded option. The index is what gets written to Windows.</summary>
    public readonly record struct Option(uint ValueIndex, string Label);

    public sealed class Entry
    {
        /// <summary>Enumerated/boolean setting: label per index, index 0 first.</summary>
        public string[] Labels { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Continuous setting: the handful of values that actually mean
        /// something to a person ("Never", "1 minute"). The raw NumberBox
        /// stays available so nothing becomes unreachable.
        /// </summary>
        public Option[] Presets { get; init; } = Array.Empty<Option>();

        /// <summary>Whole-name match, used before the looser substring match.</summary>
        public string[] ExactNames { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Lower-case aliases for this setting, matched as a WHOLE name
        /// (case-insensitively) - never as a substring. See
        /// <see cref="TryGet"/> for why a substring match is not safe here.
        /// </summary>
        public string[] NameHints { get; init; } = Array.Empty<string>();

        /// <summary>True when this entry describes an enumerated value list.</summary>
        public bool IsEnum => Labels.Length > 0;
    }

    /// <summary>The value Windows uses for "never" in a timeout setting.</summary>
    public const uint NeverTimeout = uint.MaxValue;

    private static Option[] Preset(params (uint Value, string Label)[] items)
        => items.Select(i => new Option(i.Value, i.Label)).ToArray();

    /// <summary>Common timeout choices, in seconds, starting with "Never" (0).</summary>
    private static Option[] TimeoutPresets(string neverLabel = "Never", params (uint V, string L)[] rest)
    {
        var list = new List<Option> { new(0u, neverLabel) };
        list.AddRange(rest.Select(i => new Option(i.V, i.L)));
        return list.ToArray();
    }

    /// <summary>
    /// Timeout choices in seconds, where the "never" sentinel is 0xFFFFFFFF
    /// rather than 0. Execution Required is the setting that does this.
    /// </summary>
    private static Option[] NeverIsMaxTimeoutPresets(params (uint V, string L)[] rest)
        => Preset(new[] { (NeverTimeout, "Never") }.Concat(rest).ToArray());

    /// <summary>
    /// A setting expressed in percent. The percentage is kept in the word
    /// ("50% - Balanced") because a percentage without its number would be
    /// meaningless, but the word is what tells you what the number DOES.
    /// </summary>
    private static Option[] PercentTiers(string zeroWord = "Off") => Preset(
        (0u,   zeroWord),
        (1u,   "1% - Minimum"),
        (5u,   "5% - Very low"),
        (10u,  "10% - Low"),
        (20u,  "20% - Moderate"),
        (40u,  "40% - Medium"),
        (50u,  "50% - Balanced"),
        (60u,  "60% - Responsive"),
        (70u,  "70% - High"),
        (75u,  "75% - Very high"),
        (90u,  "90% - Near maximum"),
        (95u,  "95% - Almost maximum"),
        (99u,  "99% - Nearly maximum"),
        (100u, "100% - Maximum"));

    /// <summary>A setting expressed in whole cores / whole units.</summary>
    private static Option[] CoreTiers(string zeroWord) => Preset(
        (0u,   zeroWord),
        (1u,   "1 core"),
        (2u,   "2 cores"),
        (3u,   "3 cores"),
        (4u,   "4 cores"),
        (6u,   "6 cores"),
        (7u,   "7 cores"),
        (8u,   "8 cores"),
        (10u,  "10 cores"),
        (12u,  "12 cores"),
        (16u,  "16 cores"),
        (20u,  "20 cores"),
        (24u,  "24 cores"),
        (32u,  "32 cores"),
        (64u,  "64 cores"),
        (100u, "Maximum"));

    /// <summary>A setting expressed in milliseconds, where 0 turns the feature off.</summary>
    private static Option[] MillisecondTiers(params (uint V, string L)[] rest)
        => Preset(new[] { (0u, "Off") }.Concat(rest).ToArray());

    private static readonly Dictionary<Guid, Entry> ByGuid = new()
    {
        // =====================================================================
        // Subgroup: Hard disk  (all values in SECONDS, ValueMax 0xFFFFFFFF)
        // =====================================================================

        // Enum. Windows documents 0=Active, 1=HIPM, 2=DIPM.
        [new Guid("0b2d69d7-a2a1-449c-9680-f91c70521c60")] = new Entry
        {
            Labels = new[] { "Active", "HIPM", "DIPM" },
            ExactNames = new[] { "AHCI Link Power Management - HIPM/DIPM" },
            NameHints = new[] { "hipm/dipm" },
        },
        // Seconds. Bursts shorter than this are still counted as activity.
        [new Guid("80e3c60e-bb94-4ad8-bbe0-0d3195efc663")] = new Entry
        {
            Presets = TimeoutPresets("Never",
                (5, "5 seconds"), (10, "10 seconds"), (20, "20 seconds"),
                (30, "30 seconds"), (60, "1 minute"), (300, "5 minutes")),
            ExactNames = new[] { "Hard disk burst ignore time" },
            NameHints = new[] { "hard disk burst ignore" },
        },
        // Seconds.
        [new Guid("6738e2c4-e8a5-4a42-b16a-e040e769756e")] = new Entry
        {
            Presets = TimeoutPresets("Never",
                (60, "1 minute"), (300, "5 minutes"), (600, "10 minutes"),
                (1200, "20 minutes"), (1800, "30 minutes"), (3600, "1 hour")),
            ExactNames = new[] { "Turn off hard disk after" },
            NameHints = new[] { "turn off hard disk after" },
        },
        // Seconds. NVMe idle timeouts are short by nature, hence 200/2000.
        [new Guid("d639518a-e56d-4345-8af2-b9f32fb26109")] = new Entry
        {
            Presets = TimeoutPresets("Never",
                (200, "3 seconds"), (2000, "30 seconds"), (6000, "1 minute"), (60000, "1 hour")),
            ExactNames = new[] { "Primary NVMe Idle Timeout" },
            NameHints = new[] { "primary nvme idle timeout" },
        },
        [new Guid("d3d55efd-c1ff-424e-9dc3-441be7833010")] = new Entry
        {
            Presets = TimeoutPresets("Never",
                (200, "3 seconds"), (2000, "30 seconds"), (6000, "1 minute"), (60000, "1 hour")),
            ExactNames = new[] { "Secondary NVMe Idle Timeout" },
            NameHints = new[] { "secondary nvme idle timeout" },
        },
        // Milliseconds (0 = disabled). ValueMax 60000.
        [new Guid("fc95af4d-40e7-4b6d-835a-56d131dbc80e")] = new Entry
        {
            Presets = MillisecondTiers(
                (5, "5 milliseconds"), (15, "15 milliseconds"),
                (50, "50 milliseconds"), (100, "100 milliseconds")),
            ExactNames = new[] { "Primary NVMe Power State Transition Latency Tolerance" },
            NameHints = new[] { "primary nvme power state transition latency" },
        },
        [new Guid("dbc9e238-6de9-49e3-92cd-8c2b4946b472")] = new Entry
        {
            Presets = MillisecondTiers(
                (5, "5 milliseconds"), (15, "15 milliseconds"),
                (50, "50 milliseconds"), (100, "100 milliseconds")),
            ExactNames = new[] { "Secondary NVMe Power State Transition Latency Tolerance" },
            NameHints = new[] { "secondary nvme power state transition latency" },
        },
        // Enum. Non-Operational Power State Permissive Mode.
        [new Guid("fc7372b6-ab2d-43ee-8797-15e9841f2cca")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "NVMe NOPPME" },
            NameHints = new[] { "noppme" },
        },

        // =====================================================================
        // Subgroup: USB settings
        // =====================================================================

        // Enum. Power states a USB 3 link may drop to when idle.
        [new Guid("d4e98f31-5ffe-4ce1-be31-1b38b384c009")] = new Entry
        {
            Labels = new[] { "Off", "Minimum Power Savings", "Maximum Power Savings" },
            ExactNames = new[] { "USB 3 Link Power Mangement" },
            NameHints = new[] { "usb 3 link power" },
        },
        // Seconds. ValueMax 100000.
        [new Guid("0853a681-27c8-4100-a2fd-82013e970683")] = new Entry
        {
            Presets = TimeoutPresets("Off",
                (50, "50 milliseconds"),
                (100, "0.1 seconds"), (250, "0.25 seconds"),
                (500, "0.5 seconds"), (1000, "1 second")),
            ExactNames = new[] { "Hub Selective Suspend Timeout" },
            NameHints = new[] { "hub selective suspend timeout" },
        },
        [new Guid("48e6b7a6-50f5-4782-a5d4-53bb8f07e226")] = new Entry
        {
            Labels = new[] { "Disabled", "Enabled" },
            ExactNames = new[] { "USB selective suspend setting" },
            NameHints = new[] { "usb selective suspend" },
        },
        [new Guid("498c044a-201b-4631-a522-5c744ed4e678")] = new Entry
        {
            Labels = new[] { "Disabled", "Enabled" },
            ExactNames = new[] { "Setting IOC on all TDs" },
            NameHints = new[] { "ioc on all" },
        },

        // =====================================================================
        // Subgroup: Sleep
        // =====================================================================

        // Seconds. ValueMax 0xFFFFFFFF.
        [new Guid("29f6c1db-86da-48c5-9fdb-f2b67b1f44da")] = new Entry
        {
            Presets = TimeoutPresets("Never",
                (300, "5 minutes"), (600, "10 minutes"), (900, "15 minutes"),
                (1800, "30 minutes"), (3600, "1 hour")),
            ExactNames = new[] { "Sleep after" },
            NameHints = new[] { "sleep after" },
        },
        [new Guid("7bc4a2f9-d8fc-4469-b07b-33eb785aaca0")] = new Entry
        {
            Presets = TimeoutPresets("Never",
                (60, "1 minute"), (120, "2 minutes"), (300, "5 minutes"),
                (600, "10 minutes"), (1800, "30 minutes"), (3600, "1 hour")),
            ExactNames = new[] { "System unattended sleep timeout" },
            NameHints = new[] { "system unattended sleep timeout" },
        },
        [new Guid("9d7815a6-7ee4-497e-8888-515a05f02364")] = new Entry
        {
            Presets = TimeoutPresets("Never",
                (600, "10 minutes"), (1800, "30 minutes"), (3600, "1 hour"), (7200, "2 hours")),
            ExactNames = new[] { "Hibernate after" },
            NameHints = new[] { "hibernate after" },
        },
        [new Guid("94ac6d29-73ce-41a6-809f-6363ba21b47e")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Allow hybrid sleep" },
            NameHints = new[] { "allow hybrid sleep" },
        },
        [new Guid("abfc2519-3608-4c2a-94ea-171b0ed546ab")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Allow Standby States" },
            NameHints = new[] { "allow standby states" },
        },
        [new Guid("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d")] = new Entry
        {
            Labels = new[] { "Disable", "Enable" },
            ExactNames = new[] { "Allow wake timers" },
            NameHints = new[] { "allow wake timers" },
        },
        [new Guid("d4c1d4c8-d5cc-43d3-b83e-fc51215cb04d")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Allow sleep with remote opens" },
            NameHints = new[] { "allow sleep with remote opens" },
        },
        [new Guid("a4b195f5-8225-47d8-8012-9d41369786e2")] = new Entry
        {
            Labels = new[] { "No", "Yes" },
            ExactNames = new[] { "Allow system required policy" },
            NameHints = new[] { "allow system required policy" },
        },
        [new Guid("25dfa149-5dd1-4736-b5ab-e8a37b5b8187")] = new Entry
        {
            Labels = new[] { "No", "Yes" },
            ExactNames = new[] { "Allow Away Mode Policy" },
            NameHints = new[] { "allow away mode policy" },
        },
        [new Guid("1a34bdc3-7e6b-442e-a9d0-64b6ef378e84")] = new Entry
        {
            Labels = new[] { "Disable", "Enable" },
            ExactNames = new[] { "Legacy RTC mitigations" },
            NameHints = new[] { "legacy rtc mitigations" },
        },

        // =====================================================================
        // Subgroup: Power buttons and lid  (shared "what should happen" list)
        // =====================================================================

        [new Guid("5ca83367-6e45-459f-a27b-476b1d01c936")] = new Entry
        {
            Labels = new[] { "Do nothing", "Sleep", "Hibernate", "Shut down" },
            ExactNames = new[] { "Lid close action" },
            NameHints = new[] { "lid close action" },
        },
        [new Guid("96996bc0-ad50-47ec-923b-6f41874dd9eb")] = new Entry
        {
            Labels = new[] { "Do nothing", "Sleep", "Hibernate", "Shut down" },
            ExactNames = new[] { "Sleep button action" },
            NameHints = new[] { "sleep button action" },
        },
        [new Guid("7648efa3-dd9c-4e3e-b566-50f929386280")] = new Entry
        {
            Labels = new[] { "Do nothing", "Sleep", "Hibernate", "Shut down" },
            ExactNames = new[] { "Power button action" },
            NameHints = new[] { "power button action" },
        },
        [new Guid("99ff10e7-23b1-4c07-a9d1-5c3206d741b4")] = new Entry
        {
            Labels = new[] { "Do nothing", "Turn on the display" },
            ExactNames = new[] { "Lid open action" },
            NameHints = new[] { "lid open action" },
        },
        [new Guid("a7066653-8d6c-40a8-910e-a1f54b84c7e5")] = new Entry
        {
            Labels = new[] { "Sleep", "Hibernate", "Shut down", "Lock", "Sign out" },
            ExactNames = new[] { "Start menu power button" },
            NameHints = new[] { "start menu power button" },
        },
        [new Guid("833a6b62-dfa4-46d1-82f8-e09e34d029d6")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Enable forced button/lid shutdown" },
            NameHints = new[] { "enable forced button/lid shutdown" },
        },

        // =====================================================================
        // Subgroup: Display  (timeouts in SECONDS, ValueMax 0xFFFFFFFF)
        // =====================================================================

        [new Guid("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e")] = new Entry
        {
            Presets = TimeoutPresets("Never",
                (60, "1 minute"), (120, "2 minutes"), (300, "5 minutes"),
                (600, "10 minutes"), (900, "15 minutes"), (1800, "30 minutes"), (3600, "1 hour")),
            ExactNames = new[] { "Turn off display after" },
            NameHints = new[] { "turn off display after" },
        },
        [new Guid("17aaa29b-8b43-4b94-aafe-35f64daaf1ee")] = new Entry
        {
            Presets = TimeoutPresets("Never",
                (60, "1 minute"), (120, "2 minutes"), (300, "5 minutes"),
                (585, "9 minutes 45 seconds"),
                (600, "10 minutes"), (900, "15 minutes"), (1800, "30 minutes")),
            ExactNames = new[] { "Dim display after" },
            NameHints = new[] { "dim display after" },
        },
        [new Guid("8ec4b3a5-6868-48c2-be75-4f3044be88a7")] = new Entry
        {
            Presets = TimeoutPresets("Never", (10, "10 seconds"), (30, "30 seconds"), (60, "1 minute")),
            ExactNames = new[] { "Console lock display off timeout" },
            NameHints = new[] { "console lock display off timeout" },
        },
        [new Guid("468fe7e5-1158-46ec-88bc-5b96c9e44fd0")] = new Entry
        {
            Presets = TimeoutPresets("Never", (5, "5 seconds"), (30, "30 seconds"), (60, "1 minute"), (300, "5 minutes")),
            ExactNames = new[] { "Standby Reserve Time" },
            NameHints = new[] { "standby reserve time" },
        },
        // Percent (ValueMax 100).
        [new Guid("aded5e82-b909-4619-9949-f5d71dac0bcb")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Display brightness" },
            NameHints = new[] { "display brightness" },
        },
        [new Guid("f1fbfde2-a960-4165-9f88-50667911ce96")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Dimmed display brightness" },
            NameHints = new[] { "dimmed display brightness" },
        },
        [new Guid("fbd9aa66-9553-4097-ba44-ed6e9d65eab8")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Enable adaptive brightness" },
            NameHints = new[] { "enable adaptive brightness" },
        },
        [new Guid("a9ceb8da-cd46-44fb-a98b-02af69de4623")] = new Entry
        {
            Labels = new[] { "No", "Yes" },
            ExactNames = new[] { "Allow display required policy" },
            NameHints = new[] { "allow display required policy" },
        },

        // =====================================================================
        // Subgroup: Desktop background settings / Internet Explorer
        // =====================================================================

        [new Guid("309dce9b-bef4-4119-9921-a851fb12f0f4")] = new Entry
        {
            Labels = new[] { "Available", "Disabled" },
            ExactNames = new[] { "Slide show" },
            NameHints = new[] { "slide show" },
        },
        [new Guid("4c793e7d-a264-42e1-87d3-7a0d2f523ccd")] = new Entry
        {
            Labels = new[] { "Maximum Power Savings", "Maximum Performance" },
            ExactNames = new[] { "JavaScript Timer Frequency" },
            NameHints = new[] { "javascript timer frequency" },
        },

        // =====================================================================
        // Subgroup: Wireless Adapter Settings
        // =====================================================================

        [new Guid("12bbebe6-58d6-4636-95bb-3217ef867c1a")] = new Entry
        {
            Labels = new[] { "Maximum Performance", "Balanced", "Power Saver" },
            ExactNames = new[] { "Power Saving Mode" },
            NameHints = new[] { "power saving mode" },
        },
        // Enum. VERIFIED against powrprof.dll RT_STRING 604-610, which
        // describes the ASPM policy for idle links: an option that attempts
        // the L1 state when the link is idle is labelled "Maximum power
        // savings", and there is also a "Batteries" option. So the real
        // Windows set is Off / Batteries / Maximum power savings, and the
        // earlier "Off / On / Maximum Power Savings" put the middle option in
        // the wrong slot - a laptop on battery would have been told it was
        // "On" instead of "Batteries".
        [new Guid("ee12f906-d277-404b-b6da-e5fa1a576df5")] = new Entry
        {
            Labels = new[] { "Off", "Batteries", "Maximum power savings" },
            ExactNames = new[] { "Link State Power Management" },
            NameHints = new[] { "link state power management" },
        },

        // =====================================================================
        // Subgroup: Idle Resiliency
        // =====================================================================

        [new Guid("3166bc41-7e98-4e03-b34e-ec0f5f2b218e")] = new Entry
        {
            // Default on this machine is 0xFFFFFFFF - literally "4294967295"
            // in the UI, which is exactly the confusion being fixed here.
            Presets = NeverIsMaxTimeoutPresets(
                (1, "1 second"), (5, "5 seconds"), (10, "10 seconds"), (30, "30 seconds")),
            ExactNames = new[] { "Execution Required power request timeout" },
            NameHints = new[] { "execution required power request timeout" },
        },
        [new Guid("c36f0eb4-2988-4a70-8eee-0884fc2c2433")] = new Entry
        {
            Presets = Preset(
                (0u, "Off"),
                (1u, "0.5 milliseconds"), (2u, "1 millisecond"), (5u, "2.5 milliseconds"), (10u, "5 milliseconds")),
            ExactNames = new[] { "IO coalescing timeout" },
            NameHints = new[] { "io coalescing timeout" },
        },
        [new Guid("c42b79aa-aa3a-484b-a98f-2cf32aa90a28")] = new Entry
        {
            Presets = MillisecondTiers((1, "1 millisecond"), (5, "5 milliseconds"), (10, "10 milliseconds")),
            ExactNames = new[] { "Processor Idle Resiliency Timer Resolution" },
            NameHints = new[] { "processor idle resiliency timer resolution" },
        },

        // =====================================================================
        // Subgroup: Interrupt Steering Settings
        // =====================================================================

        // Enum. VERIFIED against powrprof.dll RT_STRING 1114-1119: the three
        // options are Default, "Route interrupts to Processor 0" (label
        // "Processor 0") and "Route interrupts to any processor". The earlier
        // two-item "Default / Lock Interrupt Routing" list was wrong: it
        // invented the second label and dropped the third option entirely.
        // "Lock Interrupt Routing" is a real string in the table (1130/1131)
        // but belongs to a different setting.
        [new Guid("2bfc24f9-5ea2-4801-8213-3dbae01aa39d")] = new Entry
        {
            Labels = new[] { "Default", "Processor 0 - route interrupts to Processor 0", "Any processor" },
            ExactNames = new[] { "Interrupt Steering Mode" },
            NameHints = new[] { "interrupt steering mode" },
        },
        [new Guid("d502f7ee-1dc7-4efd-a55d-f04b6f5c0545")] = new Entry
        {
            Labels = new[] { "Deep Sleep Disabled", "Deep Sleep Enabled" },
            ExactNames = new[] { "Deep Sleep Enabled/Disabled" },
            NameHints = new[] { "deep sleep" },
        },

        // =====================================================================
        // Subgroup: Processor
        // =====================================================================

        [new Guid("51dea550-bb38-4bc4-991b-eacf37be5ec8")] = new Entry
        {
            // Measured: ValueMin 0, ValueMax 100 - this is a PERCENTAGE, not a
            // 3-value enumeration, so the AC value read back as 100. Treating it
            // as an enum offered "Balanced" for a value Windows never sends.
            Presets = PercentTiers("0% - Off"),
            ExactNames = new[] { "Maximum Power Level" },
            NameHints = new[] { "maximum power level" },
        },
        [new Guid("73cde64d-d720-4bb2-a860-c755afe77ef2")] = new Entry
        {
            Presets = Preset((0u, "Off"), (6u, "0.6%"), (20u, "2%"), (50u, "5%"), (100u, "10%")),
            ExactNames = new[] { "Target Load" },
            NameHints = new[] { "target load" },
        },
        [new Guid("d6ba4903-386f-4c2c-8adb-5c21b3328d25")] = new Entry
        {
            Presets = MillisecondTiers((1, "1 millisecond"), (10, "10 milliseconds"), (100, "100 milliseconds")),
            ExactNames = new[] { "Unparked time trigger" },
            NameHints = new[] { "unparked time trigger" },
        },

        // Minimum / maximum processor state, percent, one GUID per efficiency
        // class. The trailing hex digit is the class: ...4c class 0, ...4d
        // class 1, ...4e class 2. Class 0 is the main package.
        [new Guid("893dee8e-2bef-41e0-89c6-b55d0929964c")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Minimum processor state" },
            NameHints = new[] { "minimum processor state" },
        },
        [new Guid("893dee8e-2bef-41e0-89c6-b55d0929964d")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Minimum processor state for Processor Power Efficiency Class 1" },
        },
        [new Guid("893dee8e-2bef-41e0-89c6-b55d0929964e")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Minimum processor state for Processor Power Efficiency Class 2" },
        },
        [new Guid("bc5038f7-23e0-4960-96da-33abaf5935ec")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Maximum processor state" },
            NameHints = new[] { "maximum processor state" },
        },
        [new Guid("bc5038f7-23e0-4960-96da-33abaf5935ed")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Maximum processor state for Processor Power Efficiency Class 1" },
        },
        [new Guid("bc5038f7-23e0-4960-96da-33abaf5935ee")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Maximum processor state for Processor Power Efficiency Class 2" },
        },

        // Enum. VERIFIED against the Windows string table
        // (powrprof.dll RT_STRING 370/371/372/373, reached from the registry
        // description "@...powrprof.dll,-370"): 0 = "Passive" (slow the
        // processor before increasing fan speed), 1 = "Active" (increase fan
        // speed before slowing the processor). An earlier hand-written list
        // offered Maximum processor state / Boost mode / Balanced / Cool and
        // quiet, which Windows does not use for this setting at all.
        [new Guid("94d3a615-a899-4ac5-ae2b-e4d8f634367f")] = new Entry
        {
            Labels = new[] { "Passive - slow the processor before increasing fan speed",
                             "Active - increase fan speed before slowing the processor" },
            ExactNames = new[] { "System cooling policy" },
            NameHints = new[] { "system cooling policy" },
        },
        // Enum. VERIFIED against powrprof.dll RT_STRING 729-748. The option
        // list alternates description-then-label, and the labels in order are:
        // Disabled, Enabled, Aggressive, Efficient Enabled, Efficient
        // Aggressive, Automatic, Aggressive At Guaranteed, Efficient
        // Aggressive At Guaranteed, IdealAggressive. That is NINE options -
        // the earlier seven-item list was both the wrong length and the wrong
        // order, and would have mislabelled every option from index 3 up.
        [new Guid("be337238-0d82-4146-a960-4f3749d470c7")] = new Entry
        {
            Labels = new[]
            {
                "Disabled",
                "Enabled",
                "Aggressive",
                "Efficient Enabled",
                "Efficient Aggressive",
                "Automatic",
                "Aggressive At Guaranteed",
                "Efficient Aggressive At Guaranteed",
                "Ideal Aggressive",
            },
            ExactNames = new[] { "Processor performance boost mode" },
            NameHints = new[] { "processor performance boost mode" },
        },
        // Percent (0..100), one GUID per efficiency class. 0 = Performance,
        // 32 = Balance Power, 33 = Balance Performance, 34 = Power.
        [new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6863")] = new Entry
        {
            Presets = Preset(
                (0u, "Performance"), (32u, "Balanced power saving"),
                (33u, "Balanced performance"), (34u, "Power saving")),
            ExactNames = new[] { "Processor energy performance preference policy" },
            NameHints = new[] { "processor energy performance preference policy" },
        },
        [new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6864")] = new Entry
        {
            Presets = Preset(
                (0u, "Performance"), (32u, "Balanced power saving"),
                (33u, "Balanced performance"), (34u, "Power saving")),
            ExactNames = new[] { "Processor energy performance preference policy for Processor Power Efficiency Class 1" },
        },
        [new Guid("36687f9e-e3a5-4dbf-b1dc-15eb381c6865")] = new Entry
        {
            Presets = Preset(
                (0u, "Performance"), (32u, "Balanced power saving"),
                (33u, "Balanced performance"), (34u, "Power saving")),
            ExactNames = new[] { "Processor energy performance preference policy for Processor Power Efficiency Class 2" },
        },
        [new Guid("45bcc044-d885-43e2-8605-ee0ec6e96b59")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Processor performance boost policy" },
            NameHints = new[] { "processor performance boost policy" },
        },
        // Enum. VERIFIED against powrprof.dll RT_STRING 817/818.
        [new Guid("4e4450b3-6179-4e91-b8f1-5bb9938f81a1")] = new Entry
        {
            Labels = new[] { "Disallow", "Allow" },
            ExactNames = new[] { "Processor duty cycling" },
            NameHints = new[] { "processor duty cycling" },
        },
        [new Guid("5d76a2ca-e8c0-402f-a133-2158492d58ad")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Processor idle disable" },
            NameHints = new[] { "processor idle disable" },
        },
        [new Guid("8baa4a8a-14c6-4451-8e8b-14bdbd197537")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Processor performance autonomous mode" },
            NameHints = new[] { "processor performance autonomous mode" },
        },
        // REMOVED: "Allow Throttle States" (3b04d4fd-...).
        //
        // The audit found no option list for this setting anywhere in
        // powrprof.dll's string table - the table jumps straight from the
        // description (378) to the friendly name (379) and on to a different
        // setting. An earlier "On / Off / Manual" list here was invented, and
        // this machine's value for the setting is 2, which no published list
        // accounts for. Leaving it unlabelled is the honest outcome: it still
        // shows its raw number rather than three confident wrong words.
        // Enum. VERIFIED against powrprof.dll RT_STRING 1726-1731: these are
        // three-way policies, not on/off switches. The earlier "Off / On" pairs
        // were invented.
        [new Guid("b0deaf6b-59c0-4523-8a45-ca7f40244114")] = new Entry
        {
            Labels = new[] { "Disabled", "Round robin", "Sequential" },
            ExactNames = new[] { "Module unpark policy" },
            NameHints = new[] { "module unpark policy" },
        },
        [new Guid("b669a5e9-7b1d-4132-baaa-49190abcfeb6")] = new Entry
        {
            Labels = new[] { "Disabled", "Round robin", "Sequential" },
            ExactNames = new[] { "Complex unpark policy" },
            NameHints = new[] { "complex unpark policy" },
        },
        // Two-way (powrprof.dll 1739-1742): unpark the whole core as a group,
        // or one core per thread.
        [new Guid("b28a6829-c5f7-444e-8f61-10e24e85c532")] = new Entry
        {
            Labels = new[] { "Core", "Core per Thread" },
            ExactNames = new[] { "Smt threads unpark policy" },
            NameHints = new[] { "smt threads unpark policy" },
        },

        // Performance increase / decrease thresholds, percent.
        [new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35d")] = new Entry
        {
            Presets = PercentTiers("Never"),
            ExactNames = new[] { "Processor performance increase threshold" },
        },
        [new Guid("06cadf0e-64ed-448a-8927-ce7bf90eb35e")] = new Entry
        {
            Presets = PercentTiers("Never"),
            ExactNames = new[] { "Processor performance increase threshold for Processor Power Efficiency Class 1" },
        },
        [new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a6")] = new Entry
        {
            Presets = PercentTiers("Never"),
            ExactNames = new[] { "Processor performance decrease threshold" },
        },
        [new Guid("12a0ab44-fe28-4fa9-b3bd-4b64f44960a7")] = new Entry
        {
            Presets = PercentTiers("Never"),
            ExactNames = new[] { "Processor performance decrease threshold for Processor Power Efficiency Class 1" },
        },
        // Core parking, percent (ValueMax 100). 0 = no cores parked.
        [new Guid("0cc5b647-c1df-4637-891a-dec35c318583")] = new Entry
        {
            Presets = CoreTiers("0% - all cores unparked"),
            ExactNames = new[] { "Processor performance core parking min cores" },
        },
        [new Guid("0cc5b647-c1df-4637-891a-dec35c318584")] = new Entry
        {
            Presets = CoreTiers("0% - all cores unparked"),
            ExactNames = new[] { "Processor performance core parking min cores for Processor Power Efficiency Class 1" },
        },
        [new Guid("ea062031-0e34-4ff1-9b6d-eb1059334028")] = new Entry
        {
            Presets = CoreTiers("0% - no core parking limit"),
            ExactNames = new[] { "Processor performance core parking max cores" },
        },
        [new Guid("ea062031-0e34-4ff1-9b6d-eb1059334029")] = new Entry
        {
            Presets = CoreTiers("0% - no core parking limit"),
            ExactNames = new[] { "Processor performance core parking max cores for Processor Power Efficiency Class 1" },
        },

        // Millisecond timers (ValueMin 1, ValueMax 100) used by the
        // performance governor and the core parker.
        [new Guid("984cf492-3bed-4488-a8f9-4286c97bf5aa")] = new Entry
        {
            Presets = MillisecondTiers((1, "1 millisecond - fastest response"), (10, "10 milliseconds"), (100, "100 milliseconds - laziest")),
            ExactNames = new[] { "Processor performance increase time" },
        },
        [new Guid("984cf492-3bed-4488-a8f9-4286c97bf5ab")] = new Entry
        {
            Presets = MillisecondTiers((1, "1 millisecond - fastest response"), (10, "10 milliseconds"), (100, "100 milliseconds - laziest")),
            ExactNames = new[] { "Processor performance increase time for Processor Power Efficiency Class 1" },
        },
        [new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c8")] = new Entry
        {
            Presets = MillisecondTiers((1, "1 millisecond - fastest response"), (2, "2 milliseconds"), (3, "3 milliseconds"), (10, "10 milliseconds"), (100, "100 milliseconds - laziest")),
            ExactNames = new[] { "Processor performance decrease time" },
        },
        [new Guid("d8edeb9b-95cf-4f95-a73c-b061973693c9")] = new Entry
        {
            Presets = MillisecondTiers((1, "1 millisecond - fastest response"), (2, "2 milliseconds"), (3, "3 milliseconds"), (10, "10 milliseconds"), (100, "100 milliseconds - laziest")),
            ExactNames = new[] { "Processor performance decrease time for Processor Power Efficiency Class 1" },
        },
        [new Guid("2ddd5a84-5a71-437e-912a-db0b8c788732")] = new Entry
        {
            Presets = MillisecondTiers((1, "1 millisecond - park immediately"), (10, "10 milliseconds"), (100, "100 milliseconds - park slowest")),
            ExactNames = new[] { "Processor performance core parking increase time" },
        },
        [new Guid("dfd10d17-d5eb-45dd-877a-9a34ddd15c82")] = new Entry
        {
            Presets = MillisecondTiers((1, "1 millisecond - unpark immediately"), (2, "2 milliseconds"), (3, "3 milliseconds"), (10, "10 milliseconds"), (100, "100 milliseconds - unpark slowest")),
            ExactNames = new[] { "Processor performance core parking decrease time" },
        },
        [new Guid("4bdaf4e9-d103-46d7-a5f0-6280121616ef")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Processor performance core parking distribution threshold" },
        },
        [new Guid("2430ab6f-a520-44a2-9601-f7f23b5134b1")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Processor performance core parking concurrency threshold" },
        },
        [new Guid("f735a673-2066-4f80-a0c5-ddee0cf1bf5d")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Processor performance core parking concurrency headroom threshold" },
        },
        [new Guid("943c8cb6-6f93-4227-ad87-e9a3feec08d1")] = new Entry
        {
            // ValueMin is 5, not 0, so 0 is not a legal value here.
            Presets = Preset(
                (5u, "5% - earliest"), (25u, "25%"), (50u, "50% - balanced"),
                (75u, "75%"), (85u, "85%"), (100u, "100% - latest")),
            ExactNames = new[] { "Processor performance core parking overutilization threshold" },
        },
        [new Guid("97cfac41-2217-47eb-992d-618b1977c907")] = new Entry
        {
            Presets = TimeoutPresets("Off", (1, "1 millisecond"), (10, "10 milliseconds"), (100, "100 milliseconds"), (1000, "1 second")),
            ExactNames = new[] { "Processor performance core parking soft park latency" },
        },
        [new Guid("4b92d758-5a24-4851-a470-815d78aee119")] = new Entry
        {
            Presets = PercentTiers("Never"),
            ExactNames = new[] { "Processor idle demote threshold" },
        },
        [new Guid("7b224883-b3cc-4d79-819f-8374152cbe7c")] = new Entry
        {
            Presets = PercentTiers("Never"),
            ExactNames = new[] { "Processor idle promote threshold" },
        },
        [new Guid("1a98ad09-af22-42ca-8e61-f0a5802c270a")] = new Entry
        {
            Presets = CoreTiers("0 - no restriction"),
            ExactNames = new[] { "Processor Restriction Count" },
        },
        [new Guid("1facfc65-a930-4bc5-9f38-504ec097bbc0")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Initial performance for Processor Power Efficiency Class 1 when unparked" },
        },
        [new Guid("4d2b0152-7d5c-498b-88e2-34345392a2c5")] = new Entry
        {
            Presets = MillisecondTiers((10, "10 milliseconds"), (30, "30 milliseconds"), (100, "100 milliseconds"), (1000, "1 second")),
            ExactNames = new[] { "Processor performance time check interval" },
        },
        [new Guid("c4581c31-89ab-4597-8e2b-9c9cab440e6b")] = new Entry
        {
            Presets = MillisecondTiers((1000, "1 second"), (10000, "10 seconds"), (50000, "50 seconds"), (100000, "100 seconds")),
            ExactNames = new[] { "Processor idle time check" },
        },
        [new Guid("cfeda3d0-7697-4566-a922-a9086cd49dfa")] = new Entry
        {
            Presets = MillisecondTiers((1000, "1 second"), (10000, "10 seconds"), (30000, "30 seconds"), (60000, "1 minute")),
            ExactNames = new[] { "Processor autonomous activity window" },
        },
        [new Guid("d92998c2-6a48-49ca-85d4-8cceec294570")] = new Entry
        {
            Presets = TimeoutPresets("Disabled", (1000, "1 second"), (10000, "10 seconds"), (60000, "1 minute")),
            ExactNames = new[] { "Short vs. long running thread threshold" },
        },
        [new Guid("616cdaa5-695e-4545-97ad-97dc2d1bdd88")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Latency sensitivity hint min unparked cores/packages" },
        },
        [new Guid("616cdaa5-695e-4545-97ad-97dc2d1bdd89")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Latency sensitivity hint min unparked cores/packages for Processor Power Efficiency Class 1" },
        },
        [new Guid("619b7505-003b-4e82-b7a6-4dd29c300971")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Latency sensitivity hint processor performance" },
        },
        [new Guid("4b70f900-cdd9-4e66-aa26-ae8417f98173")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Latency sensitivity hint processor energy performance preference" },
        },

        // =====================================================================
        // Subgroup: Battery / energy saver  (percent thresholds)
        // =====================================================================

        [new Guid("e69653ca-cf7f-4f05-aa73-cb833fa90ad4")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Charge level" },
        },
        [new Guid("9a66d8d7-4ff7-4ef9-b5a2-5a326ca2a469")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Critical battery level" },
        },
        [new Guid("8183ba9a-e910-48da-8769-14ae6dc1170a")] = new Entry
        {
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Low battery level" },
        },
        [new Guid("f3c5027d-cd16-4930-aa6b-90db844a8f00")] = new Entry
        {
            // Windows defaults this to 7%, so 7 has to be a real option or the
            // shipped value falls through to "Custom value (7)".
            Presets = Preset(
                (0u, "Off"), (5u, "5% - Very low"), (7u, "7% - Default reserve"),
                (10u, "10% - Low"), (15u, "15%"), (20u, "20% - Moderate"), (100u, "100% - Maximum")),
            ExactNames = new[] { "Reserve battery level" },
        },
        [new Guid("5dbb7c9f-38e9-40d2-9749-4f8a0e9f640f")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Critical battery notification" },
        },
        [new Guid("bcded951-187b-4d05-bccc-f7e51960c258")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Low battery notification" },
        },
        [new Guid("637ea02f-bbcb-4015-8e2c-a1c7b9c0b546")] = new Entry
        {
            Labels = new[] { "Do nothing", "Hibernate", "Shut down" },
            ExactNames = new[] { "Critical battery action" },
        },
        [new Guid("d8742dcb-3e6a-4b3c-b3fe-374623cdcf06")] = new Entry
        {
            Labels = new[] { "Do nothing", "Hibernate", "Shut down" },
            ExactNames = new[] { "Low battery action" },
        },
        // Enum. VERIFIED against powrprof.dll RT_STRING 1298/1299 and
        // 1300/1301: 0 = "User" (engage Energy Saver based on user settings),
        // 1 = "Aggressive". Not an on/off switch.
        [new Guid("5c5bb349-ad29-4ee2-9d0b-2b25270f7a81")] = new Entry
        {
            Labels = new[] { "User", "Aggressive" },
            ExactNames = new[] { "Energy Saver Policy" },
        },

        // =====================================================================
        // Subgroup: Video playback / console / overlay / adaptive display
        // =====================================================================

        [new Guid("10778347-1370-4ee0-8bbd-33bdacaade49")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Video playback quality bias" },
        },
        [new Guid("03680956-93bc-4294-bba6-4e0f09bb717f")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "When sharing media" },
        },
        [new Guid("34c7b99f-9a6d-4b3c-8dc7-b6693b78cef4")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "When playing video" },
        },
        [new Guid("684c3e69-a4f7-4014-8754-d45179a56167")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Advanced Color quality bias" },
        },
        [new Guid("90959d22-d6a1-49b9-af93-bce885ad335b")] = new Entry
        {
            Labels = new[] { "Off", "On" },
            ExactNames = new[] { "Adaptive display" },
        },
        [new Guid("13d09884-f74e-474a-a852-b6bde8ad03a8")] = new Entry
        {
            // Percent (0..100). Distinct setting from "Display brightness":
            // this one weights how much of the adaptive-dimming curve applies.
            Presets = PercentTiers("Off"),
            ExactNames = new[] { "Display brightness weight" },
        },
        // Enum. VERIFIED against powrprof.dll RT_STRING 1604-1607. Windows'
        // options are "No preference" (label "None") and "Prefer low-power
        // GPU" (label "Low Power") - there is no "Maximum Performance" option
        // and no index 2. The earlier Maximum Power Savings / Maximum
        // Performance pair was wrong on both count and wording.
        [new Guid("dd848b2a-8a5d-4451-9ae2-39cd41658f6c")] = new Entry
        {
            Labels = new[] { "None", "Low Power" },
            ExactNames = new[] { "GPU preference policy" },
        },
        [new Guid("dab60367-53fe-4fbc-825e-521d069d2456")] = new Entry
        {
            // Milliseconds (ValueMax 300000). This is the "Partial -> Slumber"
            // dwell time, i.e. how long the link may idle before it downshifts.
            Presets = TimeoutPresets("Off",
                (5, "5 milliseconds"), (50, "50 milliseconds"), (100, "100 milliseconds"),
                (500, "0.5 seconds"), (1000, "1 second"), (5000, "5 seconds")),
            ExactNames = new[] { "AHCI Link Power Management - Adaptive" },
        },

        // =====================================================================
        // Deliberately NOT catalogued.
        //
        // The remaining 65 enumerated settings of the reference machine fall
        // into these groups, and none of them get a word list:
        //
        // 1. Opaque bitfields. "Hetero containment policy.", "Heterogeneous
        //    policy in effect", "Processor idle threshold scaling",
        //    "Processor performance core parking utility distribution" and
        //    "Processor performance core parking parked performance state" are
        //    read back as values like 1 and 5 out of undocumented ranges.
        //    Any label would be invented, not known.
        // 2. Per-efficiency-class duplicates whose GUID differs only in the last
        //    hex digit ("...processor performance increase time" Class 1 vs 2).
        //    Their GUIDs are recorded, but labelling them adds dozens of
        //    near-identical dropdowns a user cannot act on - the Class 0
        //    sibling already covers the intent.
        // 3. Human Presence Sensor and Standby Budget internals
        //    ("Standby Budget Grace Period", "Standby Reserve Grace Period",
        //    the four HPS timeouts). These only exist on sensor laptops; on a
        //    desktop they are unreachable, and their units are not documented.
        //
        // Every one of them still shows its raw number, and the number is
        // honest - it just is not friendly. A wrong friendly word would be
        // actively misleading, which is the failure this whole file exists
        // to avoid.
        // =====================================================================
    };

    /// <summary>
    /// Finds the catalog entry for a setting. GUID first, then the display name.
    ///
    /// WHY THE NAME MATCH IS A WHOLE-NAME MATCH AND NOT A SUBSTRING. An earlier
    /// version matched hints with Contains(), and that silently mislabelled
    /// real settings: the hint "display brightness" also matched "Display
    /// brightness weight", so the weight setting was handed the brightness
    /// setting's percent tiers. Four more sibling pairs collided the same way
    /// ("maximum processor state" vs its "Processor Power Efficiency Class 1"
    /// variant, and so on). Attaching a confident-sounding word list to the
    /// WRONG setting is far worse than showing a number, because the user has
    /// no way to tell the words are wrong. A whole-name match cannot drift onto
    /// a sibling, and a name that matches two entries is rejected outright
    /// rather than guessed.
    ///
    /// The name path only matters on a machine that publishes a setting GUID
    /// not in <see cref="ByGuid"/>; all 174 settings of the reference machine
    /// were measured and are keyed by GUID, which is also immune to the
    /// display language changing.
    /// </summary>
    public static bool TryGet(Guid settingId, string? settingName, out Entry entry)
    {
        if (ByGuid.TryGetValue(settingId, out var hit)) { entry = hit; return true; }

        if (!string.IsNullOrWhiteSpace(settingName))
        {
            string name = settingName.Trim();

            Entry? found = null;
            bool conflict = false;
            foreach (var candidate in ByGuid.Values)
            {
                bool claims = false;
                foreach (string known in candidate.ExactNames)
                    if (string.Equals(name, known, StringComparison.OrdinalIgnoreCase)) { claims = true; break; }
                if (!claims)
                    foreach (string hint in candidate.NameHints)
                        if (string.Equals(name, hint, StringComparison.OrdinalIgnoreCase)) { claims = true; break; }
                if (!claims) continue;

                if (found is not null && !SameEntry(found, candidate)) conflict = true;
                found = candidate;
            }

            if (!conflict && found is not null) { entry = found; return true; }
        }

        entry = null!;
        return false;
    }

    private static bool SameEntry(Entry a, Entry b) => ReferenceEquals(a, b);
}
