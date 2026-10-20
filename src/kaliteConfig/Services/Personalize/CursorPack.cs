using System;
namespace kaliteConfig.Services.Personalize;

public sealed record CursorPack(
    string Name,
    string FolderPath,
    string Source,
    int FileCount)
{
    public bool IsBundled => string.Equals(Source, "Bundled", StringComparison.Ordinal);
}
