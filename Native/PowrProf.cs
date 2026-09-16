using System;
using System.Runtime.InteropServices;

namespace stellarisKIT.Native
{
    public static class PowrProf
    {
        public const uint ACCESS_SCHEME = 16;
        public const uint ACCESS_SUBGROUP = 17;
        public const uint ACCESS_INDIVIDUAL_SETTING = 18;

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerEnumerate(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            uint AccessFlags,
            uint Index,
            out Guid Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerEnumerate(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid, // IntPtr.Zero for getting subgroups
            uint AccessFlags,
            uint Index,
            out Guid Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerEnumerate(
            IntPtr RootPowerKey,
            IntPtr SchemeGuid, // IntPtr.Zero for getting schemes
            IntPtr SubGroupOfPowerSettingsGuid,
            uint AccessFlags,
            uint Index,
            out Guid Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerReadFriendlyName(
            IntPtr RootPowerKey,
            IntPtr SchemeGuid, // NULL = ask for subgroup/setting name
            IntPtr SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "PowerReadFriendlyName")]
        public static extern uint PowerReadFriendlyNameScheme(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid, // Can be Zero
            IntPtr PowerSettingGuid, // Can be Zero
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        public static extern IntPtr LocalFree(IntPtr hMem);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "PowerReadFriendlyName")]
        public static extern uint PowerReadFriendlyNameSubgroup(
            IntPtr RootPowerKey,
            IntPtr SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "PowerReadFriendlyName")]
        public static extern uint PowerReadFriendlyNameSetting(
            IntPtr RootPowerKey,
            IntPtr SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "PowerReadDescription")]
        public static extern uint PowerReadDescriptionSetting(
            IntPtr RootPowerKey,
            IntPtr SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerReadFriendlyName(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerReadFriendlyName(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "PowerReadDescription")]
        public static extern uint PowerReadDescriptionScheme(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "PowerReadDescription")]
        public static extern uint PowerReadDescriptionSubgroup(
            IntPtr RootPowerKey,
            IntPtr SchemeGuid, // NULL
            ref Guid SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerReadDescription(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern uint PowerGetActiveScheme(
            IntPtr UserRootPowerKey,
            out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern uint PowerSetActiveScheme(
            IntPtr UserRootPowerKey,
            ref Guid SchemeGuid);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerWriteFriendlyName(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            byte[] Buffer,
            uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerWriteDescription(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            IntPtr SubGroupOfPowerSettingsGuid,
            IntPtr PowerSettingGuid,
            byte[] Buffer,
            uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern uint PowerReadACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            out uint AcValueIndex);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern uint PowerReadDCValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            out uint DcValueIndex);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern uint PowerWriteACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            uint AcValueIndex);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode)]
        public static extern uint PowerWriteDCValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            uint DcValueIndex);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerReadPossibleValue(
            IntPtr RootPowerKey,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            out uint Type,
            uint PossibleSettingIndex,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerReadPossibleFriendlyName(
            IntPtr RootPowerKey,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            uint PossibleSettingIndex,
            IntPtr Buffer,
            ref uint BufferSize);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerDuplicateScheme(
            IntPtr RootPowerKey,
            ref Guid SourceSchemeGuid,
            out IntPtr DestinationSchemeGuid);

        [DllImport("powrprof.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern uint PowerDeleteScheme(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid);
    }
}
