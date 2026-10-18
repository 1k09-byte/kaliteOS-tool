using System.Collections.Generic;

namespace kaliteConfig.Services.Personalize;

public sealed record MouseOptions(int PointerSpeed, bool EnhancedPrecision, int Trails, int WheelLines);

public sealed record SchemeScan(IReadOnlyDictionary<string, string> Mapping, IReadOnlyList<string> Skipped);

public interface ICursorService
{
    IReadOnlyList<string> RoleNames { get; }
    IReadOnlyDictionary<string, string> CurrentScheme();
    IReadOnlyDictionary<string, string> WindowsDefaultScheme();
    int CurrentPointerSize();
    MouseOptions CurrentMouse();
    int CurrentCaretWidth();

    (string? Path, string? Error) ImportCursorFile(string sourcePath);
    SchemeScan ScanSchemeFolder(string folder);
    IReadOnlyList<CursorPack> Packs();

    IReadOnlyList<SettingResult> ApplyScheme(IReadOnlyDictionary<string, string> roleToPath);
    IReadOnlyList<SettingResult> ApplyPointerSize(int size);
    IReadOnlyList<SettingResult> ApplyMouse(MouseOptions options);
    IReadOnlyList<SettingResult> ApplyCaretWidth(int width);

    IReadOnlyList<SettingResult> RevertSection();
    IReadOnlyList<SettingResult> ResetToWindowsDefaults();
}
