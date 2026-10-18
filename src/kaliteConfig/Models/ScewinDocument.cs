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
