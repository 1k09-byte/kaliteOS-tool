using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Models;

public abstract class ScewinSegment { }

public sealed class OtherSegment : ScewinSegment
{
    public required string Raw { get; init; }
}

public sealed class HeaderSegment : ScewinSegment
{
    public required string Raw { get; init; }
    public required string[] Path { get; init; }
}

public sealed class ItemSegment : ScewinSegment
{
    public required BiosSetting Item { get; init; }
}

public sealed class ScewinDocument
{
    public required string SourceName { get; init; }
    public required string RawText { get; init; }
    public required List<ScewinSegment> Segments { get; init; }
    public required List<BiosSetting> Items { get; init; }
    public required BiosMenuSection RootSection { get; init; }

    public int ModifiedCount => Items.Count(i => i.IsModified);
}
