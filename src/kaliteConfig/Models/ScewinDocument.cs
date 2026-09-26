// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System.Collections.Generic;
using System.Linq;

namespace kaliteConfig.Models;

public abstract class ScewinSegment { }

/// <summary>
/// Any line that is neither a section header nor part of a setup item -
/// comments, "!BIOS ..." banners, dashed separators, blank lines. Preserved
/// verbatim (content + original line ending) so export round-trips exactly.
/// </summary>
public sealed class OtherSegment : ScewinSegment
{
    public required string Raw { get; init; }
}

/// <summary>A section header line, e.g. "----- Advanced ----- CPU Configuration -----".</summary>
public sealed class HeaderSegment : ScewinSegment
{
    public required string Raw { get; init; }
    public required string[] Path { get; init; }
}

/// <summary>A parsed setup item block ("Setup Question" .. separator).</summary>
public sealed class ItemSegment : ScewinSegment
{
    public required BiosSetting Item { get; init; }
}

/// <summary>
/// A full SCEWIN dump. The segment stream covers the ENTIRE file in order -
/// exporting concatenates the segments back, substituting edited items'
/// "Value" lines, which yields a byte-identical file when nothing changed.
/// </summary>
public sealed class ScewinDocument
{
    public required string SourceName { get; init; }
    public required string RawText { get; init; }
    public required List<ScewinSegment> Segments { get; init; }
    public required List<BiosSetting> Items { get; init; }
    public required BiosMenuSection RootSection { get; init; }

    public int ModifiedCount => Items.Count(i => i.IsModified);
}
