using DotnetGBC.Memory;

namespace DotnetGBC.FileSystem;

/// <summary>
/// Handles loading ROM files and extracting ROM metadata for the Game Boy emulator.
/// </summary>
public class RomLoader
{
    // ROM header address constants
    private const int TITLE_ADDRESS = 0x0134;
    private const int TITLE_LENGTH = 16; // Max length
    private const int CGB_FLAG_ADDRESS = 0x0143;
    private const int CARTRIDGE_TYPE_ADDRESS = 0x0147;
    private const int ROM_SIZE_ADDRESS = 0x0148;
    private const int RAM_SIZE_ADDRESS = 0x0149;
    private const int HEADER_CHECKSUM_ADDRESS = 0x014D;

    // Known cartridge types
    private static readonly Dictionary<byte, string> CartridgeTypes = new()
    {
        { 0x00, "ROM ONLY" },
        { 0x01, "MBC1" },
        { 0x02, "MBC1+RAM" },
        { 0x03, "MBC1+RAM+BATTERY" },
        { 0x05, "MBC2" },
        { 0x06, "MBC2+BATTERY" },
        { 0x08, "ROM+RAM" },
        { 0x09, "ROM+RAM+BATTERY" },
        { 0x0B, "MMM01" },
        { 0x0C, "MMM01+RAM" },
        { 0x0D, "MMM01+RAM+BATTERY" },
        { 0x0F, "MBC3+TIMER+BATTERY" },
        { 0x10, "MBC3+TIMER+RAM+BATTERY" },
        { 0x11, "MBC3" },
        { 0x12, "MBC3+RAM" },
        { 0x13, "MBC3+RAM+BATTERY" },
        { 0x19, "MBC5" },
        { 0x1A, "MBC5+RAM" },
        { 0x1B, "MBC5+RAM+BATTERY" },
        { 0x1C, "MBC5+RUMBLE" },
        { 0x1D, "MBC5+RUMBLE+RAM" },
        { 0x1E, "MBC5+RUMBLE+RAM+BATTERY" },
        { 0x20, "MBC6" },
        { 0x22, "MBC7+SENSOR+RUMBLE+RAM+BATTERY" },
        { 0xFC, "POCKET CAMERA" },
        { 0xFD, "BANDAI TAMA5" },
        { 0xFE, "HuC3" },
        { 0xFF, "HuC1+RAM+BATTERY" }
    };

    // ROM sizes
    private static readonly Dictionary<byte, int> RomSizes = new()
    {
        { 0x00, 32 * 1024 }, // 32KB (no banking)
        { 0x01, 64 * 1024 }, // 64KB (4 banks)
        { 0x02, 128 * 1024 }, // 128KB (8 banks)
        { 0x03, 256 * 1024 }, // 256KB (16 banks)
        { 0x04, 512 * 1024 }, // 512KB (32 banks)
        { 0x05, 1024 * 1024 }, // 1MB (64 banks)
        { 0x06, 2 * 1024 * 1024 }, // 2MB (128 banks)
        { 0x07, 4 * 1024 * 1024 }, // 4MB (256 banks)
        { 0x08, 8 * 1024 * 1024 }, // 8MB (512 banks)
        { 0x52, 1179648 }, // 1.1MB (72 banks)
        { 0x53, 1310720 }, // 1.2MB (80 banks)
        { 0x54, 1572864 }  // 1.5MB (96 banks)
    };

    // RAM sizes
    private static readonly Dictionary<byte, int> RamSizes = new()
    {
        { 0x00, 0 }, // None
        { 0x01, 2 * 1024 }, // 2KB (1 bank)
        { 0x02, 8 * 1024 }, // 8KB (1 bank)
        { 0x03, 32 * 1024 }, // 32KB (4 banks of 8KB)
        { 0x04, 128 * 1024 }, // 128KB (16 banks of 8KB)
        { 0x05, 64 * 1024 }  // 64KB (8 banks of 8KB)
    };

    /// <summary>
    /// Loads a ROM file from the specified path.
    /// </summary>
    /// <param name="filePath">The path to the ROM file.</param>
    /// <returns>The ROM data as a byte array.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the ROM file does not exist.</exception>
    /// <exception cref="IOException">Thrown if there is an error reading the ROM file.</exception>
    public byte[] LoadRom(string filePath)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("ROM file not found.", filePath);
        }

        return File.ReadAllBytes(filePath);
    }

    /// <summary>
    /// Loads a ROM file and initializes the MMU with it.
    /// </summary>
    /// <param name="filePath">The path to the ROM file.</param>
    /// <param name="mmu">The MMU to load the ROM into.</param>
    /// <returns>ROM metadata extracted from the header.</returns>
    /// <exception cref="FileNotFoundException">Thrown if the ROM file does not exist.</exception>
    /// <exception cref="IOException">Thrown if there is an error reading the ROM file.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the ROM header is invalid.</exception>
    public RomMetadata LoadRomIntoMMU(string filePath, MMU mmu)
    {
        byte[] romData = LoadRom(filePath);
            
        // Validate ROM size
        if (romData.Length < 0x150)
        {
            throw new InvalidOperationException("Invalid ROM file: ROM is too small to contain a valid header.");
        }

        // Extract metadata from the ROM header
        RomMetadata metadata = ExtractMetadata(romData);

        // Validate header checksum
        if (!ValidateHeaderChecksum(romData))
        {
            throw new InvalidOperationException("Invalid ROM header: Checksum verification failed.");
        }

        // Load the ROM into the MMU
        bool loadSuccess = mmu.LoadRom(romData);
        if (!loadSuccess)
        {
            throw new InvalidOperationException("Failed to load ROM into MMU.");
        }

        return metadata;
    }

    /// <summary>
    /// Extracts metadata from the ROM header.
    /// </summary>
    /// <param name="romData">The ROM data.</param>
    /// <returns>The extracted ROM metadata.</returns>
    public RomMetadata ExtractMetadata(byte[] romData)
    {
        // Extract the title (trim trailing zeros and non-ASCII chars)
        string title = ExtractTitle(romData);

        // Get cartridge type
        byte cartridgeTypeByte = romData[CARTRIDGE_TYPE_ADDRESS];
        string cartridgeType = CartridgeTypes.TryGetValue(cartridgeTypeByte, out string type) 
            ? type 
            : $"UNKNOWN (0x{cartridgeTypeByte:X2})";

        // Get ROM and RAM sizes
        byte romSizeByte = romData[ROM_SIZE_ADDRESS];
        int romSize = RomSizes.TryGetValue(romSizeByte, out int rSize) 
            ? rSize 
            : 32 * 1024; // Default to 32KB if unknown

        byte ramSizeByte = romData[RAM_SIZE_ADDRESS];
        int ramSize = RamSizes.TryGetValue(ramSizeByte, out int raSize) 
            ? raSize 
            : 0; // Default to 0 if unknown

        // Check for GBC compatibility
        byte cgbFlag = romData[CGB_FLAG_ADDRESS];
        bool isGbcCompatible = (cgbFlag == 0x80 || cgbFlag == 0xC0);
        bool isGbcOnly = (cgbFlag == 0xC0);

        // Create metadata object
        return new RomMetadata
        {
            Title = title,
            CartridgeType = cartridgeType,
            CartridgeTypeByte = cartridgeTypeByte,
            RomSize = romSize,
            RomSizeByte = romSizeByte,
            RamSize = ramSize,
            RamSizeByte = ramSizeByte,
            IsGbcCompatible = isGbcCompatible,
            IsGbcOnly = isGbcOnly,
            Filename = Path.GetFileName(romData.ToString()) // This will be replaced with the actual filename in LoadRomIntoMMU
        };
    }

    /// <summary>
    /// Extracts the title from the ROM header.
    /// </summary>
    /// <param name="romData">The ROM data.</param>
    /// <returns>The ROM title.</returns>
    private string ExtractTitle(byte[] romData)
    {
        // Extract the title bytes
        byte[] titleBytes = new byte[TITLE_LENGTH];
        Array.Copy(romData, TITLE_ADDRESS, titleBytes, 0, TITLE_LENGTH);

        // Convert to string and trim trailing zeros and non-ASCII chars
        string title = "";
        
        foreach (byte b in titleBytes)
        {
            // Stop at the first zero or non-ASCII character
            if (b is 0 or < 32 or > 126)
                break;
                
            title += (char)b;
        }

        return title.Trim();
    }

    /// <summary>
    /// Validates the ROM header checksum.
    /// </summary>
    /// <param name="romData">The ROM data.</param>
    /// <returns>True if the checksum is valid; otherwise, false.</returns>
    private bool ValidateHeaderChecksum(byte[] romData)
    {
        byte storedChecksum = romData[HEADER_CHECKSUM_ADDRESS];
        byte calculatedChecksum = 0;

        // Calculate checksum over header area (0x0134-0x014C)
        for (int i = 0x0134; i <= 0x014C; i++)
        {
            calculatedChecksum = (byte)(calculatedChecksum - romData[i] - 1);
        }

        return calculatedChecksum == storedChecksum;
    }

    /// <summary>
    /// Gets the expected ROM size based on the size byte in the header.
    /// </summary>
    /// <param name="romSizeByte">The ROM size byte from the header.</param>
    /// <returns>The expected ROM size in bytes, or -1 if unknown.</returns>
    public int GetExpectedRomSize(byte romSizeByte)
    {
        return RomSizes.TryGetValue(romSizeByte, out int size) ? size : -1;
    }

    /// <summary>
    /// Gets the expected RAM size based on the size byte in the header.
    /// </summary>
    /// <param name="ramSizeByte">The RAM size byte from the header.</param>
    /// <returns>The expected RAM size in bytes, or -1 if unknown.</returns>
    public int GetExpectedRamSize(byte ramSizeByte)
    {
        return RamSizes.TryGetValue(ramSizeByte, out int size) ? size : -1;
    }

    /// <summary>
    /// Determines if a cartridge type supports battery-backed RAM (save files).
    /// </summary>
    /// <param name="cartridgeTypeByte">The cartridge type byte from the header.</param>
    /// <returns>True if the cartridge supports battery-backed RAM; otherwise, false.</returns>
    public bool SupportsBatteryBackup(byte cartridgeTypeByte)
    {
        // Cartridge types with battery backup have "BATTERY" in their description
        return CartridgeTypes.TryGetValue(cartridgeTypeByte, out string type) &&
               type.Contains("BATTERY");
    }
}

/// <summary>
/// Contains metadata extracted from a ROM header.
/// </summary>
public class RomMetadata
{
    /// <summary>
    /// Gets or sets the ROM title.
    /// </summary>
    public string Title { get; set; }

    /// <summary>
    /// Gets or sets the ROM filename.
    /// </summary>
    public string? Filename { get; set; }

    /// <summary>
    /// Gets or sets the cartridge type description.
    /// </summary>
    public string CartridgeType { get; set; }

    /// <summary>
    /// Gets or sets the cartridge type byte from the header.
    /// </summary>
    public byte CartridgeTypeByte { get; set; }

    /// <summary>
    /// Gets or sets the ROM size in bytes.
    /// </summary>
    public int RomSize { get; set; }

    /// <summary>
    /// Gets or sets the ROM size byte from the header.
    /// </summary>
    public byte RomSizeByte { get; set; }

    /// <summary>
    /// Gets or sets the RAM size in bytes.
    /// </summary>
    public int RamSize { get; set; }

    /// <summary>
    /// Gets or sets the RAM size byte from the header.
    /// </summary>
    public byte RamSizeByte { get; set; }

    /// <summary>
    /// Gets or sets whether the ROM is Game Boy Color compatible.
    /// </summary>
    public bool IsGbcCompatible { get; set; }

    /// <summary>
    /// Gets or sets whether the ROM is Game Boy Color only.
    /// </summary>
    public bool IsGbcOnly { get; set; }

    /// <summary>
    /// Gets whether the ROM uses a Memory Bank Controller.
    /// </summary>
    public bool UsesMBC => CartridgeTypeByte != 0x00;

    /// <summary>
    /// Gets whether the ROM supports save files (battery-backed RAM).
    /// </summary>
    public bool SupportsSaves => CartridgeType.Contains("BATTERY");

    /// <summary>
    /// Returns a string representation of the ROM metadata.
    /// </summary>
    /// <returns>A string representation of the ROM metadata.</returns>
    public override string ToString()
    {
        return $"Title: {Title}\n" +
               $"Cartridge Type: {CartridgeType}\n" +
               $"ROM Size: {RomSize / 1024}KB\n" +
               $"RAM Size: {(RamSize > 0 ? $"{RamSize / 1024}KB" : "None")}\n" +
               $"GBC Compatible: {(IsGbcCompatible ? "Yes" : "No")}\n" +
               $"GBC Only: {(IsGbcOnly ? "Yes" : "No")}\n" +
               $"Supports Saves: {(SupportsSaves ? "Yes" : "No")}";
    }
}

