using System.Collections.Generic;

namespace kaliteConfig.Input;

public sealed class MouseColorPreset
{
    public MouseColorPreset(string name, string hex)
    {
        Name = name;
        Hex = hex;
    }

    public string Name { get; }
    public string Hex { get; }
}

public static class MousePresets
{
    public static IReadOnlyList<MouseColorPreset> LightColors { get; } = new List<MouseColorPreset>
    {
        new("Red", "#E81123"),
        new("Orange", "#FF8C00"),
        new("Yellow", "#FFD800"),
        new("Green", "#00CC6A"),
        new("Cyan", "#00B7C3"),
        new("Blue", "#0078D4"),
        new("Purple", "#8E24AA"),
        new("White", "#FFFFFF"),
    };

}
