namespace DotnetGBC.Cartridge;

/// <summary>
/// Implementation of a cartridge with no Memory Bank Controller.
/// Used for ROM-only cartridges (32KB) or small ROM+RAM cartridges.
/// </summary>
public class NoMBC : IMBC
{
    private byte[] _romData;
    private byte[] _ramData;
    private bool _hasBattery;

    /// <summary>
    /// Initializes a new instance of the NoMBC class.
    /// </summary>
    /// <param name="ramData">RAM data array</param>
    /// <param name="hasBattery">Whether the cartridge has battery-backed RAM.</param>
    /// <param name="romData">ROM data array</param>
    public NoMBC(byte[] ramData, bool hasBattery = false)
    {
        Console.WriteLine("[NoMBC] Setup ROM and RAM data for NoMBC game");
        
        _ramData = ramData;
        _hasBattery = hasBattery;
        Reset();
    }

    /// <summary>
    /// Initializes the MBC with ROM data.
    /// </summary>
    /// <param name="romData">The full ROM data.</param>
    public void Initialize(byte[] romData)
    {
        _romData = romData ?? throw new ArgumentNullException(nameof(romData));

        // Determine RAM size from the ROM header
        byte ramSizeCode = _romData.Length > 0x0149 ? _romData[0x0149] : (byte)0;
        int ramSize = GetRamSizeFromCode(ramSizeCode);

        _ramData = ramSize > 0 ? new byte[ramSize] : new byte[0];
    }

    /// <summary>
    /// Resets the MBC to its initial state.
    /// </summary>
    public void Reset()
    {
        IsRamEnabled = false;
    }

    /// <summary>
    /// Reads a byte from the ROM address space (0x0000-0x7FFF).
    /// </summary>
    /// <param name="address">The address to read from (0x0000-0x7FFF).</param>
    /// <returns>The byte at the specified address in ROM.</returns>
    public byte ReadRomByte(ushort address)
    {
        if (_romData.Length == 0)
            return 0xFF;

        // For ROMs larger than 32KB, mirror the first 32KB
        if (address < _romData.Length)
            return _romData[address];
        else
            return 0xFF;
    }

    /// <summary>
    /// Writes a byte to the ROM address space (0x0000-0x7FFF).
    /// For ROM-only cartridges, this has no effect.
    /// </summary>
    /// <param name="address">The address to write to (0x0000-0x7FFF).</param>
    /// <param name="value">The value to write.</param>
    public void WriteRomByte(ushort address, byte value)
    {
        // ROM-only cartridges ignore writes to ROM area
        // However, some games try to enable RAM by writing to 0000-1FFF
        if (address < 0x2000)
        {
            IsRamEnabled = (value & 0x0F) == 0x0A;
        }
    }

    /// <summary>
    /// Reads a byte from the external RAM address space (0xA000-0xBFFF).
    /// </summary>
    /// <param name="address">The address to read from (0xA000-0xBFFF).</param>
    /// <returns>The byte at the specified address in RAM, or 0xFF if RAM is disabled or not present.</returns>
    public byte ReadRamByte(ushort address)
    {
        if (!IsRamEnabled || _ramData.Length == 0)
            return 0xFF;

        int offset = address - 0xA000;
        return offset < _ramData.Length ? _ramData[offset] : (byte)0xFF;
    }

    /// <summary>
    /// Writes a byte to the external RAM address space (0xA000-0xBFFF).
    /// </summary>
    /// <param name="address">The address to write to (0xA000-0xBFFF).</param>
    /// <param name="value">The value to write.</param>
    public void WriteRamByte(ushort address, byte value)
    {
        if (!IsRamEnabled || _ramData.Length == 0)
            return;

        int offset = address - 0xA000;
        if (offset < _ramData.Length)
            _ramData[offset] = value;
    }

    /// <summary>
    /// Saves the external RAM to a file.
    /// Used for games that support battery-backed saves.
    /// </summary>
    /// <param name="savePath">The path to save the file to.</param>
    /// <returns>True if the save was successful; otherwise, false.</returns>
    public bool SaveRam(string savePath)
    {
        if (!HasBattery || _ramData.Length == 0)
            return false;

        try
        {
            File.WriteAllBytes(savePath, _ramData);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Loads the external RAM from a file.
    /// Used for games that support battery-backed saves.
    /// </summary>
    /// <param name="savePath">The path to load the file from.</param>
    /// <returns>True if the load was successful; otherwise, false.</returns>
    public bool LoadRam(string savePath)
    {
        if (!HasBattery || _ramData.Length == 0)
            return false;

        try
        {
            if (!File.Exists(savePath))
                return false;

            byte[] saveData = File.ReadAllBytes(savePath);

            // Only copy as much data as we have RAM for
            int bytesToCopy = Math.Min(saveData.Length, _ramData.Length);
            Array.Copy(saveData, _ramData, bytesToCopy);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets whether this MBC has an RTC (Real Time Clock) component.
    /// </summary>
    public bool HasRTC => false;

    /// <summary>
    /// Gets whether this MBC has battery-backed RAM.
    /// </summary>
    public bool HasBattery => _hasBattery;

    /// <summary>
    /// Gets whether the external RAM is currently enabled.
    /// </summary>
    public bool IsRamEnabled { get; private set; }

    /// <summary>
    /// Gets the current ROM bank number.
    /// </summary>
    public int CurrentRomBank => 0;

    /// <summary>
    /// Gets the current RAM bank number.
    /// </summary>
    public int CurrentRamBank => 0;

    /// <summary>
    /// Gets the total number of ROM banks in this cartridge.
    /// </summary>
    public int TotalRomBanks => Math.Max(1, _romData.Length / 0x4000);

    /// <summary>
    /// Gets the total number of RAM banks in this cartridge.
    /// </summary>
    public int TotalRamBanks => Math.Max(1, _ramData.Length / 0x2000);

    /// <summary>
    /// Gets or sets the current RTC (Real Time Clock) values.
    /// Not used in ROM-only cartridges.
    /// </summary>
    public RTCData RTC
    {
        get => RTCData.Zero;
        set { /* Not used in ROM-only cartridges */ }
    }

    /// <summary>
    /// Determines the RAM size in bytes from the size code in the ROM header.
    /// </summary>
    /// <param name="ramSizeCode">The RAM size code from the ROM header (0x0149).</param>
    /// <returns>The RAM size in bytes.</returns>
    private int GetRamSizeFromCode(byte ramSizeCode)
    {
        return ramSizeCode switch
        {
            0x00 => 0,         // No RAM
            0x01 => 0x800,     // 2 KB
            0x02 => 0x2000,    // 8 KB
            0x03 => 0x8000,    // 32 KB (4 banks of 8KB)
            0x04 => 0x20000,   // 128 KB (16 banks of 8KB)
            0x05 => 0x10000,   // 64 KB (8 banks of 8KB)
            _ => 0,         // Unknown
        };
    }
}

