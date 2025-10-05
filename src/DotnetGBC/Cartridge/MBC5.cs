using System;
using System.IO;

namespace DotnetGBC.Cartridge
{
    /// <summary>
    /// MBC5 Memory Bank Controller implementation.
    /// Supports up to 64 Mbits ROM (512 banks) and/or 128KByte RAM with optional rumble support.
    /// It is the first MBC guaranteed to work properly with GBC Double Speed mode.
    /// </summary>
    public class MBC5 : IMBC
    {
        // ROM and RAM data
        private byte[] _romData;
        private byte[] _ramData;

        // Banking registers
        private bool _ramEnabled;
        private byte _romBankLow;       // 8-bit register (2000-2FFF)
        private byte _romBankHigh;      // 1-bit register (3000-3FFF)
        private byte _ramBankRegister;  // 4-bit register (4000-5FFF)

        // Derived properties for bank numbers
        private int _currentRomBank;
        private int _currentRamBank;

        // Cartridge properties
        private int _romBankMask;       // Mask to apply to ROM bank number (based on ROM size)
        private int _ramBankMask;       // Mask to apply to RAM bank number (based on RAM size)
        private readonly bool _hasRumble;        // Whether this cartridge has rumble support

        /// <summary>
        /// Initialize a new MBC5 controller.
        /// The main setup, including mask calculation and Reset(), is done in the Initialize method.
        /// </summary>
        public MBC5(byte[] ramData, bool hasBattery = false, bool hasRumble = false)
        {
            Console.WriteLine("MBC5 created");
            _ramData = ramData ?? [];
            HasBattery = hasBattery;
            _hasRumble = hasRumble;
            // Reset() is called in Initialize() after masks are set.
        }

        /// <summary>
        /// Initializes the MBC with ROM data and sets up internal properties like masks.
        /// </summary>
        /// <param name="romData">The full ROM data.</param>
        public void Initialize(byte[] romData)
        {
            Console.WriteLine("[MBC5] Initializing...");
            
            _romData = romData ?? throw new ArgumentNullException(nameof(romData), "ROM data cannot be null.");

            // Calculate ROM size and mask
            // ROM banks are 16KB (0x4000 bytes)
            int numRomBanks = _romData.Length / 0x4000;
            _romBankMask = numRomBanks > 0 ? numRomBanks - 1 : 0;

            // Calculate RAM size and mask using the _ramData provided by the factory (via constructor)
            // RAM banks are 8KB (0x2000 bytes)
            if (_ramData != null && _ramData.Length > 0)
            {
                int numRamBanks = _ramData.Length / 0x2000;
                _ramBankMask = numRamBanks > 0 ? numRamBanks - 1 : 0;
            }
            else
            {
                _ramData = []; // Ensure _ramData is not null
                _ramBankMask = 0;
            }
            
            Reset(); // Call Reset() after masks and data arrays are confirmed/set up.
        }

        /// <summary>
        /// Resets the MBC to its initial state.
        /// </summary>
        public void Reset()
        {
            _ramEnabled = false;
            _romBankLow = 0x00;    // Default is 0 for MBC5, not 1 like MBC1
            _romBankHigh = 0x00;
            _ramBankRegister = 0x00;
            UpdateCurrentBanks();
        }

        /// <summary>
        /// Reads a byte from the ROM address space (0x0000-0x7FFF).
        /// </summary>
        /// <param name="address">The address to read from (0x0000-0x7FFF).</param>
        /// <returns>The byte at the mapped ROM location.</returns>
        public byte ReadRomByte(ushort address)
        {
            if (_romData.Length == 0)
                return 0xFF; // No ROM data

            if (address < 0x4000)
            {
                // ROM Bank 0 area (0000-3FFF)
                // MBC5 always maps ROM bank 0 here
                return address < _romData.Length ? _romData[address] : (byte)0xFF;
            }
            else
            {
                // ROM Bank 1-1FF area (4000-7FFF)
                // _currentRomBank is already masked correctly by UpdateCurrentBanks()
                int romAddr = _currentRomBank * 0x4000 + (address - 0x4000);
                return romAddr < _romData.Length ? _romData[romAddr] : (byte)0xFF;
            }
        }

        /// <summary>
        /// Writes a byte to the ROM address space (0x0000-0x7FFF).
        /// This writes to MBC control registers.
        /// </summary>
        /// <param name="address">The address to write to (0x0000-0x7FFF).</param>
        /// <param name="value">The value to write.</param>
        public void WriteRomByte(ushort address, byte value)
        {
            if (address < 0x2000)
            {
                // RAM Enable (0000-1FFF)
                // Writing $0A (in lower nibble) enables RAM, anything else disables it.
                _ramEnabled = (_ramData.Length > 0) && ((value & 0x0F) == 0x0A);
            }
            else if (address < 0x3000)
            {
                // ROM Bank Number Low (2000-2FFF)
                // 8 least significant bits of the ROM bank number
                _romBankLow = value;
                UpdateCurrentBanks();
            }
            else if (address < 0x4000)
            {
                // ROM Bank Number High (3000-3FFF)
                // 9th bit of the ROM bank number (only bit 0 of value is used)
                _romBankHigh = (byte)(value & 0x01);
                UpdateCurrentBanks();
            }
            else if (address < 0x6000)
            {
                // RAM Bank Number / Rumble Control (4000-5FFF)
                // Lower 4 bits are used for RAM bank.
                // If rumble is present, bit 3 of these 4 bits controls rumble.
                _ramBankRegister = (byte)(value & 0x0F);
                UpdateCurrentBanks();
                
                // if (_hasRumble) {
                //    bool rumbleActive = (_ramBankRegister & 0x08) != 0;
                //    Console.WriteLine(rumbleActive ? "Rumble ON" : "Rumble OFF");
                // }
            }
            // Writes to 6000-7FFF are ignored by MBC5.
        }

        /// <summary>
        /// Reads a byte from the external RAM address space (0xA000-0xBFFF).
        /// </summary>
        /// <param name="address">The address to read from (0xA000-0xBFFF).</param>
        /// <returns>The byte at the mapped RAM location, or 0xFF if RAM is disabled or not present.</returns>
        public byte ReadRamByte(ushort address)
        {
            if (!_ramEnabled || _ramData.Length == 0)
                return 0xFF;

            int offset = address - 0xA000; // Offset within an 8KB RAM bank
            // _currentRamBank is already masked correctly by UpdateCurrentBanks()
            int ramAddr = _currentRamBank * 0x2000 + offset;
            
            return ramAddr < _ramData.Length ? _ramData[ramAddr] : (byte)0xFF;
        }

        /// <summary>
        /// Writes a byte to the external RAM address space (0xA000-0xBFFF).
        /// </summary>
        /// <param name="address">The address to write to (0xA000-0xBFFF).</param>
        /// <param name="value">The value to write.</param>
        public void WriteRamByte(ushort address, byte value)
        {
            if (!_ramEnabled || _ramData.Length == 0)
                return;

            int offset = address - 0xA000; // Offset within an 8KB RAM bank
            // _currentRamBank is already masked correctly by UpdateCurrentBanks()
            int ramAddr = _currentRamBank * 0x2000 + offset;
            
            if (ramAddr < _ramData.Length)
                _ramData[ramAddr] = value;
        }

        /// <summary>
        /// Saves the external RAM to a file.
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
                Console.WriteLine($"[MBC5] Error saving RAM: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Loads the external RAM from a file.
        /// </summary>
        public bool LoadRam(string savePath)
        {
            if (!HasBattery || _ramData.Length == 0) // Can't load if no RAM or no battery to persist it
                return false;
            
            try
            {
                if (!File.Exists(savePath))
                    return false;
                
                byte[] saveData = File.ReadAllBytes(savePath);
                
                // Only copy as much data as we have RAM for, or as much as is in the save file
                int bytesToCopy = Math.Min(saveData.Length, _ramData.Length);
                Array.Copy(saveData, 0, _ramData, 0, bytesToCopy);
                Console.WriteLine($"[MBC5] Loaded {bytesToCopy} bytes into RAM.");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MBC5] Error loading RAM: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Updates the current ROM and RAM bank numbers based on the MBC registers.
        /// </summary>
        private void UpdateCurrentBanks()
        {
            // Calculate the effective ROM bank (9-bit value: _romBankHigh (1 bit) << 8 | _romBankLow (8 bits))
            // Max ROM bank is 0x1FF (511)
            _currentRomBank = ((_romBankHigh & 0x01) << 8) | _romBankLow;
            _currentRomBank &= _romBankMask; // Apply ROM size mask

            // Calculate the effective RAM bank
            // Max RAM bank is 0x0F (15)
            if (_hasRumble)
            {
                // Bit 3 of _ramBankRegister controls rumble, bits 0-2 select RAM bank
                _currentRamBank = _ramBankRegister & 0x07; 
            }
            else
            {
                // Bits 0-3 of _ramBankRegister select RAM bank
                _currentRamBank = _ramBankRegister & 0x0F;
            }
            _currentRamBank &= _ramBankMask; // Apply RAM size mask
        }

        /// <summary>
        /// Determines the RAM size in bytes from the size code in the ROM header.
        /// Note: This method is for reference or if MBC5 needed to self-determine RAM size.
        /// Currently, MBC5 uses the RAM buffer sized and provided by MBCFactory.
        /// According to the provided Pan Docs for MBC5, supported RAM sizes are 8KB, 32KB, and 128KB.
        /// </summary>
        /// <param name="ramSizeCode">The RAM size code from the ROM header (0x0149).</param>
        /// <returns>The RAM size in bytes.</returns>
        private int GetRamSizeFromCode(byte ramSizeCode)
        {
            switch (ramSizeCode)
            {
                case 0x00: return 0;      // No RAM
                // case 0x01: return 0x800;  // 2 KB (Not listed for MBC5 in provided Pan Docs)
                case 0x02: return 0x2000; // 8 KB (1 bank)
                case 0x03: return 0x8000; // 32 KB (4 banks of 8KB)
                case 0x04: return 0x20000;// 128 KB (16 banks of 8KB)
                // case 0x05: return 0x10000;// 64 KB (8 banks of 8KB) (Not listed for MBC5 in provided Pan Docs)
                default:   return 0;      // Unknown or unsupported for MBC5
            }
        }

        public bool HasRTC => false; // MBC5 never has an RTC

        public bool HasBattery { get; }

        public bool IsRamEnabled => _ramEnabled;

        public int CurrentRomBank => _currentRomBank;

        public int CurrentRamBank => _currentRamBank;

        public int TotalRomBanks => _romData.Length / 0x4000;

        // Corrected TotalRamBanks
        public int TotalRamBanks => _ramData.Length > 0 ? Math.Max(1, _ramData.Length / 0x2000) : 0;

        public RTCData RTC 
        { 
            get => RTCData.Zero; 
            set { /* Not used in MBC5 */ }
        }

        /// <summary>
        /// Gets whether the rumble motor is currently active (for cartridges with rumble support).
        /// Bit 3 of the RAM Bank Register (4000-5FFF) controls rumble.
        /// </summary>
        public bool IsRumbleActive => _hasRumble && (_ramBankRegister & 0x08) != 0;
    }
}

