namespace DotnetGBC.CPU;

/// <summary>
/// Enumeration of available CPU interrupt types.
/// </summary>
public enum InterruptType
{
    /// <summary>V-Blank interrupt, triggered when the PPU enters V-Blank phase.</summary>
    VBlank = 0,
    /// <summary>LCD STAT interrupt, triggered by various LCD events.</summary>
    LcdStat = 1,
    /// <summary>Timer interrupt, triggered when the timer overflows.</summary>
    Timer = 2,
    /// <summary>Serial interrupt, triggered when a serial transfer is complete.</summary>
    Serial = 3,
    /// <summary>Joypad interrupt, triggered when a button is pressed.</summary>
    Joypad = 4
}

