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

namespace kaliteConfig.Models;

/// <summary>
/// A node in the BIOS menu tree, built from the dump's section header lines
/// (e.g. "----- Advanced ----- CPU Configuration -----"). Counts are
/// subtree-inclusive so a badge like "CPU Configuration (24)" tells the user
/// how many settings live below the node before they open it.
/// </summary>
public sealed class BiosMenuSection
{
    public string Name { get; }
    public string[] Path { get; }
    public BiosMenuSection? Parent { get; }
    public List<BiosMenuSection> Children { get; } = new();
    public List<BiosSetting> Settings { get; } = new();

    /// <summary>Direct settings plus everything in descendants.</summary>
    public int TotalCount { get; private set; }

    public bool IsRoot => Path.Length == 0;

    internal BiosMenuSection(string name, string[] path, BiosMenuSection? parent)
    {
        Name = name;
        Path = path;
        Parent = parent;
    }

    public string CountText => $"({TotalCount})";

    internal void AttachChild(BiosMenuSection child) => Children.Add(child);

    internal void AddSetting(BiosSetting setting)
    {
        Settings.Add(setting);
        for (var node = this; node is not null; node = node.Parent) node.TotalCount++;
    }

    public IEnumerable<BiosMenuSection> SelfAndDescendants()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var descendant in child.SelfAndDescendants())
                yield return descendant;
    }
}
