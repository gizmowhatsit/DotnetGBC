namespace DotnetGBC.Cartridge;

/// <summary>
/// MBC2 Memory Bank Controller implementation.
/// Supports up to 256KByte ROM (16 banks) and 512x4 bits of built-in RAM.
/// </summary>
public class MBC2 : IMBC
{
    private readonly int _ramSize = 0x200; // 512 bytes for 512x4 bits
    private readonly int _romBankSize = 0x4000; // 16KB
    private readonly int _maxRomBanks = 16; // Max 256KB ROM
    private readonly int _ramAddressMask = 0x01FF; // For 512 entries
    private readonly byte _ramEnableValue = 0x0A;
    private readonly int _romBankValueMask = 0x0F; // Lower 4 bits for ROM bank
        
    private byte[] _romData = [];
    private readonly byte[] _ramData; // Built-in RAM (512x4 bits, stored as 512 bytes)

    private bool _ramEnabled;
    private int _currentRomBank; // Physical ROM bank number for 0x4000-0x7FFF (1-15)
    private int _romBankMask; // Mask based on actual ROM size (num physical banks - 1)

    /// <summary>
    /// Initializes a new MBC2 controller.
    /// The MBCFactory provides romData and hasBattery here.
    /// Actual ROM-dependent setup (like _romBankMask) is done in Initialize.
    /// </summary>
    public MBC2(bool hasBattery = false)
    {
        // Note: The romData parameter here is largely for compatibility with how MBCFactory might call it.
        // The definitive _romData and its dependent settings are configured in Initialize().
        _ramData = new byte[_ramSize];
        HasBattery = hasBattery;
        // Initial Reset sets defaults. Initialize() will call Reset() again after full setup.
        Reset();
    }

    /// <summary>
    /// Initializes the MBC with ROM data. Called by MMU.
    /// </summary>
    public void Initialize(byte[] romData)
    {
        _romData = romData ?? throw new ArgumentNullException(nameof(romData));
        if (_romData.Length == 0)
            throw new ArgumentException("ROM data cannot be empty.", nameof(romData));

        // Calculate ROM size and mask
        // Total number of 16KB banks in the ROM
        int numPhysicalRomBanks = Math.Max(1, _romData.Length / _romBankSize);
        // MBC2 supports up to 16 banks (256KB)
        numPhysicalRomBanks = Math.Min(numPhysicalRomBanks, _maxRomBanks);
            
        // _romBankMask is used to mirror/wrap bank selections to available physical banks.
        // If numPhysicalRomBanks is 1 (e.g. 16KB ROM), mask is 0.
        // If numPhysicalRomBanks is 16 (e.g. 256KB ROM), mask is 15 (0x0F).
        _romBankMask = numPhysicalRomBanks - 1;

        // RAM is fixed size for MBC2 and already initialized in constructor.
        // Ensure RAM is clear and 4-bit, though LoadRam would also handle 4-bit.
        for (int i = 0; i < _ramData.Length; i++)
        {
            _ramData[i] = 0x00;
        }

        Reset(); // Reset to apply initial banking state with correct masks.
    }

    /// <summary>
    /// Resets the MBC to its initial power-up state.
    /// </summary>
    public void Reset()
    {
        _ramEnabled = false;
        _currentRomBank = 1; // ROM Bank 1 is initially selected for 0x4000-0x7FFF.
    }

    /// <summary>
    /// Reads a byte from the ROM address space (0x0000-0x7FFF).
    /// </summary>
    public byte ReadRomByte(ushort address)
    {
        if (_romData.Length == 0) return 0xFF; // No ROM loaded

        if (address < 0x4000)
        {
            // ROM Bank 00 (Fixed)
            // Ensure read is within bounds of bank 0 data.
            return address < _romData.Length ? _romData[address] : (byte)0xFF;
        }
        else
        {
            // Switchable ROM Bank (01-0F)
            // _currentRomBank is 1-based.
            int romAddr = _currentRomBank * _romBankSize + (address - 0x4000);
            return romAddr < _romData.Length ? _romData[romAddr] : (byte)0xFF;
        }
    }

    /// <summary>
    /// Writes a byte to the ROM address space (0x0000-0x7FFF).
    /// This controls MBC2 registers.
    /// </summary>
    public void WriteRomByte(ushort address, byte value)
    {
        // MBC2 register writes only occur in the 0x0000-0x3FFF range
        if (address < 0x4000)
        {
            // Bit 8 of the address determines function:
            // Cleared (0): RAM Enable/Disable (e.g. 0000-00FF, 0200-02FF, ...)
            // Set (1): ROM Bank Number Select (e.g. 0100-01FF, 0300-03FF, ...)
            bool isRomBankSelectRegister = (address & 0x0100) != 0;

            if (!isRomBankSelectRegister) // Address Bit 8 is 0: RAM Enable Register
            {
                // RAM is enabled if the lower nibble of the written value is 0x0A.
                // Any other value in the lower nibble disables RAM.
                _ramEnabled = (value & 0x0F) == _ramEnableValue;
            }
            else // Address Bit 8 is 1: ROM Bank Number Register
            {
                // Lower 4 bits of value select the ROM bank number (0x00-0x0F).
                int selectedBankNibble = value & _romBankValueMask;

                // A value of 0x00 is treated as 0x01.
                int bankToSelect = (selectedBankNibble == 0) ? 1 : selectedBankNibble;

                // Apply ROM size mask (mirroring)
                // Example: if _romBankMask is 3 (for a 4-bank ROM: 0,1,2,3)
                // and bankToSelect is 5, (5 & 3) = 1. So bank 5 maps to bank 1.
                // and bankToSelect is 4, (4 & 3) = 0. So bank 4 maps to bank 0.
                _currentRomBank = bankToSelect & _romBankMask;
                    
                // If the masking results in bank 0, it should be forced to bank 1,
                // as bank 0 is fixed at 0000-3FFF and not selectable for 4000-7FFF.
                // This ensures _currentRomBank is always at least 1 if multiple banks exist.
                // If only one bank exists (_romBankMask = 0), _currentRomBank will become 0,
                // then forced to 1. ReadByte will handle out-of-bounds for bank 1 if it doesn't exist.
                if (_currentRomBank == 0)
                {
                    _currentRomBank = 1;
                }
            }
        }
        // Writes to 0x4000-0x7FFF are ignored by MBC2.
    }

    /// <summary>
    /// Reads a byte from the built-in RAM (0xA000-0xA1FF, mirrored up to 0xBFFF).
    /// </summary>
    public byte ReadRamByte(ushort address)
    {
        if (!_ramEnabled)
            return 0xFF; // RAM disabled, open bus

        // Address is masked to 9 bits for 512 RAM entries.
        // Valid RAM area 0xA000-0xA1FF. Other addresses in 0xA000-0xBFFF are mirrored.
        int ramAddr = address & _ramAddressMask;

        // MBC2 RAM is 4-bit wide. Only lower 4 bits of each byte are used.
        // Upper 4 bits often return as 1s (0xF0) or 0s. Returning lower nibble + 0xF0 for upper.
        return (byte)((_ramData[ramAddr] & 0x0F) | 0xF0); 
        // Alternatively, return (byte)(_ramData[ramAddr] & 0x0F); if upper bits should be 0.
        // Pan Docs are unclear on upper bit behavior. 0xF0 is a common practice.
    }

    /// <summary>
    /// Writes a byte to the built-in RAM (0xA000-0xA1FF, mirrored up to 0xBFFF).
    /// </summary>
    public void WriteRamByte(ushort address, byte value)
    {
        if (!_ramEnabled)
            return; // RAM disabled

        int ramAddr = address & _ramAddressMask;

        // Only lower 4 bits of the value are stored.
        _ramData[ramAddr] = (byte)(value & 0x0F);
    }

    /// <summary>
    /// Saves the built-in RAM to a file.
    /// </summary>
    public bool SaveRam(string savePath)
    {
        if (!HasBattery || _ramData.Length == 0)
            return false;

        try
        {
            File.WriteAllBytes(savePath, _ramData);
            return true;
        }
        catch (Exception ex)
        {
            // Consider proper logging instead of Console.WriteLine
            Console.WriteLine($"[MBC2] Error saving RAM: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Loads the built-in RAM from a file.
    /// </summary>
    public bool LoadRam(string savePath)
    {
        if (!HasBattery || _ramData.Length == 0)
            return false;

        try
        {
            if (!File.Exists(savePath))
                return false;

            byte[] saveData = File.ReadAllBytes(savePath);

            int bytesToCopy = Math.Min(saveData.Length, _ramData.Length);
            Array.Copy(saveData, _ramData, bytesToCopy);

            // Ensure all loaded RAM values are 4-bit only.
            for (int i = 0; i < _ramData.Length; i++)
            {
                _ramData[i] &= 0x0F;
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MBC2] Error loading RAM: {ex.Message}");
            return false;
        }
    }

    public bool HasRTC => false; // MBC2 never has RTC
    public bool HasBattery { get; }
    public bool IsRamEnabled => _ramEnabled;
    public int CurrentRomBank => _currentRomBank; // This is the bank for 0x4000-0x7FFF
    public int CurrentRamBank => 0; // MBC2 does not have RAM banking

    public int TotalRomBanks
    {
        get
        {
            if (_romData.Length == 0) return 0;
            int banks = Math.Max(1, _romData.Length / _romBankSize);
            return Math.Min(banks, _maxRomBanks);
        }
    }

    public int TotalRamBanks => 1; // MBC2 has one fixed built-in RAM block (not bankable)

    public RTCData RTC
    {
        get => RTCData.Zero; // No RTC
        set { /* No RTC, do nothing */ }
    }
}

