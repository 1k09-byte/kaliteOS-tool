// ==============================================================================
// Copyright (c) 2026 kaliteConfig
// All rights reserved.
//
// This software and associated documentation files are proprietary.
// You may not use, copy, reproduce, modify, merge, publish, distribute, sublicense,
// reverse-engineer, or sell copies of the software in any form, in whole or in part,
// without the express written permission of the copyright holder.
// ==============================================================================
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml.Media;

namespace kaliteConfig.Models;

[JsonDerivedType(typeof(CopyAction), typeDiscriminator: "copy")]
[JsonDerivedType(typeof(SaveAction), typeDiscriminator: "save")]
[JsonDerivedType(typeof(EditorAction), typeDiscriminator: "editor")]
[JsonDerivedType(typeof(OcrAction), typeDiscriminator: "ocr")]
[JsonDerivedType(typeof(CompressAction), typeDiscriminator: "compress")]
[JsonDerivedType(typeof(StampAction), typeDiscriminator: "stamp")]
[JsonDerivedType(typeof(WebhookAction), typeDiscriminator: "webhook")]
public abstract class PipelineActionBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    [JsonIgnore]
    public abstract string DisplayName { get; }

    [JsonIgnore]
    public abstract string Description { get; }

    [JsonIgnore]
    public abstract string IconGlyph { get; }
    
    // Tracks execution status during pipeline run
    private bool _isRunning;
    [JsonIgnore]
    public bool IsRunning 
    { 
        get => _isRunning; 
        set { _isRunning = value; OnPropertyChanged(nameof(IsRunning)); }
    }
}

public class CopyAction : PipelineActionBase
{
    public override string DisplayName => "Copy to Clipboard";
    public override string Description => "Puts the image into the system clipboard.";
    public override string IconGlyph => "\uE8C8"; // Copy
}

public class SaveAction : PipelineActionBase
{
    public override string DisplayName => "Save to Disk";
    public override string Description => "Saves the image to the default Snip directory.";
    public override string IconGlyph => "\uE74E"; // Save
    
    public bool OverwriteExisting { get; set; } = false;
}

public class EditorAction : PipelineActionBase
{
    public override string DisplayName => "Open in Editor";
    public override string Description => "Launches the full Snip image editor window.";
    public override string IconGlyph => "\uE70F"; // Edit
}

public class OcrAction : PipelineActionBase
{
    public override string DisplayName => "Extract Text (OCR)";
    public override string Description => "Extracts text from the image and copies it.";
    public override string IconGlyph => "\uE8D2"; // Textbox
}

public class CompressAction : PipelineActionBase
{
    public override string DisplayName => "Compress Image";
    public override string Description => "Applies WebP compression targeting a specific file size.";
    public override string IconGlyph => "\uE8A8"; // Zip
    
    public int TargetSizeBytes { get; set; } = 204800; // 200 KB
}

public class StampAction : PipelineActionBase
{
    public override string DisplayName => "Watermark / Stamp";
    public override string Description => "Overlays timestamps and active app metadata onto the image.";
    public override string IconGlyph => "\uE734"; // Edit
}

public class WebhookAction : PipelineActionBase
{
    public override string DisplayName => "Webhook POST";
    public override string Description => "Uploads the snip payload to a REST API endpoint.";
    public override string IconGlyph => "\uE72A"; // Web
    
    public string TargetUrl { get; set; } = "";
}

public class SnipPipeline : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private string _name = "New Pipeline";
    public string Name 
    { 
        get => _name; 
        set { _name = value; OnPropertyChanged(nameof(Name)); }
    }

    private string _boundProcess = "";
    public string BoundProcess 
    { 
        get => _boundProcess; 
        set { _boundProcess = value; OnPropertyChanged(nameof(BoundProcess)); }
    }

    private string _hotkeyLabel = "";
    public string HotkeyLabel 
    { 
        get => _hotkeyLabel; 
        set { _hotkeyLabel = value; OnPropertyChanged(nameof(HotkeyLabel)); }
    }

    public ObservableCollection<PipelineActionBase> Actions { get; set; } = new();
}
