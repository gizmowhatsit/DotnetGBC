using System;
using System.IO;

namespace DotnetGBC.Cartridge
{
    /// <summary>
    /// MBC3 Memory Bank Controller implementation.
    /// Supports up to 2MByte ROM and/or 32KByte RAM with Real Time Clock functionality.
    /// </summary>
    public class MBC3 : IMBC
    {
        private byte[] _romData;
        private byte[] _ramData;

        private bool _ramAndTimerEnabled;
        private byte _romBankNumber;     // 7-bit register for ROM bank (0x00-0x7F)
        private byte _ramBankOrRtcSelect; // Selects RAM bank (0x00-0x07) or RTC register (0x08-0x0C)
        
        private RTCData _currentRtc;
        private RTCData _latchedRtc;
        private byte _latchSequenceState; // For 0x00 -> 0x01 sequence

        private int _currentRomBankUpper; // Bank selected by _romBankNumber for 0x4000-0x7FFF
        private int _currentRamBank;      // Effective RAM bank for 0xA000-0xBFFF

        private int _romBankMask;
        private int _ramBankMask;

        public bool HasBattery { get; }
        public bool HasRTC { get; }

        public MBC3(byte[] ramData, bool hasBattery, bool hasRTC)
        {
            _ramData = ramData ?? new byte[0]; // Pan Docs implies MBC3 can have 0 RAM even with RTC
            HasBattery = hasBattery;
            HasRTC = hasRTC;
            
            if (HasRTC)
            {
                _currentRtc = RTCData.Zero; //
                _latchedRtc = RTCData.Zero; //
            }

            Reset();
        }

        // Initialize is called by the factory AFTER the constructor
        public void Initialize(byte[] romData)
        {
            _romData = romData ?? throw new ArgumentNullException(nameof(romData), "ROM data cannot be null.");
            if (_romData.Length == 0)
                throw new ArgumentException("ROM data cannot be empty.", nameof(romData));
            
            // Calculate ROM size and masks
            int romSizeInBytes = _romData.Length;
            if (romSizeInBytes == 0) throw new ArgumentException("ROM data cannot be empty.", nameof(romData));
            int numRomBanks = Math.Max(2, romSizeInBytes / 0x4000); // Min 2 banks (32KB)
            _romBankMask = numRomBanks - 1;
            
            // Calculate RAM size and masks
            if (_ramData.Length > 0)
            {
                // MBC3 supports up to 32KB RAM (4 banks of 8KB)
                // Pan Docs: "RAM Bank 00-07", but typical max is 32KB (4 banks).
                // Your RTCData.cs seems to handle up to 4 banks.
                int numRamBanks = Math.Max(1, _ramData.Length / 0x2000); // 8KB per bank
                _ramBankMask = numRamBanks > 1 ? numRamBanks - 1 : 0;
            }
            else
            {
                _ramBankMask = 0;
            }
        }

        public void Reset()
        {
            _ramAndTimerEnabled = false;
            _romBankNumber = 0x01; // Default to bank 1, writing 0 also results in bank 1
            _ramBankOrRtcSelect = 0x00; // Default to RAM bank 0 or RTC register 0x08 if mapped
            _latchSequenceState = 0xFF; // Initial state, not 0x00 or 0x01

            if (HasRTC)
            {
                // On reset, RTC time itself isn't reset if battery-backed.
                // Latched values might be cleared or reflect current time.
                // For simplicity, let's ensure latched is same as current after potential RTC update.
                _currentRtc.Update(); //
                _latchedRtc = _currentRtc;
            }
            UpdateBanks();
        }

        private void UpdateBanks()
        {
            // ROM Bank for 0x4000-0x7FFF
            _currentRomBankUpper = _romBankNumber & 0x7F; // 7-bit ROM bank number
            if (_currentRomBankUpper == 0x00)
            {
                _currentRomBankUpper = 0x01; //
            }
            _currentRomBankUpper &= _romBankMask; // Apply mask based on actual ROM size

            // RAM Bank for 0xA000-0xBFFF
            if (_ramBankOrRtcSelect <= 0x07) // Pan Docs: "$00-$07 The corresponding RAM Bank"
            {
                // Typical MBC3 RAM size is up to 32KB (4 banks: 0-3)
                _currentRamBank = _ramBankOrRtcSelect & _ramBankMask;
            }
            else
            {
                // RTC register selected, no RAM bank accessible here.
                _currentRamBank = 0; 
            }
        }

        public byte ReadRomByte(ushort address)
        {
            if (address < 0x4000)
            {
                // ROM Bank 00 (Always Bank 0)
                return address < _romData.Length ? _romData[address] : (byte)0xFF;
            }
            else // 0x4000 - 0x7FFF
            {
                // Switchable ROM Bank
                int mappedAddress = (_currentRomBankUpper * 0x4000) + (address - 0x4000);
                return mappedAddress < _romData.Length ? _romData[mappedAddress] : (byte)0xFF;
            }
        }

        public void WriteRomByte(ushort address, byte value)
        {
            if (address < 0x2000) // 0000-1FFF: RAM and Timer Enable
            {
                _ramAndTimerEnabled = (value & 0x0F) == 0x0A; //
            }
            else if (address < 0x4000) // 2000-3FFF: ROM Bank Number
            {
                _romBankNumber = (byte)(value & 0x7F); // Lower 7 bits
                UpdateBanks();
            }
            else if (address < 0x6000) // 4000-5FFF: RAM Bank Number or RTC Register Select
            {
                _ramBankOrRtcSelect = value; // Value can be 0x00-0x07 for RAM or 0x08-0x0C for RTC
                UpdateBanks();
            }
            else // 6000-7FFF: Latch Clock Data
            {
                if (HasRTC)
                {
                    if (_latchSequenceState == 0x00 && value == 0x01)
                    {
                        _currentRtc.Update(); // Ensure current time is up-to-date before latching
                        _latchedRtc = _currentRtc;
                    }
                    _latchSequenceState = value;
                }
            }
        }

        public byte ReadRamByte(ushort address)
        {
            if (!_ramAndTimerEnabled) return 0xFF;

            // ushort offset = (ushort)(address - 0xA000); // Offset within 0xA000-0xBFFF

            if (_ramBankOrRtcSelect >= 0x08 && _ramBankOrRtcSelect <= 0x0C) // RTC Register selected
            {
                if (!HasRTC) return 0xFF; // No RTC, nothing to read

                // Pan Docs: "This provides a way to read the RTC registers while the clock keeps ticking."
                // This implies reads are from latched values.
                // _currentRtc.Update(); // RTCData.Update in your file doesn't advance time if Halted.
                                      // No need to call Update() here, as we are reading latched values.

                switch (_ramBankOrRtcSelect)
                {
                    case 0x08: return _latchedRtc.Seconds; //
                    case 0x09: return _latchedRtc.Minutes; //
                    case 0x0A: return _latchedRtc.Hours;   //
                    case 0x0B: return _latchedRtc.DaysLow; //
                    case 0x0C: return _latchedRtc.DaysHigh; //
                    default: return 0xFF; // Should not happen given the range check
                }
            }
            else // RAM Bank selected (_ramBankOrRtcSelect <= 0x07)
            {
                if (_ramData.Length == 0) return 0xFF; // No RAM

                int ramAddress = (_currentRamBank * 0x2000) + (address - 0xA000);
                return ramAddress < _ramData.Length ? _ramData[ramAddress] : (byte)0xFF;
            }
        }

        public void WriteRamByte(ushort address, byte value)
        {
            if (!_ramAndTimerEnabled) return;

            // ushort offset = (ushort)(address - 0xA000);

            if (_ramBankOrRtcSelect >= 0x08 && _ramBankOrRtcSelect <= 0x0C) // RTC Register selected
            {
                if (!HasRTC) return; // No RTC, nothing to write

                // Pan Docs: "The Halt Flag is supposed to be set before writing to the RTC Registers."
                // This check should ideally be done by the game software.
                // if (!_currentRtc.Halted) { Console.WriteLine("Warning: Writing to RTC while not halted."); }

                // Writes should affect the actual _currentRtc, not the latched one.
                // But first, ensure _currentRtc's time base (LastUpdate) is current if it was running.
                if (!_currentRtc.Halted) //
                {
                     _currentRtc.Update(); // Bring elapsed time into Seconds, Minutes, etc. before overwriting them.
                }
               
                switch (_ramBankOrRtcSelect)
                {
                    case 0x08: _currentRtc.Seconds = (byte)(value % 60); break; // Value is 0-59
                    case 0x09: _currentRtc.Minutes = (byte)(value % 60); break; // Value is 0-59
                    case 0x0A: _currentRtc.Hours = (byte)(value % 24); break;   // Value is 0-23
                    case 0x0B: _currentRtc.DaysLow = value; break; //
                    case 0x0C:
                        // RTC DH: Bit 0: Day Counter MSB, Bit 6: Halt, Bit 7: Carry
                        // Game writes to Day MSB and Halt. Carry is an overflow flag.
                        // Preserve current carry bit, apply new Day MSB and Halt from value.
                        _currentRtc.DaysHigh = (byte)((_currentRtc.DaysHigh & 0x80) | (value & 0x41)); // Keep Carry, set Halt and Day MSB from value
                        break;
                }
                // After writing to RTC registers, its state has been set externally.
                // Update LastUpdate to "now" so future Update() calls calculate elapsed time from this point.
                _currentRtc.LastUpdate = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); //
            }
            else // RAM Bank selected
            {
                if (_ramData.Length == 0) return;

                int ramAddress = (_currentRamBank * 0x2000) + (address - 0xA000);
                if (ramAddress < _ramData.Length)
                {
                    _ramData[ramAddress] = value;
                }
            }
        }

        public bool SaveRam(string savePath)
        {
            if (!HasBattery) return false; 

            try
            {
                if (_ramData.Length > 0)
                {
                    File.WriteAllBytes(savePath, _ramData);
                }

                if (HasRTC)
                {
                    // Ensure RTC is current before saving, only if not halted.
                    if (!_currentRtc.Halted) _currentRtc.Update(); //
                    
                    string rtcPath = savePath + ".rtc";
                    using (BinaryWriter writer = new BinaryWriter(File.Open(rtcPath, FileMode.Create)))
                    {
                        writer.Write(_currentRtc.Seconds);
                        writer.Write(_currentRtc.Minutes);
                        writer.Write(_currentRtc.Hours);
                        writer.Write(_currentRtc.DaysLow);
                        writer.Write(_currentRtc.DaysHigh);
                        writer.Write(_currentRtc.LastUpdate); // Use LastUpdate from user's RTCData
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error saving MBC3 RAM/RTC: {ex.Message}");
                return false;
            }
        }

        public bool LoadRam(string savePath)
        {
            if (!HasBattery) return false;

            bool loadedSomething = false;

            try
            {
                if (_ramData.Length > 0 && File.Exists(savePath))
                {
                    byte[] saveData = File.ReadAllBytes(savePath);
                    int bytesToCopy = Math.Min(saveData.Length, _ramData.Length);
                    Array.Copy(saveData, 0, _ramData, 0, bytesToCopy);
                    loadedSomething = true;
                }

                if (HasRTC)
                {
                    string rtcPath = savePath + ".rtc";
                    if (File.Exists(rtcPath))
                    {
                        using (BinaryReader reader = new BinaryReader(File.Open(rtcPath, FileMode.Open)))
                        {
                            _currentRtc.Seconds = reader.ReadByte();
                            _currentRtc.Minutes = reader.ReadByte();
                            _currentRtc.Hours = reader.ReadByte();
                            _currentRtc.DaysLow = reader.ReadByte();
                            _currentRtc.DaysHigh = reader.ReadByte();
                            _currentRtc.LastUpdate = reader.ReadInt64(); // Use LastUpdate
                        }
                        // Advance RTC to current time based on loaded timestamp and current time, if not halted.
                        if (!_currentRtc.Halted) _currentRtc.Update(); //
                        
                        _latchedRtc = _currentRtc; // Latch the freshly loaded & updated time
                        loadedSomething = true;
                    }
                }
                return loadedSomething;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading MBC3 RAM/RTC: {ex.Message}");
                return false;
            }
        }

        public bool IsRamEnabled => _ramAndTimerEnabled;
        public int CurrentRomBank => _currentRomBankUpper; 
        public int CurrentRamBank => (_ramBankOrRtcSelect <= 0x07 && _ramData.Length > 0) ? _currentRamBank : -1;
        public int TotalRomBanks => _romData.Length / 0x4000;
        public int TotalRamBanks => _ramData.Length > 0 ? Math.Max(1, _ramData.Length / 0x2000) : 0;

        public RTCData RTC
        {
            get
            {
                if (HasRTC)
                {
                    if (!_currentRtc.Halted) _currentRtc.Update(); //
                    return _currentRtc;
                }
                return RTCData.Zero; //
            }
            set
            {
                if (HasRTC)
                {
                    _currentRtc = value;
                    // Update internal timestamp as RTC state was set externally
                    _currentRtc.LastUpdate = DateTimeOffset.UtcNow.ToUnixTimeSeconds();  //
                    _latchedRtc = _currentRtc; 
                }
            }
        }
    }
}

