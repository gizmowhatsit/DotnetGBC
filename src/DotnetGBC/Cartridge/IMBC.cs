namespace DotnetGBC.Cartridge;

/// <summary>
/// Interface for Memory Bank Controller implementations.
/// Memory Bank Controllers (MBCs) handle ROM and RAM banking for Game Boy cartridges.
/// </summary>
public interface IMBC
{
    /// <summary>
    /// Initializes the MBC with ROM data.
    /// </summary>
    /// <param name="romData">The full ROM data.</param>
    void Initialize(byte[] romData);

    /// <summary>
    /// Resets the MBC to its initial state.
    /// </summary>
    void Reset();

    /// <summary>
    /// Reads a byte from the ROM address space (0x0000-0x7FFF).
    /// The MBC may map different ROM banks based on its current state.
    /// </summary>
    /// <param name="address">The address to read from (0x0000-0x7FFF).</param>
    /// <returns>The byte at the mapped ROM location.</returns>
    byte ReadRomByte(ushort address);

    /// <summary>
    /// Writes a byte to the ROM address space (0x0000-0x7FFF).
    /// For most MBCs, this doesn't write to ROM but instead controls
    /// banking registers and other MBC settings.
    /// </summary>
    /// <param name="address">The address to write to (0x0000-0x7FFF).</param>
    /// <param name="value">The value to write.</param>
    void WriteRomByte(ushort address, byte value);

    /// <summary>
    /// Reads a byte from the external RAM address space (0xA000-0xBFFF).
    /// The MBC may map different RAM banks based on its current state.
    /// </summary>
    /// <param name="address">The address to read from (0xA000-0xBFFF).</param>
    /// <returns>The byte at the mapped RAM location, or 0xFF if RAM is disabled.</returns>
    byte ReadRamByte(ushort address);

    /// <summary>
    /// Writes a byte to the external RAM address space (0xA000-0xBFFF).
    /// The MBC may map different RAM banks based on its current state.
    /// </summary>
    /// <param name="address">The address to write to (0xA000-0xBFFF).</param>
    /// <param name="value">The value to write.</param>
    void WriteRamByte(ushort address, byte value);

    /// <summary>
    /// Saves the external RAM to a file.
    /// Used for games that support battery-backed saves.
    /// </summary>
    /// <param name="savePath">The path to save the file to.</param>
    /// <returns>True if the save was successful; otherwise, false.</returns>
    bool SaveRam(string savePath);
    
    /// <summary>
    /// Loads the external RAM from a file.
    /// Used for games that support battery-backed saves.
    /// </summary>
    /// <param name="savePath">The path to load the file from.</param>
    /// <returns>True if the load was successful; otherwise, false.</returns>
    bool LoadRam(string savePath);

    /// <summary>
    /// Gets whether this MBC has an RTC (Real Time Clock) component.
    /// </summary>
    bool HasRTC { get; }

    /// <summary>
    /// Gets whether this MBC has battery-backed RAM.
    /// </summary>
    bool HasBattery { get; }

    /// <summary>
    /// Gets whether the external RAM is currently enabled.
    /// </summary>
    bool IsRamEnabled { get; }

    /// <summary>
    /// Gets the current ROM bank number.
    /// </summary>
    int CurrentRomBank { get; }

    /// <summary>
    /// Gets the current RAM bank number.
    /// </summary>
    int CurrentRamBank { get; }

    /// <summary>
    /// Gets the total number of ROM banks in this cartridge.
    /// </summary>
    int TotalRomBanks { get; }

    /// <summary>
    /// Gets the total number of RAM banks in this cartridge.
    /// </summary>
    int TotalRamBanks { get; }

    /// <summary>
    /// Gets or sets the current RTC (Real Time Clock) values.
    /// Only relevant for MBC3 cartridges with RTC.
    /// </summary>
    RTCData RTC { get; set; }
}

