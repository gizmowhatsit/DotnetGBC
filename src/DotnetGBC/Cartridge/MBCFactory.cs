namespace DotnetGBC.Cartridge;

/// <summary>
/// Factory class for creating Memory Bank Controller instances based on cartridge type.
/// </summary>
public static class MBCFactory
{
    /// <summary>
    /// Creates an appropriate MBC instance based on cartridge type.
    /// </summary>
    /// <param name="cartridgeType">The cartridge type byte from the ROM header (0x0147).</param>
    /// <param name="romData">The ROM data.</param>
    /// <returns>An IMBC instance appropriate for the cartridge type.</returns>
    /// <exception cref="NotSupportedException">Thrown if the cartridge type is not supported.</exception>
    public static IMBC CreateMBC(byte cartridgeType, byte[] romData)
    {
        if (romData == null || romData.Length == 0)
            throw new ArgumentException("Invalid ROM data", nameof(romData));

        // Extract some ROM information for MBC creation
        bool hasBattery = HasBattery(cartridgeType);
        bool hasRTC = HasRTC(cartridgeType);
        bool hasRumble = HasRumble(cartridgeType);
            
        // Determine RAM size from ROM header
        byte ramSizeCode = romData.Length > 0x0149 ? romData[0x0149] : (byte)0;
        int ramSize = GetRamSizeFromCode(ramSizeCode);
            
        // Create appropriately sized RAM buffer
        byte[] ramData = ramSize > 0 ? new byte[ramSize] : new byte[0];
        
        // Special case for large ROMs with MBC1
        bool isBigROM = romData.Length >= 0x100000; // 1MB
        if (IsMBC1Type(cartridgeType) && isBigROM) // Applies to ALL MBC1 types (incl. MBC1M) with >= 1MB ROM
        {
            // MBC1 carts with >= 1MB ROM use alternate wiring limiting RAM to 8KB.
            ramSize = Math.Min(ramSize, 0x2000); // Max 8KB
            ramData = ramSize > 0 ? new byte[ramSize] : new byte[0];
        }
            
        // Create the appropriate MBC based on cartridge type
        switch (cartridgeType)
        {
            // ROM ONLY
            case 0x00:
                Console.WriteLine("[MBCFactory] Initializing ROM only game with no MBC");
                return new NoMBC(ramData, hasBattery);
                
            // MBC1
            case 0x01: // MBC1
            case 0x02: // MBC1+RAM
            case 0x03: // MBC1+RAM+BATTERY
                Console.WriteLine("[MBCFactory] Initializing game with MBC1");
                return new MBC1(ramData, hasBattery, IsMBC1M(romData));
                
            // MBC2
            case 0x05: // MBC2
            case 0x06: // MBC2+BATTERY
                Console.WriteLine("[MBCFactory] Initializing game with MBC2");
                return new MBC2(hasBattery);
                
            // ROM+RAM
            case 0x08: // ROM+RAM
            case 0x09: // ROM+RAM+BATTERY
                Console.WriteLine("[MBCFactory] Initializing ROM+RAM game with no MBC");
                return new NoMBC(ramData, hasBattery);
                
            // MMM01
            case 0x0B: // MMM01
            case 0x0C: // MMM01+RAM
            case 0x0D: // MMM01+RAM+BATTERY
                throw new NotImplementedException("MMM01 is not yet implemented");
                
            // MBC3
            case 0x0F: // MBC3+TIMER+BATTERY
            case 0x10: // MBC3+TIMER+RAM+BATTERY
            case 0x11: // MBC3
            case 0x12: // MBC3+RAM
            case 0x13: // MBC3+RAM+BATTERY
                Console.WriteLine("Initializing game with MBC3");
                return new MBC3(ramData, hasBattery, hasRTC);
                
            // MBC5
            case 0x19: // MBC5
            case 0x1A: // MBC5+RAM
            case 0x1B: // MBC5+RAM+BATTERY
            case 0x1C: // MBC5+RUMBLE
            case 0x1D: // MBC5+RUMBLE+RAM
            case 0x1E: // MBC5+RUMBLE+RAM+BATTERY
                Console.WriteLine("Initializing game with MBC5");
                return new MBC5(ramData, hasBattery, hasRumble);
                
            // MBC6 & MBC7 - Will not be implemented
            case 0x20: // MBC6
            case 0x22: // MBC7+SENSOR+RUMBLE+RAM+BATTERY
                
            // Other special types
            case 0xFC: // POCKET CAMERA
            case 0xFD: // BANDAI TAMA5
            case 0xFE: // HuC3
            case 0xFF: // HuC1+RAM+BATTERY
                throw new NotSupportedException($"Cartridge type 0x{cartridgeType:X2} is not supported");

            default:
                throw new NotSupportedException($"Unknown cartridge type: 0x{cartridgeType:X2}");
        }
    }

    /// <summary>
    /// Determines if a cartridge type has battery-backed RAM.
    /// </summary>
    /// <param name="cartridgeType">The cartridge type byte.</param>
    /// <returns>True if the cartridge has battery-backed RAM; otherwise, false.</returns>
    private static bool HasBattery(byte cartridgeType)
    {
        return cartridgeType switch
        {
            0x03 => true, // MBC1+RAM+BATTERY
            0x06 => true, // MBC2+BATTERY
            0x09 => true, // ROM+RAM+BATTERY
            0x0D => true, // MMM01+RAM+BATTERY
            0x0F => true, // MBC3+TIMER+BATTERY
            0x10 => true, // MBC3+TIMER+RAM+BATTERY
            0x13 => true, // MBC3+RAM+BATTERY
            0x1B => true, // MBC5+RAM+BATTERY
            0x1E => true, // MBC5+RUMBLE+RAM+BATTERY
            0x22 => true, // MBC7+SENSOR+RUMBLE+RAM+BATTERY
            0xFF => true, // HuC1+RAM+BATTERY
            _ => false
        };
    }

    /// <summary>
    /// Determines if a cartridge type has a Real-Time Clock.
    /// </summary>
    /// <param name="cartridgeType">The cartridge type byte.</param>
    /// <returns>True if the cartridge has RTC; otherwise, false.</returns>
    private static bool HasRTC(byte cartridgeType)
    {
        return cartridgeType switch
        {
            0x0F => true, // MBC3+TIMER+BATTERY
            0x10 => true, // MBC3+TIMER+RAM+BATTERY
            _ => false
        };
    }

    /// <summary>
    /// Determines if a cartridge type has rumble support.
    /// </summary>
    /// <param name="cartridgeType">The cartridge type byte.</param>
    /// <returns>True if the cartridge has rumble; otherwise, false.</returns>
    private static bool HasRumble(byte cartridgeType)
    {
        return cartridgeType switch
        {
            0x1C => true, // MBC5+RUMBLE
            0x1D => true, // MBC5+RUMBLE+RAM
            0x1E => true, // MBC5+RUMBLE+RAM+BATTERY
            0x22 => true, // MBC7+SENSOR+RUMBLE+RAM+BATTERY
            _ => false
        };
    }

    /// <summary>
    /// Determines if a cartridge type uses MBC1.
    /// </summary>
    /// <param name="cartridgeType">The cartridge type byte.</param>
    /// <returns>True if the cartridge uses MBC1; otherwise, false.</returns>
    private static bool IsMBC1Type(byte cartridgeType)
    {
        return cartridgeType switch
        {
            0x01 => true, // MBC1
            0x02 => true, // MBC1+RAM
            0x03 => true, // MBC1+RAM+BATTERY
            _ => false
        };
    }

    /// <summary>
    /// Determines if the ROM is an MBC1M multicart.
    /// </summary>
    /// <param name="romData">The ROM data.</param>
    /// <returns>True if the ROM is an MBC1M multicart; otherwise, false.</returns>
    private static bool IsMBC1M(byte[] romData)
    {
        if (romData.Length != 0x100000) // Must be exactly 1MB
            return false;

        // Check for Nintendo copyright header in bank $10
        // Typical signature is "NINTENDO" at position 0x10104 (bank $10 + $0104)
        if (romData.Length >= 0x10134 &&
            romData[0x10104] == 'N' &&
            romData[0x10105] == 'I' &&
            romData[0x10106] == 'N' &&
            romData[0x10107] == 'T' &&
            romData[0x10108] == 'E' &&
            romData[0x10109] == 'N' &&
            romData[0x1010A] == 'D' &&
            romData[0x1010B] == 'O')
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Determines the RAM size in bytes from the size code in the ROM header.
    /// </summary>
    /// <param name="ramSizeCode">The RAM size code from the ROM header (0x0149).</param>
    /// <returns>The RAM size in bytes.</returns>
    private static int GetRamSizeFromCode(byte ramSizeCode)
    {
        return ramSizeCode switch
        {
            0x00 => 0,         // No RAM
            0x01 => 0x800,     // 2 KB
            0x02 => 0x2000,    // 8 KB
            0x03 => 0x8000,    // 32 KB (4 banks of 8KB)
            0x04 => 0x20000,   // 128 KB (16 banks of 8KB)
            0x05 => 0x10000,   // 64 KB (8 banks of 8KB)
            _ => 0,            // Unknown
        };
    }
}

