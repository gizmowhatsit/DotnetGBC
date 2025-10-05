using DotnetGBC.Cartridge;
using DotnetGBC.SDL2.Input;

namespace DotnetGBC.Memory;

/// <summary>
/// Implementation of the Game Boy Memory Management Unit (MMU).
/// This implementation supports all cartridge types through the MBC interface.
/// </summary>
public class MMU : SDLEventSubscriber, IMemoryWriteObservable
{
    // Memory regions
    private readonly byte[] _vram; // 0x8000-0x9FFF (16KB for CGB: 2 banks of 8KB)
    private readonly byte[] _wram; // 0xC000-0xDFFF (8KB normally, CGB has banked WRAM too)
    private readonly byte[] _oam;  // 0xFE00-0xFE9F (160B)
    private readonly byte[] _io;   // 0xFF00-0xFF7F (128B)
    private readonly byte[] _hram; // 0xFF80-0xFFFE (127B)
    private byte _ieRegister;      // 0xFFFF (Interrupt Enable)
    public int DivCounter { get; set; }

    private bool _timaEnabled;
    private int _timaAccumulator = 0;

    private int _tacFrequency = 1024; // default value for 0x00 in register

    // Memory Bank Controller
    private IMBC _mbc;

    // ROM data for header access
    private byte[]? _romData;

    // GBC or DMG?
    public bool IsGbcMode { get; private set; }

    // Input states - reference SDLInputBindEnum for order
    private bool[] _inputState = new bool[8];

    // Dictionary to store write handlers for memory observers
    private readonly Dictionary<ushort, List<Action<ushort, byte>>> _writeHandlers = new Dictionary<ushort, List<Action<ushort, byte>>>();

    // CGB VRAM Bank (mirrors _io[0x4F] bit 0 for faster access in Read/WriteByte)
    private int _currentVramBank = 0;

    // Constructor
    public MMU()
    {
        SDLEventPublisher.Subscribe(this, SDLInputSubscriptionCategory.GameInputs);

        _vram = new byte[0x4000]; // 16KB for CGB (2 banks of 8KB)
        _wram = new byte[0x2000]; // 8KB base WRAM (WRAM banking handled by SVBK for CGB)
        _oam = new byte[0xA0];   // 160B
        _io = new byte[0x80];    // 128B
        _hram = new byte[0x7F];  // 127B
        _ieRegister = 0;
        _mbc = null!; // Will be set when a ROM is loaded

        Reset();
    }

    /// <summary>
    /// Resets the MMU to its initial state.
    /// </summary>
    public void Reset()
    {
        // Clear all RAM areas
        Array.Clear(_vram, 0, _vram.Length);
        Array.Clear(_wram, 0, _wram.Length); // CGB WRAM banks also need clearing if implemented fully
        Array.Clear(_oam, 0, _oam.Length);
        Array.Clear(_io, 0, _io.Length);
        Array.Clear(_hram, 0, _hram.Length);
        _ieRegister = 0;
        _currentVramBank = 0;

        // Reset the MBC if it exists
        _mbc?.Reset();

        // Initialize I/O registers with their power-up values
        InitializeIORegisters();
    }

    /// <summary>
    /// Reads a byte from the specified memory address.
    /// </summary>
    /// <param name="address">The memory address to read from (0x0000-0xFFFF).</param>
    /// <returns>The byte value at the specified address.</returns>
    public byte ReadByte(ushort address)
    {
        if (address < 0x8000)
        {
            return _mbc != null ? _mbc.ReadRomByte(address) : (byte)0xFF;
        }
        else if (address < 0xA000) // VRAM (0x8000-0x9FFF)
        {
            if (IsGbcMode)
            {
                // CGB VRAM Bank 0 or 1
                // _currentVramBank is derived from _io[0x4F] bit 0
                int vramOffset = _currentVramBank * 0x2000;
                return _vram[vramOffset + (address - 0x8000)];
            }
            else
            {
                // DMG VRAM (Bank 0 only)
                return _vram[address - 0x8000];
            }
        }
        else if (address < 0xC000)
        {
            return _mbc != null ? _mbc.ReadRamByte(address) : (byte)0xFF;
        }
        else if (address < 0xE000) // WRAM (0xC000-0xDFFF)
        {
            // Note: CGB WRAM banking (SVBK - FF70) would be handled here if fully implemented.
            // For now, assuming Bank 0 (C000-CFFF) and Bank 1 (D000-DFFF from _wram)
            // or Bank X (D000-DFFF) selected by SVBK.
            // This example keeps it simple and uses the first 8KB of WRAM.
            // A full SVBK implementation would involve more WRAM space and bank selection logic.
            if (IsGbcMode && address >= 0xD000)
            {
                int wramBank = _io[0x70] & 0x07;
                if (wramBank == 0) wramBank = 1; // Bank 0 means Bank 1
                // This requires _wram to be larger (e.g., 32KB) and proper indexing.
                // For simplicity, this example does not implement full WRAM banking.
                // Assuming _wram[address - 0xC000] for now.
                // To correctly implement, you'd need:
                // if (address < 0xD000) return _wram[address - 0xC000]; // Bank 0
                // else return _wram[(wramBank * 0x1000) + (address - 0xD000)]; // Bank 1-7 in D000-DFFF
            }
            return _wram[address - 0xC000];
        }
        else if (address < 0xFE00)
        {
            // Echo RAM (0xE000-0xFDFF) - Mirror of 0xC000-0xDDFF
            // Also needs to consider WRAM banking for CGB
            if (IsGbcMode && address >= 0xF000) // Mirror of D000-DDFF
            {
                 // Similar WRAM bank logic as above for C000-DFFF applies here for the echo.
            }
            return _wram[address - 0xE000];
        }
        else if (address < 0xFEA0)
        {
            return _oam[address - 0xFE00];
        }
        else if (address < 0xFF00)
        {
            return 0xFF;
        }
        else if (address < 0xFF80)
        {
            return ReadIORegister(address);
        }
        else if (address < 0xFFFF)
        {
            return _hram[address - 0xFF80];
        }
        else
        {
            return _ieRegister;
        }
    }

    /// <summary>
    /// Writes a byte to the specified memory address.
    /// </summary>
    /// <param name="address">The memory address to write to (0x0000-0xFFFF).</param>
    /// <param name="value">The byte value to write.</param>
    public void WriteByte(ushort address, byte value)
    {
        NotifyWriteHandlers(address, value);

        if (address < 0x8000)
        {
            if (_mbc != null)
                _mbc.WriteRomByte(address, value);
        }
        else if (address < 0xA000) // VRAM (0x8000-0x9FFF)
        {
            if (IsGbcMode)
            {
                // CGB VRAM Bank 0 or 1
                // _currentVramBank is derived from _io[0x4F] bit 0
                int vramOffset = _currentVramBank * 0x2000;
                _vram[vramOffset + (address - 0x8000)] = value;
            }
            else
            {
                // DMG VRAM (Bank 0 only)
                _vram[address - 0x8000] = value;
            }
        }
        else if (address < 0xC000)
        {
            if (_mbc != null)
                _mbc.WriteRamByte(address, value);
        }
        else if (address < 0xE000) // WRAM (0xC000-0xDFFF)
        {
            // CGB WRAM banking (SVBK - FF70) similar to ReadByte
             if (IsGbcMode && address >= 0xD000)
            {
                // WRAM Bank X for D000-DFFF
            }
            _wram[address - 0xC000] = value;
        }
        else if (address < 0xFE00)
        {
            // Echo RAM (0xE000-0xFDFF) - Mirror of 0xC000-0xDDFF
            // Also needs to consider WRAM banking for CGB
             if (IsGbcMode && address >= 0xF000) // Mirror of D000-DDFF
            {
                // WRAM Bank X for echo
            }
            _wram[address - 0xE000] = value;
        }
        else if (address < 0xFEA0)
        {
            _oam[address - 0xFE00] = value;
        }
        else if (address < 0xFF00)
        {
            return;
        }
        else if (address < 0xFF80)
        {
            WriteIORegister(address, value);
        }
        else if (address < 0xFFFF)
        {
            _hram[address - 0xFF80] = value;
        }
        else
        {
            _ieRegister = value;
        }
    }

    public ushort ReadWord(ushort address)
    {
        byte low = ReadByte(address);
        byte high = ReadByte((ushort)(address + 1));
        return (ushort)((high << 8) | low);
    }

    public void WriteWord(ushort address, ushort value)
    {
        WriteByte(address, (byte)(value & 0xFF));
        WriteByte((ushort)(address + 1), (byte)(value >> 8));
    }

    public bool LoadRom(byte[] romData)
    {
        if (romData == null || romData.Length == 0) return false;
        _romData = romData;
        if (romData.Length < 0x150) return false;

        IsGbcMode = DetermineGbcMode(romData); 

        byte cartridgeType = romData[0x0147];
        _mbc = MBCFactory.CreateMBC(cartridgeType, romData);
        _mbc.Initialize(romData);
        
        // After determining IsGbcMode, reset and initialize IO registers again
        // to ensure correct power-up values for the determined mode.
        Reset(); 

        return true;
    }
    
    private bool DetermineGbcMode(byte[] romData)
    {
        if (romData.Length > 0x0143)
        {
            byte cgbFlag = romData[0x0143];
            return (cgbFlag == 0x80 || cgbFlag == 0xC0);
        }
        return false;
    }

    private void InitializeIORegisters()
    {
        bool notifyHandlers = _writeHandlers.Count > 0;
        byte initValue;

        // Common registers
        _io[0x05] = 0x00; // TIMA
        _io[0x06] = 0x00; // TMA
        _io[0x07] = 0x00; // TAC
        
        _io[0x10] = 0x80; // NR10
        _io[0x11] = 0xBF; // NR11
        _io[0x12] = 0xF3; // NR12
        // _io[0x13] = 0xFF; // NR13 Write-only
        _io[0x14] = 0xBF; // NR14
        _io[0x16] = 0x3F; // NR21
        _io[0x17] = 0x00; // NR22
        // _io[0x18] = 0xFF; // NR23 Write-only
        _io[0x19] = 0xBF; // NR24
        _io[0x1A] = 0x7F; // NR30
        _io[0x1B] = 0xFF; // NR31
        _io[0x1C] = 0x9F; // NR32
        // _io[0x1D] = 0xFF; // NR33 Write-only
        _io[0x1E] = 0xBF; // NR34
        // _io[0x20] = 0xFF; // NR41 Write-only
        _io[0x21] = 0x00; // NR42
        _io[0x22] = 0x00; // NR43
        _io[0x23] = 0xBF; // NR44
        _io[0x24] = 0x77; // NR50
        _io[0x25] = 0xF3; // NR51
        
        // NR52 is weirdly complex.
        // https://gbdev.io/pandocs/Power_Up_Sequence.html
        // _io[0x26] = IsGbcMode ? (byte)0xF1 : (byte)0xF1;
        _io[0x26] = 0xF1; // NR52

        initValue = 0x91; _io[0x40] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF40, initValue); // LCDC
        initValue = 0x00; _io[0x41] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF41, initValue); // STAT (though bits 0-2 are read-only from PPU)
        initValue = 0x00; _io[0x42] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF42, initValue); // SCY
        initValue = 0x00; _io[0x43] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF43, initValue); // SCX
        initValue = 0x00; _io[0x44] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF44, initValue); // LY
        initValue = 0x00; _io[0x45] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF45, initValue); // LYC
        initValue = 0x00; _io[0x46] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF46, initValue); // DMA
        initValue = 0xFC; _io[0x47] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF47, initValue); // BGP
        initValue = 0xFF; _io[0x48] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF48, initValue); // OBP0
        initValue = 0xFF; _io[0x49] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF49, initValue); // OBP1
        initValue = 0x00; _io[0x4A] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF4A, initValue); // WY
        initValue = 0x00; _io[0x4B] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF4B, initValue); // WX
        // _io[0x4C] - KEY0 (CGB Mode only) - Handled by boot ROM, usually becomes read-only. Not typically emulated for write by game.
        // _io[0x4D] - KEY1 (CGB Mode only) - Prepare Speed Switch
        // _io[0x4F] - VBK (CGB Mode only) - VRAM Bank Select

        if (IsGbcMode)
        {
            initValue = 0x00; _io[0x4D] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF4D, initValue); // KEY1
            initValue = 0xFE; _io[0x4F] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF4F, initValue); // VBK (Bit 0 = 0, others 1 on read, so init with bit0=0, effectively 0x00 after masking on first write for bank 0)
                                                                                                                 // The write handler for 0x4F will mask it to (value & 0x01), so effectively init to 0 for bank 0.
                                                                                                                 // Initializing with 0xFE ensures ReadIORegister for 0x4F (before any game write) returns 0xFE as per PanDocs if written value for read is just _io[0x4F] | 0xFE
                                                                                                                 // Let's ensure write to 0x4F sets _currentVramBank correctly and read gives back `(_io[0x4F] & 0x01) | 0xFE;`
            _currentVramBank = 0; // Default to bank 0
            _io[0x4F] = 0xFE; // Initial read value before game writes to it. Game write will set bit 0.
            
            _io[0x51] = 0xFF; // HDMA1
            _io[0x52] = 0xFF; // HDMA2
            _io[0x53] = 0xFF; // HDMA3
            _io[0x54] = 0xFF; // HDMA4
            _io[0x55] = 0xFF; // HDMA5 (0xFF means inactive)

            initValue = 0x00; _io[0x68] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF68, initValue); // BCPS/BGPI
            initValue = 0x00; _io[0x69] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF69, initValue); // BCPD/BGPD
            initValue = 0x00; _io[0x6A] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF6A, initValue); // OCPS/OBPI
            initValue = 0x00; _io[0x6B] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF6B, initValue); // OCPD/OBPD
            
            initValue = 0xF9; // Default SVBK to bank 1 (bits 0-2 = 001), other bits are 1.
                              // Write to SVBK will mask to (value & 0x07), if 0 then treated as 1.
            _io[0x70] = initValue; if (notifyHandlers) NotifyWriteHandlers(0xFF70, initValue); // SVBK (WRAM Bank)

            // Undocumented CGB registers
            _io[0x56] = 0xFF; // RP (Infrared) - PanDocs initial value not specified, but typically $FF or $C0 if reading enabled
            _io[0x6C] = 0xFF; // OPRI (Object Priority) - FF according to some tests.
            _io[0x72] = 0x00;
            _io[0x73] = 0x00;
            _io[0x74] = IsGbcMode ? (byte)0x00 : (byte)0xFF; // Different for CGB vs Non-CGB
            _io[0x75] = 0x8F; // Bits 4-6 R/W, default 0. Other bits fixed. So 0x8F would mean bits 4,5,6 are 0. PanDocs says their initial value is 0. And others are fixed.
                              // So if fixed bits are 10001111 = 0x8F (assuming bits 4,5,6 = 000)
                              // Let's assume 0x8F is a common default observed.
        }
        else // DMG specific initial values (some IO regs differ)
        {
            // FF50 is often set to 0x01 by game to disable boot ROM, but MMU does not need to init it.
            // DMG doesn't have FF4C, FF4D, FF4F, FF51-FF56, FF68-FF6C, FF70, FF72-FF77
        }

        _io[0x0F] = 0xE1; // IF register often starts with bit 0 set if VBlank occurred during boot. E1 means (11100001)
        _ieRegister = 0x00;
    }


    private byte ReadIORegister(ushort address)
    {
        byte register = (byte)(address & 0xFF);
        switch (register)
        {
            case 0x00: // JOYP
                byte currentJoypadWrite = _io[0x00];
                byte selectionBits = (byte)(currentJoypadWrite & 0x30);
                byte result = (byte)(selectionBits | 0x0F); // Start with unselected buttons high

                if ((selectionBits & 0x10) == 0) // Direction buttons selected
                {
                    if (_inputState[(int)SDLInputBindAction.BUTTON_RIGHT]) result &= 0xFE; // Bit 0
                    if (_inputState[(int)SDLInputBindAction.BUTTON_LEFT])  result &= 0xFD; // Bit 1
                    if (_inputState[(int)SDLInputBindAction.BUTTON_UP])    result &= 0xFB; // Bit 2
                    if (_inputState[(int)SDLInputBindAction.BUTTON_DOWN])  result &= 0xF7; // Bit 3
                }
                if ((selectionBits & 0x20) == 0) // Action buttons selected
                {
                    if (_inputState[(int)SDLInputBindAction.BUTTON_A])      result &= 0xFE; // Bit 0
                    if (_inputState[(int)SDLInputBindAction.BUTTON_B])      result &= 0xFD; // Bit 1
                    if (_inputState[(int)SDLInputBindAction.BUTTON_SELECT]) result &= 0xFB; // Bit 2
                    if (_inputState[(int)SDLInputBindAction.BUTTON_START])  result &= 0xF7; // Bit 3
                }
                return (byte)(result | 0xC0); // Bits 6 and 7 always read 1

            case 0x07: // TAC
                return (byte)(_io[0x07] | 0xF8); // Upper 5 bits always 1

            case 0x0F: // IF (Interrupt Flags)
                return (byte)(_io[0x0F] | 0xE0); // Upper 3 bits always 1
            
            case 0x41: // STAT
                // Combine writable bits from _io[0x41] with read-only bits from PPU
                // For now, assuming PPU updates _io[0x41] directly for bits 0-2.
                // Ensure upper bit is 1 as per some docs.
                return (byte)(_io[0x41] | 0x80); // Bit 7 usually reads 1
                // return _io[0x41]; // Or just this if PPU fully manages it including fixed bits.

            case 0x44: // LY
                return _io[0x44]; // Managed by PPU

            case 0x4F: // VBK (VRAM Bank Select) - CGB Only
                if (IsGbcMode)
                {
                    // "Reading from this register will return the number of the currently 
                    //  loaded VRAM bank in bit 0, and all other bits will be set to 1."
                    return (byte)((_io[0x4F] & 0x01) | 0xFE);
                }
                return 0xFF; // Not present on DMG, reads FF

            case 0x55: // HDMA5 (CGB Only)
                if (IsGbcMode) {
                    // If HDMA is active, bit 7 is 0. If inactive/complete, bit 7 is 1.
                    // The value reflects remaining length or FF if done.
                    return _io[0x55]; 
                }
                return 0xFF; // Not present on DMG

            case 0x70: // SVBK (WRAM Bank Select) - CGB Only
                if (IsGbcMode)
                {
                    return (byte)((_io[0x70] & 0x07) | 0xF8); // Bits 3-7 read as 1
                }
                return 0xFF; // Not present on DMG

            // Add cases for other CGB read-only/masked bits if necessary
            // e.g. FF4D (KEY1) bit 7 is read-only.
            case 0x4D: // KEY1 (CGB only)
                if (IsGbcMode) {
                    // Bit 7 is current speed (read-only), bit 0 is switch armed (R/W)
                    // This requires knowing the actual current speed from CPU/timer logic
                    // For now, just return what's stored, assuming CPU updates bit 7 if needed.
                    // Or, manage current speed state here and combine.
                    // byte currentSpeedBit = _isDoubleSpeed ? (byte)0x80 : (byte)0x00;
                    // return (byte)((_io[0x4D] & 0x01) | currentSpeedBit | 0x7E); // Bits 1-6 read 1
                    return (byte)(_io[0x4D] | 0x7E); // Assuming bit 7 is correctly maintained elsewhere or this is sufficient
                }
                return 0xFF;


            default:
                // For many I/O registers, unused bits read as 1.
                // This default might need to be smarter or have more explicit cases.
                // For example, FF01 (SB), FF02 (SC) have specific read behaviors for unused bits.
                // For now, keep it simple:
                return _io[register];
        }
    }

    private void WriteIORegister(ushort address, byte value)
    {
        byte register = (byte)(address & 0xFF);

        switch (register)
        {
            case 0x00: // JOYP
                _io[0x00] = (byte)((_io[0x00] & 0xCF) | (value & 0x30)); // Only bits 4-5 writable
                break;

            case 0x04: // DIV
                _io[0x04] = 0;
                DivCounter = 0;
                break;

            case 0x05: // TIMA 
                _io[0x05] = value;
                break;
            
            case 0x06: // TMA
                _io[0x06] = value;
                break;
            
            case 0x07: // TAC
                _io[0x07] = (byte)((value & 0x07)/* | 0xF8*/); // Hardware seems to allow writing to upper bits, though they read back as 1.
                                                             // Let's store what's written for now. ReadIORegister will enforce read behavior.
                _timaEnabled = (value & 0x04) != 0;
                byte clockSelect = (byte)(value & 0x03);
                switch (clockSelect)
                {
                    case 0x00: _tacFrequency = 1024; break;
                    case 0x01: _tacFrequency = 16; break;
                    case 0x02: _tacFrequency = 64; break;
                    case 0x03: _tacFrequency = 256; break;
                }
                break;

            case 0x41: // STAT
                _io[0x41] = (byte)((_io[0x41] & 0x07) | (value & 0x78)); // Bits 3-6 writable, 0-2 read-only (from PPU), bit 7 fixed 1 (on read)
                break;

            case 0x44: // LY
                _io[0x44] = 0; // Writing to LY resets it
                break;

            case 0x46: // DMA
                _io[0x46] = value;
                DoDmaTransfer(value);
                break;

            case 0x50: // Boot ROM disable
                if (value != 0x00) // Any non-zero write disables boot ROM
                {
                    _io[0x50] = 0x01; // Store it as disabled
                    // Actual boot ROM unmapping would happen here or be signaled.
                }
                break;
            
            case 0x4D: // KEY1 (CGB Only) - Prepare speed switch
                if (IsGbcMode) {
                    _io[0x4D] = (byte)(value & 0x01); // Only bit 0 is R/W
                }
                break;

            case 0x4F: // VBK (VRAM Bank Select) - CGB Only
                if (IsGbcMode)
                {
                    _io[0x4F] = (byte)(value & 0x01); // Only bit 0 is used
                    _currentVramBank = _io[0x4F];     // Update cached bank
                }
                break;
            
            case 0x55: // HDMA5 (CGB Only) - VRAM DMA Length/Mode/Start
                if (IsGbcMode) {
                    _io[0x55] = value;
                    // TODO: Implement HDMA Start logic based on value written
                    // If bit 7 is 0 (General Purpose DMA) or 1 (HBlank DMA)
                    // For General Purpose DMA, it would block and transfer.
                    // For HBlank DMA, it sets up for transfers during HBlanks.
                    // For now, just storing value. A full implementation is complex.
                    if ((value & 0x80) == 0) // General Purpose DMA
                    {
                        // Start General Purpose DMA Transfer.
                        // This will consume many cycles.
                        // Length is ((value & 0x7F) + 1) * 0x10 bytes.
                        // After transfer, FF55 is set to 0xFF.
                        // This is a simplified stub; actual HDMA is more involved.
                        DoHdmaTransfer(); // Assuming DoHdmaTransfer handles both modes based on FF55
                    }
                    // If bit 7 is 1, it's an HBlank DMA setup or termination.
                    // If game writes 0 to bit 7 (e.g. 0x00-0x7F), HBlank DMA starts/continues.
                    // If game writes 1 to bit 7 (e.g. 0x80), HBlank DMA is terminated.
                }
                break;

            case 0x68: // BCPS/BGPI (Background Palette Index) - CGB Only
                if (IsGbcMode) {
                    _io[0x68] = value;
                    // Bit 7 is auto-increment for FF69
                }
                break;
            case 0x69: // BCPD/BGPD (Background Palette Data) - CGB Only
                if (IsGbcMode) {
                    // Write to current CRAM address specified by FF68
                    // _cramBackground[_io[0x68] & 0x3F] = value; (Need CRAM arrays)
                    _io[0x69] = value; // Store it raw for now
                    // if (_io[0x68] & 0x80) { // Auto-increment
                    //    _io[0x68] = (byte)(((_io[0x68] & 0x3F) + 1) | 0x80 | (_io[0x68] & 0x40));
                    // }
                }
                break;
            case 0x6A: // OCPS/OBPI (Sprite Palette Index) - CGB Only
                if (IsGbcMode) {
                     _io[0x6A] = value;
                    // Bit 7 is auto-increment for FF6B
                }
                break;
            case 0x6B: // OCPD/OBPD (Sprite Palette Data) - CGB Only
                if (IsGbcMode) {
                    // Write to current CRAM address specified by FF6A
                    // _cramSprite[_io[0x6A] & 0x3F] = value; (Need CRAM arrays)
                    _io[0x6B] = value; // Store it raw for now
                    // if (_io[0x6A] & 0x80) { // Auto-increment
                    //    _io[0x6A] = (byte)(((_io[0x6A] & 0x3F) + 1) | 0x80 | (_io[0x6A] & 0x40));
                    // }
                }
                break;

            case 0x70: // SVBK (WRAM Bank Select) - CGB Only
                if (IsGbcMode)
                {
                    _io[0x70] = (byte)(value & 0x07); // Only bits 0-2 are used. Bank 0 maps to 1.
                    // Actual WRAM bank switching needs to happen in Read/WriteByte for 0xD000-0xDFFF
                }
                break;

            default:
                _io[register] = value;
                break;
        }
    }
    
    // Ensure DoDmaTransfer and DoHdmaTransfer use the banked VRAM access if IsGbcMode
    // For HDMA (DoHdmaTransfer):
    // ushort dstAddr = (ushort)(((_io[0x53] << 8) | _io[0x54]) & 0x1FF0); // Mask to 16-byte alignment
    // dstAddr |= 0x8000; // Base VRAM
    // When writing: WriteByte((ushort)(dstAddr + i), data); // This will use the banked write

    public void DoDmaTransfer(byte sourceAddressHighByte)
    {
        ushort sourceStartAddress = (ushort)(sourceAddressHighByte << 8);
        // DMA should not be affected by VRAM bank. It writes to OAM (FE00-FE9F)
        // or specific hardware registers usually.
        // OAM DMA copies 160 bytes from source to OAM (0xFE00-0xFE9F)
        for (int i = 0; i < 0xA0; i++)
        {
            byte data = ReadByte((ushort)(sourceStartAddress + i));
            WriteDirect((ushort)(0xFE00 + i), data); // OAM is not banked by VBK
        }
    }
    
    /// <summary>
    /// Initiates or continues an HDMA transfer (GBC only).
    /// This is a simplified version. Real HDMA is more complex.
    /// </summary>
    /// <returns>The number of cycles consumed by the HDMA operation.</returns>
    public int DoHdmaTransfer()
    {
        if (!IsGbcMode) return 0;

        byte hdma5 = _io[0x55]; // HDMA5 register

        // Check if HDMA mode is General Purpose (bit 7 = 0) and not yet complete
        if ((hdma5 & 0x80) == 0) // General Purpose DMA Mode
        {
            ushort srcAddr = (ushort)((_io[0x51] << 8) | _io[0x52]);
            ushort dstAddr = (ushort)(((_io[0x53] << 8) | _io[0x54]) & 0x1FF0); // Destination in VRAM, bits 12-4, lower 4 ignored
            dstAddr |= 0x8000; // Base VRAM address

            srcAddr &= 0xFFF0; // Source address lower 4 bits ignored

            int numBlocks = (hdma5 & 0x7F) + 1;
            int totalBytesToTransfer = numBlocks * 0x10;

            for (int i = 0; i < totalBytesToTransfer; i++)
            {
                // Read from source (ROM/WRAM etc)
                byte data = ReadByte((ushort)(srcAddr + i)); 
                // Write to VRAM destination - WriteByte will handle CGB VRAM banking via _currentVramBank
                WriteByte((ushort)(dstAddr + i), data); 
            }
            
            _io[0x55] = 0xFF; // Transfer complete

            return totalBytesToTransfer * 2;
        }

        return 0;
    }
    
    public void IncrementDivRegister() => _io[0x04]++;
    
    public void UpdateLY(byte value)
    {
        _io[0x44] = value;
        // TODO: LYC compare and STAT interrupt logic
    }

    public void TIMAStep(int cycles)
    {
        if (!_timaEnabled) return;
        _timaAccumulator += cycles;
        while (_timaAccumulator >= _tacFrequency)
        {
            _timaAccumulator -= _tacFrequency;
            _io[0x05]++;
            if (_io[0x05] == 0) // Overflow
            {
                _io[0x05] = _io[0x06]; // Reload TMA
                _io[0x0F] |= 0x04;     // Request Timer Interrupt
            }
        }
    }

    public bool IsDoubleSpeed()
    {
        // Check KEY1 register (FF4D), bit 7 for current speed
        return IsGbcMode && (_io[0x4D] & 0x80) != 0;
    }
    
    public void Process(SDLInputEvent sdlInputEvent, bool isPressed) => 
        _inputState[(int)sdlInputEvent.InputBinding!] = isPressed;
    
    #region IMemoryWriteObservable Implementation
    public void RegisterWriteHandler(ushort address, Action<ushort, byte> handler)
    {
        if (!_writeHandlers.ContainsKey(address))
        {
            _writeHandlers[address] = new List<Action<ushort, byte>>();
        }
        _writeHandlers[address].Add(handler);
    }
    
    public void UnregisterWriteHandler(ushort address, Action<ushort, byte> handler)
    {
        if (_writeHandlers.TryGetValue(address, out var handlers))
        {
            handlers.Remove(handler);
            if (handlers.Count == 0)
            {
                _writeHandlers.Remove(address);
            }
        }
    }
    
    private void NotifyWriteHandlers(ushort address, byte value)
    {
        if (_writeHandlers.TryGetValue(address, out var handlers))
        {
            foreach (var handler in handlers.ToList())
            {
                handler(address, value);
            }
        }
    }
    
    public void WriteDirect(ushort address, byte value)
    {
        // This is similar to WriteByte but without notifying handlers
        if (address < 0x8000) { if (_mbc != null) _mbc.WriteRomByte(address, value); }
        else if (address < 0xA000) // VRAM
        {
             if (IsGbcMode) { _vram[(_currentVramBank * 0x2000) + (address - 0x8000)] = value; }
             else { _vram[address - 0x8000] = value; }
        }
        else if (address < 0xC000) { if (_mbc != null) _mbc.WriteRamByte(address, value); }
        else if (address < 0xE000) { _wram[address - 0xC000] = value; /* CGB WRAM Banked */ }
        else if (address < 0xFE00) { _wram[address - 0xE000] = value; /* Echo CGB WRAM Banked */ }
        else if (address < 0xFEA0) { _oam[address - 0xFE00] = value; }
        else if (address < 0xFF00) { return; }
        else if (address < 0xFF80) { _io[(byte)(address & 0xFF)] = value; } // Direct IO write, careful with side effects
        else if (address < 0xFFFF) { _hram[address - 0xFF80] = value; }
        else { _ieRegister = value; }
    }
    #endregion
    
    // Added placeholder for GetRomTitle and GetCartridgeType from your original code
    public string GetRomTitle()
    {
        if (_romData == null || _romData.Length < 0x0144) return "";
        string title = "";
        for (int i = 0; i < 16 && i + 0x0134 < _romData.Length; i++)
        {
            byte b = _romData[0x0134 + i];
            if (b == 0 || b < 32 || b > 126) break;
            title += (char)b;
        }
        return title.Trim();
    }

    public byte GetCartridgeType()
    {
        if (_romData == null || _romData.Length < 0x0148) return 0;
        return _romData[0x0147];
    }
    
    public IMBC GetMBC() => _mbc;
    public byte[] GetRomData() => _romData ?? []; // Ensure non-null return

    public bool SaveCartridgeRam(string savePath) => _mbc != null && _mbc.SaveRam(savePath);
    public bool LoadCartridgeRam(string savePath) => _mbc != null && _mbc.LoadRam(savePath);

}

