using System;
using System.Runtime.InteropServices;

namespace kaliteConfig.Services;

/// <summary>
/// Win32 common file dialog fallback (IFileDialog / IShellItem COM).
///
/// WHY THIS EXISTS: this app runs elevated (requireAdministrator manifest).
/// The brokered WinUI pickers (<c>FileOpenPicker</c>/<c>FileSavePicker</c>)
/// throw E_ACCESSDENIED in elevated processes (a long-standing WinUI 3 issue),
/// so callers try the WinUI picker first and fall back to these Win32 dialogs
/// - which do work elevated - when that happens.
/// </summary>
internal static class Win32FilePicker
{
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint FOS_FILEMUSTEXIST = 0x1;
    private const uint FOS_OVERWRITEPROMPT = 0x2;
    private const uint FOS_FORCEFILESYSTEM = 0x40;
    private const uint FOS_PATHMUSTEXIST = 0x800;
    private const int HR_CANCELLED = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    // {DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7} FileOpenDialog
    [ComImport]
    [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]
    private sealed class FileOpenDialogClass { }

    // {C0B4E2F3-BA21-4773-8DBA-335EC946EB8B} FileSaveDialog
    [ComImport]
    [Guid("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B")]
    private sealed class FileSaveDialogClass { }

    // Full IFileDialog vtable (order matters - this is the COM contract).
    [ComImport]
    [Guid("42F85136-DB7E-439C-85F1-E4075D135FC8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialog
    {
        [PreserveSig] int Show(nint hwndOwner);
        [PreserveSig] int SetFileTypes(uint cFileTypes, nint rgFilterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(nint pfde, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        [PreserveSig] int SetDefaultFolder(nint psi);
        [PreserveSig] int SetFolder(nint psi);
        [PreserveSig] int GetFolder(out nint ppsi);
        [PreserveSig] int GetCurrentSelection(out nint ppsi);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetFileName(out nint pszName);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] int SetResult(nint psi);
        [PreserveSig] int GetResult(out IShellItem ppsi);
        [PreserveSig] int AddPlace(nint psi, int fdap);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int SetClientGuid(in Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(nint pFilter);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint pbc, in Guid bhid, in Guid riid, out nint ppv);
        [PreserveSig] int GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out nint ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttributes);
        [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FilterSpec
    {
        public readonly nint Name;
        public readonly nint Spec;
        public FilterSpec(nint name, nint spec) { Name = name; Spec = spec; }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    public static string? PickOpenFile(nint owner)
    {
        return Show(new FileOpenDialogClass(), isOpen: true, owner,
            new[] { ("SCEWIN dump (*.txt)", "*.txt"), ("All files (*.*)", "*.*") },
            suggestedName: null, title: "Open SCEWIN dump");
    }

    /// <summary>Open dialog with caller-defined filters - used by the
    /// per-game profile binding picker (elevated-safe fallback path).</summary>
    public static string? PickOpenFile(
        nint owner,
        (string Name, string Spec)[] filters,
        string title)
    {
        return Show(new FileOpenDialogClass(), isOpen: true, owner, filters,
            suggestedName: null, title: title);
    }

    public static string? PickSaveFile(nint owner, string suggestedName)
    {
        return PickSaveFile(owner, suggestedName,
            new[] { ("Text file (*.txt)", "*.txt") },
            title: "Export modified dump", defaultExtension: "txt");
    }

    /// <summary>Save dialog with caller-defined filters - used by the
    /// overclock verification-record export (elevated-safe fallback path).</summary>
    public static string? PickSaveFile(
        nint owner, string suggestedName,
        (string Name, string Spec)[] filters,
        string title, string defaultExtension)
    {
        return Show(new FileSaveDialogClass(), isOpen: false, owner, filters,
            suggestedName: suggestedName, title: title, defaultExtension: defaultExtension);
    }

    private static string? Show(
        object dialogClass, bool isOpen, nint owner,
        (string Name, string Spec)[] filters, string? suggestedName, string title,
        string defaultExtension = "txt")
    {
        var dialog = (IFileDialog)dialogClass;
        nint specArray = nint.Zero;
        var namePtrs = new nint[filters.Length];
        var specPtrs = new nint[filters.Length];
        try
        {
            dialog.GetOptions(out var fos);
            fos |= FOS_FORCEFILESYSTEM |
                   (isOpen ? FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST : FOS_OVERWRITEPROMPT);
            dialog.SetOptions(fos);
            dialog.SetTitle(title);
            if (suggestedName is { Length: > 0 }) dialog.SetFileName(suggestedName);
            if (!isOpen) dialog.SetDefaultExtension(defaultExtension);

            int size = Marshal.SizeOf<FilterSpec>();
            specArray = Marshal.AllocCoTaskMem(size * filters.Length);
            for (int i = 0; i < filters.Length; i++)
            {
                namePtrs[i] = Marshal.StringToCoTaskMemUni(filters[i].Name);
                specPtrs[i] = Marshal.StringToCoTaskMemUni(filters[i].Spec);
                Marshal.StructureToPtr(new FilterSpec(namePtrs[i], specPtrs[i]), specArray + i * size, fDeleteOld: false);
            }
            dialog.SetFileTypes((uint)filters.Length, specArray);
            dialog.SetFileTypeIndex(1);

            var hr = dialog.Show(owner != 0 ? owner : GetForegroundWindow());
            if (hr < 0 || hr == HR_CANCELLED) return null;

            dialog.GetResult(out var item);
            try
            {
                item.GetDisplayName(SIGDN_FILESYSPATH, out var pathPtr);
                var path = Marshal.PtrToStringUni(pathPtr);
                Marshal.FreeCoTaskMem(pathPtr);
                return path;
            }
            finally
            {
                _ = Marshal.FinalReleaseComObject(item);
            }
        }
        finally
        {
            if (specArray != nint.Zero) Marshal.FreeCoTaskMem(specArray);
            for (int i = 0; i < filters.Length; i++)
            {
                if (namePtrs[i] != nint.Zero) Marshal.FreeCoTaskMem(namePtrs[i]);
                if (specPtrs[i] != nint.Zero) Marshal.FreeCoTaskMem(specPtrs[i]);
            }
            try { _ = Marshal.FinalReleaseComObject(dialog); } catch { /* best effort */ }
        }
    }
}
