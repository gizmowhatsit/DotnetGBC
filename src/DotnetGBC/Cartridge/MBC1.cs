namespace DotnetGBC.Cartridge;

/// <summary>
/// MBC1 Memory Bank Controller implementation.
/// Supports up to 2MByte ROM and/or 32KByte RAM with different modes and configurations.
/// </summary>
public class MBC1 : IMBC
{
    // ROM and RAM data
    private byte[] _romData;
    private byte[] _ramData;

    // Banking registers
    private bool _ramEnabled;
    private byte _romBankRegister;     // 5-bit register (2000-3FFF), effectively 0x01-0x1F
    private byte _secondaryRegister;   // 2-bit register (4000-5FFF), 0x00-0x03
    private bool _advancedMode;        // Banking mode select (6000-7FFF)

    // Current effective bank numbers
    private int _currentRomBank;
    private int _currentRamBank;

    // Cartridge properties determined during Initialize
    private bool _isMBC1M;
    private bool _isBigROM;            // ROM is 1MB or larger
    private int _romBankMask;
    private int _ramBankMask;

    /// <summary>
    /// Initializes a new MBC1 controller.
    /// The main setup is done in the Initialize method, as per IMBC interface.
    /// </summary>
    public MBC1(byte[] ramData, bool hasBattery, bool isMBC1M)
    {
        _ramData = ramData; // Factory sizes this based on header & MBC1 rules
        HasBattery = hasBattery;
        _isMBC1M = isMBC1M; // Factory determines this
    }

    /// <summary>
    /// Initializes the MBC with ROM data and sets up internal properties.
    /// </summary>
    public void Initialize(byte[] romData)
    {
        Console.WriteLine("[MBC1] Initializing...");
        _romData = romData ?? throw new ArgumentNullException(nameof(romData), "ROM data cannot be null.");
        // _isMBC1M is already set from constructor via factory.
        // _ramData is already provided and sized by the factory.

        // ROM properties
        int romSizeInBytes = _romData.Length;
        if (romSizeInBytes == 0) throw new ArgumentException("ROM data cannot be empty.", nameof(romData));
        int numRomBanks = Math.Max(1, romSizeInBytes / 0x4000); // 16KB per bank
        _romBankMask = numRomBanks - 1;
        _isBigROM = romSizeInBytes >= 0x100000; // 1MB or more

        // RAM properties (using _ramData sized by factory)
        if (_ramData.Length > 0)
        {
            int numRamBanks = Math.Max(1, _ramData.Length / 0x2000); // 8KB per bank
            // _ramBankMask is 0 if 1 bank (8KB or less), (num_banks-1) if multiple banks (e.g., 3 for 32KB)
            _ramBankMask = numRamBanks > 1 ? numRamBanks - 1 : 0;
        }
        else
        {
            _ramBankMask = 0;
        }
            
        // As per Pan Docs, all MBC1 carts with >= 1MB ROM use alternate wiring limiting RAM to 8KB
        // If _isBigROM is true, _ramData.Length should be <= 0x2000.
        // Consequently, _ramBankMask would be 0.

        Reset();
    }

    /// <summary>
    /// Resets the MBC to its initial power-up state.
    /// </summary>
    public void Reset()
    {
        _ramEnabled = false;
        _romBankRegister = 0x01; // Defaults to 0x00, which is treated as 0x01
        _secondaryRegister = 0x00;
        _advancedMode = false;    // Mode 0 (Simple RAM/ROM mode)
        UpdateCurrentBanks();
    }

    /// <summary>
    /// Reads a byte from the ROM address space (0x0000-0x7FFF).
    /// </summary>
    public byte ReadRomByte(ushort address)
    {
        if (_romData.Length == 0) return 0xFF;

        int bankToRead;
        ushort offsetInBank = (ushort)(address & 0x3FFF); // Offset within a 16KB bank

        if (address < 0x4000) // 0000-3FFF (ROM Bank X0 area)
        {
            if (!_advancedMode) // Mode 0 (Simple)
            {
                bankToRead = 0; // Always Bank 0
            }
            else // Mode 1 (Advanced)
            {
                if (_isMBC1M)
                {
                    // For MBC1M, Mode 1: 0000-3FFF uses ((SecondaryReg << 4) | (RomRegBank & 0x0F))
                    // This is (SecondaryBankBits << 4) | (LowerBitsOfRomBankRegister)
                    bankToRead = ((_secondaryRegister & 0x03) << 4) | (_romBankRegister & 0x0F);
                }
                else // Standard MBC1, Mode 1
                {
                    // Banks $00, $20, $40, $60 selected by _secondaryRegister (upper 2 bits of bank number)
                    // The lower 5 bits of the bank number are treated as 0 for this region in this mode.
                    bankToRead = (_secondaryRegister & 0x03) << 5;
                }
                bankToRead &= _romBankMask; // Apply overall ROM size mask
            }
        }
        else // 4000-7FFF (Switchable ROM Bank area)
        {
            // This area always uses the fully calculated _currentRomBank
            bankToRead = _currentRomBank;
        }

        int romAddr = bankToRead * 0x4000 + offsetInBank;
        return romAddr < _romData.Length ? _romData[romAddr] : (byte)0xFF;
    }

    /// <summary>
    /// Writes to MBC control registers mapped in the ROM address space (0x0000-0x7FFF).
    /// </summary>
    public void WriteRomByte(ushort address, byte value)
    {
        if (address < 0x2000) // 0000-1FFF: RAM Enable
        {
            _ramEnabled = (_ramData.Length > 0) && ((value & 0x0F) == 0x0A);
        }
        else if (address < 0x4000) // 2000-3FFF: ROM Bank Number (lower 5 bits)
        {
            _romBankRegister = (byte)(value & 0x1F);
            if (_romBankRegister == 0x00)
            {
                _romBankRegister = 0x01; // Bank 0 is never selectable here, maps to Bank 1
            }
            UpdateCurrentBanks();
        }
        else if (address < 0x6000) // 4000-5FFF: RAM Bank Number or Upper Bits of ROM Bank Number
        {
            _secondaryRegister = (byte)(value & 0x03); // 2-bit register
            UpdateCurrentBanks();
        }
        else // 6000-7FFF: Banking Mode Select
        {
            _advancedMode = (value & 0x01) != 0; // 0 = Simple, 1 = Advanced
            UpdateCurrentBanks();
        }
    }

    /// <summary>
    /// Reads a byte from the external RAM address space (0xA000-0xBFFF).
    /// </summary>
    public byte ReadRamByte(ushort address)
    {
        if (!_ramEnabled || _ramData.Length == 0)
        {
            return 0xFF; // RAM disabled or not present
        }

        ushort offsetInBank = (ushort)(address & 0x1FFF); // Offset within an 8KB bank
        int ramAddr = _currentRamBank * 0x2000 + offsetInBank;

        return ramAddr < _ramData.Length ? _ramData[ramAddr] : (byte)0xFF;
    }

    /// <summary>
    /// Writes a byte to the external RAM address space (0xA000-0xBFFF).
    /// </summary>
    public void WriteRamByte(ushort address, byte value)
    {
        if (!_ramEnabled || _ramData.Length == 0)
        {
            return; // RAM disabled or not present
        }

        ushort offsetInBank = (ushort)(address & 0x1FFF); // Offset within an 8KB bank
        int ramAddr = _currentRamBank * 0x2000 + offsetInBank;

        if (ramAddr < _ramData.Length)
        {
            _ramData[ramAddr] = value;
        }
    }

    /// <summary>
    /// Updates the current effective ROM and RAM bank numbers based on register values and mode.
    /// </summary>
    private void UpdateCurrentBanks()
    {
        // --- ROM Bank Calculation for 4000-7FFF region (and sometimes 0000-3FFF for MBC1M Mode 1) ---
        int effectiveRomBank;
        if (_isMBC1M)
        {
            // MBC1M: Secondary register (bits 0-1) maps to bits 4-5 of the ROM bank.
            // Main ROM register (5-bits, 0x01-0x1F) provides bits 0-3 (its bit 4 is ignored for this calculation).
            effectiveRomBank = ((_secondaryRegister & 0x03) << 4) | (_romBankRegister & 0x0F);
        }
        else if (_isBigROM) // Standard wiring, ROM >= 1MB
        {
            // Secondary register (bits 0-1) maps to bits 5-6 of the ROM bank.
            // Main ROM register (5-bits, 0x01-0x1F) provides bits 0-4.
            effectiveRomBank = ((_secondaryRegister & 0x03) << 5) | _romBankRegister;
        }
        else // Standard wiring, ROM < 1MB (<= 512KB)
        {
            // Only the main ROM bank register is used. Secondary register is for RAM (if applicable) or unused for ROM.
            effectiveRomBank = _romBankRegister;
        }
        _currentRomBank = effectiveRomBank & _romBankMask;

        // --- RAM Bank Calculation ---
        // RAM banking is possible if:
        // 1. In Advanced Mode.
        // 2. Not a "Big ROM" (because for Big ROMs, secondary register is used for ROM banking).
        // 3. RAM size allows for banking (i.e., _ramBankMask > 0, e.g. for 32KB RAM).
        if (_advancedMode && !_isBigROM)
        {
            _currentRamBank = _secondaryRegister & _ramBankMask; // _ramBankMask is 0 for non-bankable RAM (e.g. 8KB)
        }
        else
        {
            // Simple Mode, or Big ROM (secondary reg used for ROM), or RAM not bankable
            _currentRamBank = 0;
        }
    }

    public bool SaveRam(string savePath)
    {
        if (!HasBattery || _ramData.Length == 0) return false;
        try
        {
            File.WriteAllBytes(savePath, _ramData);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving RAM: {ex.Message}");
            return false;
        }
    }
    
    

    public bool LoadRam(string savePath)
    {
        if (!HasBattery || _ramData.Length == 0) return false;
        try
        {
            if (!File.Exists(savePath)) return false;
            byte[] saveData = File.ReadAllBytes(savePath);
            int bytesToCopy = Math.Min(saveData.Length, _ramData.Length);
            Array.Copy(saveData, _ramData, bytesToCopy);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading RAM: {ex.Message}");
            return false;
        }
    }

    public bool HasRTC => false;
    public bool HasBattery { get; }
    public bool IsRamEnabled => _ramEnabled;
    public int CurrentRomBank => _currentRomBank;
    public int CurrentRamBank => _currentRamBank;
    public int TotalRomBanks => _romData.Length / 0x4000;
    public int TotalRamBanks => _ramData.Length > 0 ? Math.Max(1, _ramData.Length / 0x2000) : 0;
    public RTCData RTC { get => RTCData.Zero; set { /* No RTC in MBC1 */ } }
}

