using System.Runtime.InteropServices;
using DotnetGBC.Memory;
using DotnetGBC.Threading;
using System;

namespace DotnetGBC.Graphics;

/// <summary>
/// Implementation of the Game Boy/Game Boy Color PPU
/// </summary>
public class PPU : IDisposable
{
    // Constants
    private const int GB_SCREEN_WIDTH = 160;
    private const int GB_SCREEN_HEIGHT = 144;
    private const int BYTES_PER_PIXEL = 4; // PPU Internal buffer uses RGBA layout
    private const int SCANLINE_CYCLES = 456; // T-cycles per scanline
    private const int VBLANK_START_LINE = 144;
    private const int TOTAL_LINES = 154; // Lines 0-153

    // LCD Registers addresses
    private const ushort LCDC_REG = 0xFF40; // LCD Control
    private const ushort STAT_REG = 0xFF41; // LCD Status
    private const ushort SCY_REG = 0xFF42;  // Scroll Y
    private const ushort SCX_REG = 0xFF43;  // Scroll X
    private const ushort LY_REG = 0xFF44;   // LCD Y Coordinate
    private const ushort LYC_REG = 0xFF45;  // LY Compare
    private const ushort DMA_REG = 0xFF46;  // OAM DMA Transfer
    private const ushort BGP_REG = 0xFF47;  // BG Palette
    private const ushort OBP0_REG = 0xFF48; // Object Palette 0
    private const ushort OBP1_REG = 0xFF49; // Object Palette 1
    private const ushort WY_REG = 0xFF4A;   // Window Y
    private const ushort WX_REG = 0xFF4B;   // Window X

    // GBC-specific registers
    private const ushort VBK_REG = 0xFF4F;   // VRAM Bank
    private const ushort BCPS_REG = 0xFF68;  // BG Color Palette Specification
    private const ushort BCPD_REG = 0xFF69;  // BG Color Palette Data
    private const ushort OCPS_REG = 0xFF6A;  // OBJ Color Palette Specification
    private const ushort OCPD_REG = 0xFF6B;  // OBJ Color Palette Data

    // LCD Control (LCDC) bits
    private const byte LCDC_BG_ENABLE = 0x01;       // Bit 0: BG/Window Enable/Priority (DMG) / BG Enable (GBC)
    private const byte LCDC_OBJ_ENABLE = 0x02;      // Bit 1: Sprite Enable
    private const byte LCDC_OBJ_SIZE = 0x04;        // Bit 2: Sprite Size (0=8x8, 1=8x16)
    private const byte LCDC_BG_MAP = 0x08;          // Bit 3: BG Tile Map Select (0=9800, 1=9C00)
    private const byte LCDC_TILE_DATA = 0x10;       // Bit 4: BG & Window Tile Data Select (0=8800, 1=8000)
    private const byte LCDC_WINDOW_ENABLE = 0x20;   // Bit 5: Window Enable
    private const byte LCDC_WINDOW_MAP = 0x40;      // Bit 6: Window Tile Map Select (0=9800, 1=9C00)
    private const byte LCDC_DISPLAY_ENABLE = 0x80;  // Bit 7: LCD Power / Display Enable

    // LCD Status (STAT) bits and masks
    private const byte STAT_MODE_MASK = 0x03;
    private const byte STAT_MODE_HBLANK = 0x00;     // Mode 0
    private const byte STAT_MODE_VBLANK = 0x01;     // Mode 1
    private const byte STAT_MODE_OAM = 0x02;        // Mode 2
    private const byte STAT_MODE_TRANSFER = 0x03;   // Mode 3
    private const byte STAT_LYC_EQUAL = 0x04;       // Bit 2: LYC == LY Flag
    private const byte STAT_HBLANK_INT = 0x08;      // Bit 3: Mode 0 Interrupt Enable
    private const byte STAT_VBLANK_INT = 0x10;      // Bit 4: Mode 1 Interrupt Enable
    private const byte STAT_OAM_INT = 0x20;         // Bit 5: Mode 2 Interrupt Enable
    private const byte STAT_LYC_INT = 0x40;         // Bit 6: LYC == LY Interrupt Enable

    // Frame buffer
    private readonly byte[] _frameBuffer;
    private readonly GCHandle _bufferHandle;
    private IntPtr _bufferPointer;
    private readonly int _pitch;

    // PPU state
    private int _modeClock;
    private byte _currentMode;
    private int _currentLine;

    // DMG (original Game Boy) color palettes (cached)
    private uint[] _bgPalette = new uint[4];
    private uint[] _objPalette0 = new uint[4];
    private uint[] _objPalette1 = new uint[4];

    // GBC color palettes (cached)
    private uint[] _gbcBgPalettes = new uint[8 * 4]; // 8 palettes of 4 colors each
    private uint[] _gbcObjPalettes = new uint[8 * 4]; // 8 palettes of 4 colors each

    // Palette Index tracking (for auto-increment)
    private byte _bgPaletteIndex;
    private byte _objPaletteIndex;

    // Reference to MMU
    private readonly MMU _mmu;

    // Indicates if we're in GBC mode
    private readonly bool _isGbcMode;

    // Debug options
    private bool _renderDebugEnabled = false;
    private bool _drawDebugBorder = false;
    private byte _lastLcdc = 0;
    private bool _overlayMode = false;
    private bool _debugLogging = false;

    private bool _disposed = false;

    private bool IsLcdPoweredOn => (_mmu.ReadByte(LCDC_REG) & LCDC_DISPLAY_ENABLE) != 0;

    public PPU(MMU mmu)
    {
        _mmu = mmu ?? throw new ArgumentNullException(nameof(mmu));
        _isGbcMode = mmu.IsGbcMode;

        _pitch = GB_SCREEN_WIDTH * BYTES_PER_PIXEL;
        _frameBuffer = new byte[GB_SCREEN_HEIGHT * _pitch];
        _bufferHandle = GCHandle.Alloc(_frameBuffer, GCHandleType.Pinned);
        _bufferPointer = _bufferHandle.AddrOfPinnedObject();

        if (_mmu is IMemoryWriteObservable observable)
        {
            observable.RegisterWriteHandler(BGP_REG, HandleBGPWrite);
            observable.RegisterWriteHandler(OBP0_REG, HandleOBP0Write);
            observable.RegisterWriteHandler(OBP1_REG, HandleOBP1Write);
            observable.RegisterWriteHandler(BCPS_REG, HandleBCPSWrite);
            observable.RegisterWriteHandler(BCPD_REG, HandleGBCBgPaletteWrite);
            observable.RegisterWriteHandler(OCPS_REG, HandleOCPSWrite);
            observable.RegisterWriteHandler(OCPD_REG, HandleGBCObjPaletteWrite);
            observable.RegisterWriteHandler(DMA_REG, HandleDMAWrite);
        }

        Reset();
    }

    public Span<byte> FrameBuffer => _frameBuffer;
    public int Width => GB_SCREEN_WIDTH;
    public int Height => GB_SCREEN_HEIGHT;
    public int Pitch => _pitch;
    public IntPtr BufferPointer => _bufferPointer;
    public int BufferSize => _frameBuffer.Length;

    public event EventHandler FrameCompleted;

    public void Reset()
    {
        _modeClock = 0;
        _currentMode = STAT_MODE_OAM;
        _currentLine = 0;
        _bgPaletteIndex = 0;
        _objPaletteIndex = 0;
        _lastLcdc = 0;
        _mmu.UpdateLY(0);
        ClearFramebufferToWhite();
        UpdateDMGPalettes();
        InitializeGBCPalettes();
        Console.WriteLine("[PPU] PPU Reset Complete - Buffer Size: " + _frameBuffer.Length);
    }
    
    private void ClearFramebufferToWhite()
    {
        uint whiteColor = GetDMGColor(0); 
        byte r = (byte)((whiteColor >> 16) & 0xFF);
        byte g = (byte)((whiteColor >> 8) & 0xFF);
        byte b = (byte)(whiteColor & 0xFF);
        byte a = (byte)(whiteColor >> 24);

        for (int i = 0; i < _frameBuffer.Length; i += BYTES_PER_PIXEL)
        {
            _frameBuffer[i + 0] = r;
            _frameBuffer[i + 1] = g;
            _frameBuffer[i + 2] = b;
            _frameBuffer[i + 3] = a;
        }
    }

    private void InitializeGBCPalettes()
    {
        uint white = 0xFFFFFFFF; 
        if (_isGbcMode)
        {
            for (int i = 0; i < _gbcBgPalettes.Length; i++) _gbcBgPalettes[i] = white;
            for (int i = 0; i < _gbcObjPalettes.Length; i++) _gbcObjPalettes[i] = white;
        }
    }

    public void Step(int cycles)
    {
        bool lcdOn = this.IsLcdPoweredOn;
        _modeClock += cycles;

        if (!lcdOn)
        {
            if (_modeClock >= SCANLINE_CYCLES)
            {
                _modeClock -= SCANLINE_CYCLES;
            }
            if (_currentLine != 0)
            {
                _currentLine = 0;
                _mmu.UpdateLY(0);
            }
            byte currentStatMode = (byte)(_mmu.ReadByte(STAT_REG) & STAT_MODE_MASK);
            if (currentStatMode != STAT_MODE_VBLANK)
            {
                _currentMode = STAT_MODE_VBLANK;
                byte stat = _mmu.ReadByte(STAT_REG);
                stat = (byte)((stat & ~STAT_MODE_MASK) | STAT_MODE_VBLANK);
                _mmu.WriteByte(STAT_REG, stat);
            }
            return;
        }

        switch (_currentMode)
        {
            case STAT_MODE_OAM: 
                if (_modeClock >= 80)
                {
                    _modeClock -= 80;
                    SetMode(STAT_MODE_TRANSFER);
                }
                break;

            case STAT_MODE_TRANSFER: 
                if (_modeClock >= 172)
                {
                    _modeClock -= 172;
                    if (_currentLine < GB_SCREEN_HEIGHT)
                    {
                         RenderScanline();
                    }
                    SetMode(STAT_MODE_HBLANK);
                }
                break;

            case STAT_MODE_HBLANK:
                if (_modeClock >= 204)
                {
                    _modeClock -= 204;
                    _currentLine++;
                    _mmu.UpdateLY((byte)_currentLine);
                    CheckLYC(); 

                    if (_currentLine == VBLANK_START_LINE)
                    {
                        SetMode(STAT_MODE_VBLANK);
                        RequestVBlankInterrupt();
                        FrameCompleted?.Invoke(this, EventArgs.Empty);
                    }
                    else 
                    {
                        SetMode(STAT_MODE_OAM);
                    }
                }
                break;

            case STAT_MODE_VBLANK:
                if (_modeClock >= SCANLINE_CYCLES)
                {
                    _modeClock -= SCANLINE_CYCLES;
                    _currentLine++; 
                    if (_currentLine >= TOTAL_LINES)
                    {
                        _currentLine = 0; 
                        _mmu.UpdateLY((byte)_currentLine); 
                        CheckLYC(); 
                        SetMode(STAT_MODE_OAM);
                    }
                    else 
                    {
                         _mmu.UpdateLY((byte)_currentLine);
                         CheckLYC();
                    }
                }
                break;
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            if (_mmu is IMemoryWriteObservable observable)
            {
                observable.UnregisterWriteHandler(BCPD_REG, HandleGBCBgPaletteWrite);
                observable.UnregisterWriteHandler(OCPD_REG, HandleGBCObjPaletteWrite);
                observable.UnregisterWriteHandler(BCPS_REG, HandleBCPSWrite);
                observable.UnregisterWriteHandler(OCPS_REG, HandleOCPSWrite);
                observable.UnregisterWriteHandler(DMA_REG, HandleDMAWrite);
                observable.UnregisterWriteHandler(BGP_REG, HandleBGPWrite);
                observable.UnregisterWriteHandler(OBP0_REG, HandleOBP0Write);
                observable.UnregisterWriteHandler(OBP1_REG, HandleOBP1Write);
            }
        }
        if (_bufferHandle.IsAllocated)
        {
            _bufferHandle.Free();
            _bufferPointer = IntPtr.Zero;
        }
        _disposed = true;
    }

    ~PPU() => Dispose(false);

    private void HandleBCPSWrite(ushort address, byte value) { _bgPaletteIndex = value; }
    private void HandleOCPSWrite(ushort address, byte value) { _objPaletteIndex = value; }
    private void HandleDMAWrite(ushort address, byte value) { PerformDMATransfer(value); }
    private void HandleBGPWrite(ushort address, byte value) { UpdateDMGPalettes(); }
    private void HandleOBP0Write(ushort address, byte value) { UpdateDMGPalettes(); }
    private void HandleOBP1Write(ushort address, byte value) { UpdateDMGPalettes(); }

    private void HandleGBCBgPaletteWrite(ushort address, byte value)
    {
        if (!_isGbcMode) return;
        byte index = (byte)(_bgPaletteIndex & 0x3F);
        UpdateGBCPalette(_gbcBgPalettes, index, value);
        if ((_bgPaletteIndex & 0x80) != 0) { _bgPaletteIndex = (byte)((_bgPaletteIndex & 0x80) | ((index + 1) & 0x3F)); _mmu.WriteByte(BCPS_REG, _bgPaletteIndex); }
    }

    private void HandleGBCObjPaletteWrite(ushort address, byte value)
    {
        if (!_isGbcMode) return;
        byte index = (byte)(_objPaletteIndex & 0x3F);
        UpdateGBCPalette(_gbcObjPalettes, index, value);
        if ((_objPaletteIndex & 0x80) != 0) { _objPaletteIndex = (byte)((_objPaletteIndex & 0x80) | ((index + 1) & 0x3F)); _mmu.WriteByte(OCPS_REG, _objPaletteIndex); }
    }

    private void UpdateGBCPalette(uint[] paletteCache, byte index, byte value)
    {
        int colorEntryIndex = index / 2;    
        bool isHighByte = (index % 2) == 1;

        if (colorEntryIndex >= paletteCache.Length) return; 

        int cacheIndex = colorEntryIndex; // Direct mapping for GBC: 64 bytes -> 32 colors -> 8 palettes * 4 colors/palette

        uint currentColorDataInCache = paletteCache[cacheIndex];
        ushort currentGbcColorValue = 0;

        // If the cache holds 0xFFFFFFFF (initial white), treat its 15-bit representation as 0x7FFF
        if (currentColorDataInCache == 0xFFFFFFFF) {
            currentGbcColorValue = 0x7FFF;
        } else {
            // Convert cached RGBA to 15-bit GBC color
             byte r_cached = (byte)((currentColorDataInCache >> 16) & 0xFF);
             byte g_cached = (byte)((currentColorDataInCache >> 8) & 0xFF);
             byte b_cached = (byte)(currentColorDataInCache & 0xFF);
             currentGbcColorValue = (ushort)(((b_cached / 8) << 10) | ((g_cached / 8) << 5) | (r_cached / 8));
        }

        if (isHighByte) 
        {
            currentGbcColorValue = (ushort)((currentGbcColorValue & 0x00FF) | (value << 8)); 
        }
        else 
        {
            currentGbcColorValue = (ushort)((currentGbcColorValue & 0xFF00) | value); 
        }

        byte r = (byte)((currentGbcColorValue & 0x1F) * 8);
        byte g = (byte)(((currentGbcColorValue >> 5) & 0x1F) * 8);
        byte b = (byte)(((currentGbcColorValue >> 10) & 0x1F) * 8);
        paletteCache[cacheIndex] = (uint)(0xFF << 24 | r << 16 | g << 8 | b);
    }

    private void PerformDMATransfer(byte sourceHigh)
    {
        ushort sourceAddress = (ushort)(sourceHigh << 8);
        for (int i = 0; i < 0xA0; i++)
        {
            byte value = _mmu.ReadByte((ushort)(sourceAddress + i));
            _mmu.WriteDirect((ushort)(0xFE00 + i), value); 
        }
    }

    private void UpdateDMGPalettes()
    {
        byte bgp = _mmu.ReadByte(BGP_REG);
        byte obp0 = _mmu.ReadByte(OBP0_REG);
        byte obp1 = _mmu.ReadByte(OBP1_REG);

        _bgPalette[0] = GetDMGColor((byte)((bgp >> 0) & 0x03));
        _bgPalette[1] = GetDMGColor((byte)((bgp >> 2) & 0x03));
        _bgPalette[2] = GetDMGColor((byte)((bgp >> 4) & 0x03));
        _bgPalette[3] = GetDMGColor((byte)((bgp >> 6) & 0x03));

        _objPalette0[0] = GetDMGColor((byte)((obp0 >> 0) & 0x03)); 
        _objPalette0[1] = GetDMGColor((byte)((obp0 >> 2) & 0x03));
        _objPalette0[2] = GetDMGColor((byte)((obp0 >> 4) & 0x03));
        _objPalette0[3] = GetDMGColor((byte)((obp0 >> 6) & 0x03));

        _objPalette1[0] = GetDMGColor((byte)((obp1 >> 0) & 0x03)); 
        _objPalette1[1] = GetDMGColor((byte)((obp1 >> 2) & 0x03));
        _objPalette1[2] = GetDMGColor((byte)((obp1 >> 4) & 0x03));
        _objPalette1[3] = GetDMGColor((byte)((obp1 >> 6) & 0x03));
    }

    private uint GetDMGColor(byte colorId)
    {
        switch (colorId)
        {
            case 0: return 0xFFE0F8D0; 
            case 1: return 0xFF88C070; 
            case 2: return 0xFF346856; 
            case 3: return 0xFF081820; 
            default: return 0xFF000000;
        }
    }

    private void SetMode(byte mode)
    {
        _currentMode = mode;
        byte stat = _mmu.ReadByte(STAT_REG);
        stat = (byte)((stat & ~STAT_MODE_MASK) | _currentMode);
        _mmu.WriteByte(STAT_REG, stat);

        bool triggerInterrupt = false;
        switch (_currentMode)
        {
            case STAT_MODE_HBLANK when (stat & STAT_HBLANK_INT) != 0: triggerInterrupt = true; break;
            case STAT_MODE_VBLANK when (stat & STAT_VBLANK_INT) != 0: triggerInterrupt = true; break;
            case STAT_MODE_OAM when (stat & STAT_OAM_INT) != 0: triggerInterrupt = true; break;
        }
        if (triggerInterrupt) RequestStatInterrupt();
    }

    private void CheckLYC()
    {
        byte lyc = _mmu.ReadByte(LYC_REG);
        byte stat = _mmu.ReadByte(STAT_REG);
        bool lycMatch = (lyc == _currentLine);

        if (lycMatch)
        {
            stat |= STAT_LYC_EQUAL; 
            if ((stat & STAT_LYC_INT) != 0) RequestStatInterrupt();
        }
        else
        {
            stat &= unchecked((byte)~STAT_LYC_EQUAL); 
        }
        _mmu.WriteByte(STAT_REG, stat);
    }

    private void RequestVBlankInterrupt()
    {
        byte oldIf = _mmu.ReadByte(0xFF0F);
        _mmu.WriteByte(0xFF0F, (byte)(oldIf | 0x01));
    }

    private void RequestStatInterrupt()
    {
        byte ifReg = _mmu.ReadByte(0xFF0F);
        _mmu.WriteByte(0xFF0F, (byte)(ifReg | 0x02));
    }

    private void RenderScanline()
    {
        byte lcdc = _mmu.ReadByte(LCDC_REG);
        // ClearScanline(); // Optional: Clear to a specific color or rely on BG drawing

        if ((lcdc & LCDC_BG_ENABLE) != 0 || _isGbcMode) 
        {
            RenderBackground(lcdc);
            if ((lcdc & LCDC_WINDOW_ENABLE) != 0)
            {
                byte wy = _mmu.ReadByte(WY_REG);
                if (_currentLine >= wy)
                {
                    RenderWindow(lcdc);
                }
            }
        }
        else // If BG is disabled (DMG only feature, GBC BG is always "on" if LCD is on)
        {
            // Fill scanline with white (color 0 of BGP) if BG is off for DMG
            // This prevents previous frame data from showing if BG is toggled.
            // GBC effectively always has BG on if LCD is on.
            if (!_isGbcMode)
            {
                 uint whiteColor = _bgPalette[0]; // DMG white
                 byte r = (byte)((whiteColor >> 16) & 0xFF);
                 byte g = (byte)((whiteColor >> 8) & 0xFF);
                 byte b = (byte)(whiteColor & 0xFF);
                 int lineOffset = _currentLine * _pitch;
                 for (int x = 0; x < GB_SCREEN_WIDTH; x++)
                 {
                     int pixelOffset = lineOffset + (x * BYTES_PER_PIXEL);
                     _frameBuffer[pixelOffset + 0] = r;
                     _frameBuffer[pixelOffset + 1] = g;
                     _frameBuffer[pixelOffset + 2] = b;
                     _frameBuffer[pixelOffset + 3] = 0xFF;
                 }
            }
        }


        if ((lcdc & LCDC_OBJ_ENABLE) != 0)
        {
            RenderSprites(lcdc);
        }
        if (_overlayMode && _renderDebugEnabled) AddGridOverlay();
    }
    
    // Helper to read GBC attributes from VRAM Bank 1
    private byte ReadGbcTileAttributes(ushort tileMapAddressInBank0)
    {
        // Attributes are at the same address as the tile index, but in VRAM Bank 1.
        byte originalVbk = _mmu.ReadByte(VBK_REG);
        _mmu.WriteByte(VBK_REG, (byte)((originalVbk & 0xFE) | 1)); // Select VRAM Bank 1
        byte attributes = _mmu.ReadByte(tileMapAddressInBank0);
        _mmu.WriteByte(VBK_REG, originalVbk); // Restore original VRAM bank
        return attributes;
    }


    private void RenderBackground(byte lcdc)
    {
        byte scy = _mmu.ReadByte(SCY_REG);
        byte scx = _mmu.ReadByte(SCX_REG);

        ushort bgMapBaseAddr = (lcdc & LCDC_BG_MAP) != 0 ? (ushort)0x9C00 : (ushort)0x9800;
        bool unsignedTileIndices = (lcdc & LCDC_TILE_DATA) != 0; // true: 0x8000-0x8FFF, false: 0x8800-0x97FF (signed)
        ushort tileDataBaseAddr = unsignedTileIndices ? (ushort)0x8000 : (ushort)0x8800;

        int scanlineOffset = _currentLine * _pitch;
        byte yPosInMap = (byte)((scy + _currentLine) & 0xFF); 

        for (int xScreen = 0; xScreen < GB_SCREEN_WIDTH; xScreen++)
        {
            byte xPosInMap = (byte)((scx + xScreen) & 0xFF);
            
            byte tileRowInMap = (byte)(yPosInMap / 8);
            byte tileColInMap = (byte)(xPosInMap / 8);
            ushort tileMapEntryAddr = (ushort)(bgMapBaseAddr + tileRowInMap * 32 + tileColInMap);
            
            byte tileIndex = _mmu.ReadByte(tileMapEntryAddr); // Tile index from VRAM Bank 0 (Tile Map)

            byte attributes = 0;
            int gbcPaletteNum = 0;
            bool gbcTileDataInBank1 = false;
            bool gbcHFlip = false;
            bool gbcVFlip = false;

            if (_isGbcMode)
            {
                attributes = ReadGbcTileAttributes(tileMapEntryAddr);
                gbcPaletteNum = attributes & 0x07;
                gbcTileDataInBank1 = (attributes & 0x08) != 0;
                gbcHFlip = (attributes & 0x20) != 0;
                gbcVFlip = (attributes & 0x40) != 0;
            }

            byte yInTile = (byte)(yPosInMap % 8);
            if (_isGbcMode && gbcVFlip) yInTile = (byte)(7 - yInTile);

            byte xInTile = (byte)(xPosInMap % 8);
            // HFlip is applied when extracting color bit

            ushort tileDataAddressActual;
            if (unsignedTileIndices) tileDataAddressActual = (ushort)(tileDataBaseAddr + tileIndex * 16);
            else tileDataAddressActual = (ushort)(tileDataBaseAddr + ((sbyte)tileIndex + 128) * 16);
            
            tileDataAddressActual += (ushort)(yInTile * 2);

            byte tileLow, tileHigh;
            byte originalVbkForData = 0;
            if (_isGbcMode)
            {
                originalVbkForData = _mmu.ReadByte(VBK_REG);
                byte targetBank = gbcTileDataInBank1 ? (byte)1 : (byte)0;
                 if((originalVbkForData & 1) != targetBank) // Only switch if necessary
                    _mmu.WriteByte(VBK_REG, targetBank);
            }

            tileLow = _mmu.ReadByte(tileDataAddressActual);
            tileHigh = _mmu.ReadByte((ushort)(tileDataAddressActual + 1));

            if (_isGbcMode)
            {
                // Restore VBK if it was changed for GBC tile data fetch
                 if((originalVbkForData & 1) != (gbcTileDataInBank1 ? 1 : 0))
                    _mmu.WriteByte(VBK_REG, originalVbkForData);
            }
            
            byte colorBit;
            if (_isGbcMode && gbcHFlip) colorBit = xInTile;
            else colorBit = (byte)(7 - xInTile);
            
            byte colorId = (byte)( ((tileLow >> colorBit) & 1) | (((tileHigh >> colorBit) & 1) << 1) );

            uint finalColor;
            if (_isGbcMode)
            {
                int paletteCacheIndex = (gbcPaletteNum * 4) + colorId;
                finalColor = _gbcBgPalettes[paletteCacheIndex < _gbcBgPalettes.Length ? paletteCacheIndex : 0];
            }
            else
            {
                finalColor = _bgPalette[colorId];
            }
            
            int pixelOffset = scanlineOffset + (xScreen * BYTES_PER_PIXEL);
            if (pixelOffset >= 0 && pixelOffset + 3 < _frameBuffer.Length)
            {
                _frameBuffer[pixelOffset + 0] = (byte)((finalColor >> 16) & 0xFF); 
                _frameBuffer[pixelOffset + 1] = (byte)((finalColor >> 8) & 0xFF);  
                _frameBuffer[pixelOffset + 2] = (byte)(finalColor & 0xFF);        
                _frameBuffer[pixelOffset + 3] = 0xFF;                         
            }
        }
    }

    private void RenderWindow(byte lcdc)
    {
        byte wy = _mmu.ReadByte(WY_REG);
        byte wx = _mmu.ReadByte(WX_REG);

        if (_currentLine < wy) return; // Window not active on this line yet

        int windowStartX = wx - 7;
        if (windowStartX >= GB_SCREEN_WIDTH) return; // Window completely off-screen to the right

        ushort winMapBaseAddr = (lcdc & LCDC_WINDOW_MAP) != 0 ? (ushort)0x9C00 : (ushort)0x9800;
        bool unsignedTileIndices = (lcdc & LCDC_TILE_DATA) != 0;
        ushort tileDataBaseAddr = unsignedTileIndices ? (ushort)0x8000 : (ushort)0x8800;
        
        int scanlineOffset = _currentLine * _pitch;
        int yInWindow = _currentLine - wy; // Y position within the window's content area

        for (int xScreen = windowStartX; xScreen < GB_SCREEN_WIDTH; xScreen++)
        {
            if (xScreen < 0) continue; // Pixel is off-screen to the left

            int xInWindow = xScreen - windowStartX; // X position relative to window's display start

            byte tileRowInMap = (byte)(yInWindow / 8);
            byte tileColInMap = (byte)(xInWindow / 8);
            ushort tileMapEntryAddr = (ushort)(winMapBaseAddr + tileRowInMap * 32 + tileColInMap);

            byte tileIndex = _mmu.ReadByte(tileMapEntryAddr); // Tile index from VRAM Bank 0 (Tile Map)

            byte attributes = 0;
            int gbcPaletteNum = 0;
            bool gbcTileDataInBank1 = false;
            bool gbcHFlip = false;
            bool gbcVFlip = false;

            if (_isGbcMode)
            {
                attributes = ReadGbcTileAttributes(tileMapEntryAddr);
                gbcPaletteNum = attributes & 0x07;
                gbcTileDataInBank1 = (attributes & 0x08) != 0;
                gbcHFlip = (attributes & 0x20) != 0;
                gbcVFlip = (attributes & 0x40) != 0;
            }

            byte yInTile = (byte)(yInWindow % 8);
            if (_isGbcMode && gbcVFlip) yInTile = (byte)(7 - yInTile);

            byte xInTile = (byte)(xInWindow % 8);
            // HFlip applied when extracting color bit

            ushort tileDataAddressActual;
            if (unsignedTileIndices) tileDataAddressActual = (ushort)(tileDataBaseAddr + tileIndex * 16);
            else tileDataAddressActual = (ushort)(tileDataBaseAddr + ((sbyte)tileIndex + 128) * 16);
            
            tileDataAddressActual += (ushort)(yInTile * 2);

            byte tileLow, tileHigh;
            byte originalVbkForData = 0;
             if (_isGbcMode)
            {
                originalVbkForData = _mmu.ReadByte(VBK_REG);
                byte targetBank = gbcTileDataInBank1 ? (byte)1 : (byte)0;
                 if((originalVbkForData & 1) != targetBank)
                    _mmu.WriteByte(VBK_REG, targetBank);
            }

            tileLow = _mmu.ReadByte(tileDataAddressActual);
            tileHigh = _mmu.ReadByte((ushort)(tileDataAddressActual + 1));

            if (_isGbcMode)
            {
                 if((originalVbkForData & 1) != (gbcTileDataInBank1 ? 1 : 0))
                    _mmu.WriteByte(VBK_REG, originalVbkForData);
            }
            
            byte colorBit;
            if (_isGbcMode && gbcHFlip) colorBit = xInTile;
            else colorBit = (byte)(7 - xInTile);

            byte colorId = (byte)( ((tileLow >> colorBit) & 1) | (((tileHigh >> colorBit) & 1) << 1) );

            uint finalColor;
            if (_isGbcMode)
            {
                int paletteCacheIndex = (gbcPaletteNum * 4) + colorId;
                finalColor = _gbcBgPalettes[paletteCacheIndex < _gbcBgPalettes.Length ? paletteCacheIndex : 0];
            }
            else
            {
                finalColor = _bgPalette[colorId]; // Window uses BG palette in DMG
            }
            
            int pixelOffset = scanlineOffset + (xScreen * BYTES_PER_PIXEL);
            if (pixelOffset >= 0 && pixelOffset + 3 < _frameBuffer.Length)
            {
                _frameBuffer[pixelOffset + 0] = (byte)((finalColor >> 16) & 0xFF);
                _frameBuffer[pixelOffset + 1] = (byte)((finalColor >> 8) & 0xFF);
                _frameBuffer[pixelOffset + 2] = (byte)(finalColor & 0xFF);
                _frameBuffer[pixelOffset + 3] = 0xFF;
            }
        }
    }


    private void RenderSprites(byte lcdc)
    {
        bool tallSprites = (lcdc & LCDC_OBJ_SIZE) != 0;
        int spriteHeight = tallSprites ? 16 : 8;
        
        Span<(int oamIndex, byte xPos, byte oamAddress)> visibleSprites = stackalloc (int, byte, byte)[10];
        int visibleCount = 0;

        for (int i = 0; i < 40 && visibleCount < 10; i++) 
        {
            ushort oamEntryAddr = (ushort)(0xFE00 + i * 4);
            byte spriteY = (byte)(_mmu.ReadByte(oamEntryAddr) - 16); 
            byte spriteX = _mmu.ReadByte((ushort)(oamEntryAddr + 1)); 

            if (_currentLine >= spriteY && _currentLine < (spriteY + spriteHeight))
            {
                 visibleSprites[visibleCount++] = (i, spriteX, (byte)oamEntryAddr);
            }
        }

        if (visibleCount == 0) return;

        // Sort by X-coordinate for DMG, OAM index for CGB tie-breaking (already implicitly by scan order for CGB if X is equal)
        // For simplicity, just X-sort. Proper CGB priority is more complex if X coords are equal (lower OAM index wins).
        // The current loop below (rendering right-to-left after sorting by X) handles X-priority correctly.
        visibleSprites[..visibleCount].Sort((a, b) => a.xPos.CompareTo(b.xPos));

        int scanlineOffset = _currentLine * _pitch;

        for (int i = visibleCount - 1; i >= 0; i--) // Render from right to left (lower X has higher priority)
        {
            ushort oamAddr = (ushort)(0xFE00 + visibleSprites[i].oamIndex * 4); // Use oamIndex to fetch full data again

            byte spriteYScreen = (byte)(_mmu.ReadByte(oamAddr) - 16);
            byte spriteXScreen = (byte)(_mmu.ReadByte((ushort)(oamAddr + 1)) - 8);
            byte tileIndexOam = _mmu.ReadByte((ushort)(oamAddr + 2));
            byte attributes = _mmu.ReadByte((ushort)(oamAddr + 3));

            bool yFlip = (attributes & 0x40) != 0;
            bool xFlip = (attributes & 0x20) != 0;
            bool bgHasPriority = (attributes & 0x80) != 0; 

            int gbcPaletteNum = 0;
            bool gbcSpriteTileInBank1 = false;
            uint[] selectedObjPaletteDMG = _objPalette0;

            if (_isGbcMode)
            {
                gbcPaletteNum = attributes & 0x07;
                gbcSpriteTileInBank1 = (attributes & 0x08) != 0; // Bit 3 for VRAM bank
            }
            else // DMG
            {
                selectedObjPaletteDMG = (attributes & 0x10) != 0 ? _objPalette1 : _objPalette0;
            }
            
            int lineInSprite = _currentLine - spriteYScreen;
            if (yFlip) lineInSprite = spriteHeight - 1 - lineInSprite;

            byte finalTileIndex = tileIndexOam;
            if (tallSprites)
            {
                finalTileIndex &= 0xFE; // Mask LSB for 8x16
                if (lineInSprite >= 8) finalTileIndex++; // Use second tile
            }
            
            ushort tilePixelDataRowAddress = (ushort)(0x8000 + finalTileIndex * 16 + (lineInSprite % 8) * 2);

            byte tileLow, tileHigh;
            byte originalVbkForSpriteData = 0;

            if (_isGbcMode)
            {
                originalVbkForSpriteData = _mmu.ReadByte(VBK_REG);
                byte targetBank = gbcSpriteTileInBank1 ? (byte)1 : (byte)0;
                if((originalVbkForSpriteData & 1) != targetBank)
                    _mmu.WriteByte(VBK_REG, targetBank); // Sprites always use 0x8000-0x9FFF for tile data.
            }
            // For DMG, VBK_REG doesn't apply, effectively Bank 0 for 0x8000 range.

            tileLow = _mmu.ReadByte(tilePixelDataRowAddress);
            tileHigh = _mmu.ReadByte((ushort)(tilePixelDataRowAddress + 1));

            if (_isGbcMode)
            {
                 if((originalVbkForSpriteData & 1) != (gbcSpriteTileInBank1 ? 1 : 0))
                    _mmu.WriteByte(VBK_REG, originalVbkForSpriteData); // Restore
            }

            for (int xInSpriteTile = 0; xInSpriteTile < 8; xInSpriteTile++)
            {
                int screenX = spriteXScreen + xInSpriteTile;
                if (screenX < 0 || screenX >= GB_SCREEN_WIDTH) continue;

                byte colorBit = xFlip ? (byte)xInSpriteTile : (byte)(7 - xInSpriteTile);
                byte colorId = (byte)( ((tileLow >> colorBit) & 1) | (((tileHigh >> colorBit) & 1) << 1) );

                if (colorId == 0) continue; // Color 0 is transparent

                if (bgHasPriority) // Sprite-behind-BG/Win (BG colors 1-3 hide sprite)
                {
                    // This check needs to consider the final BG/Win pixel color ID already on framebuffer for this spot
                    // Or, re-calculate the BG/Win color ID for this specific pixel (more accurate)
                    byte bgPixelColorId = GetBackgroundColorIdForSpritePriority(lcdc, screenX);
                    if (bgPixelColorId != 0) // If BG pixel is not color 0, it has priority
                    {
                        // GBC specific: LCDC Bit 0 also plays a role if BG Master Priority is enabled.
                        // If LCDC.0=1 (BG priority enabled) AND attribute.7=1 (this sprite has BG prio)
                        // then this check is active.
                        // If LCDC.0=0 (Sprites always win unless this sprite's attr.7=1 AND underlying BG pixel is opaque)
                        // This logic can get complex with GBC LCDC.0. The current 'bgHasPriority' flag from sprite OAM is usually sufficient.
                        bool bgReallyWins = true;
                        if (_isGbcMode) {
                            // In GBC mode, if LCDC bit 0 is 0 (BG/Window over OBJ disabled, i.e. OBJ has priority),
                            // then sprite's own bgHasPriority (OAM bit 7) is the only thing that can make it go behind BG.
                            // If LCDC bit 0 is 1 (BG/Window over OBJ enabled by tile attribute), then sprite's bgHasPriority
                            // is evaluated against the BG tile's own priority bit (bit 7 of BG attribute map).
                            // For simplicity, we assume if OAM.7 is set, sprite yields to non-transparent BG pixel.
                            // The `GetBackgroundColorIdForSpritePriority` should ideally also return GBC BG attribute priority if needed.
                        }


                        if (bgReallyWins) continue;
                    }
                }
                
                uint finalColor;
                if (_isGbcMode)
                {
                    int paletteCacheIndex = (gbcPaletteNum * 4) + colorId;
                    finalColor = _gbcObjPalettes[paletteCacheIndex < _gbcObjPalettes.Length ? paletteCacheIndex : 0];
                }
                else
                {
                    finalColor = selectedObjPaletteDMG[colorId];
                }

                int pixelOffset = scanlineOffset + (screenX * BYTES_PER_PIXEL);
                 if (pixelOffset >= 0 && pixelOffset + 3 < _frameBuffer.Length)
                {
                    _frameBuffer[pixelOffset + 0] = (byte)((finalColor >> 16) & 0xFF);
                    _frameBuffer[pixelOffset + 1] = (byte)((finalColor >> 8) & 0xFF);
                    _frameBuffer[pixelOffset + 2] = (byte)(finalColor & 0xFF);
                    _frameBuffer[pixelOffset + 3] = 0xFF;
                }
            }
        }
    }

    // Simplified version for sprite priority check.
    // This needs to correctly determine the BG/Window color ID at screenX on _currentLine
    private byte GetBackgroundColorIdForSpritePriority(byte lcdc, int screenX)
    {
        // This is a simplified check. A full check would re-simulate BG/Win rendering for (screenX, _currentLine)
        // to get the precise color ID and its GBC attributes if applicable.
        // For now, we'll assume that if the framebuffer pixel is not the global "white" (DMG color 0),
        // it's an opaque BG/Win pixel. This is a rough approximation.
        
        int pixelOffset = (_currentLine * _pitch) + (screenX * BYTES_PER_PIXEL);
        if (pixelOffset < 0 || pixelOffset + 3 >= _frameBuffer.Length) return 0; // Off-screen, treat as transparent

        // Read the current color from the framebuffer
        // This represents what the BG/Window pass has already drawn
        uint existingPixelColor = (uint)(_frameBuffer[pixelOffset+3] << 24 | 
                                        _frameBuffer[pixelOffset+0] << 16 | 
                                        _frameBuffer[pixelOffset+1] << 8  | 
                                        _frameBuffer[pixelOffset+2]);

        // Compare with DMG color 0 (system white/transparent for BG priority purposes)
        if (!_isGbcMode) {
            return existingPixelColor == GetDMGColor(0) ? (byte)0 : (byte)1; // 0 if transparent, 1 if opaque
        } else {
            // For GBC, we need to know the actual color index that was drawn by BG/Win.
            // This is hard without re-rendering or storing color indices per pixel.
            // A placeholder: if it's the initial GBC palette white, assume color 0.
            // This requires a more robust solution like a separate buffer for color indices or attributes.
            // For now, let's assume if it's not pure white from initial palette, it's >0.
            bool isInitialWhite = true;
            for(int i=0; i < _gbcBgPalettes.Length; i+=4) { // Check against color 0 of all BG palettes
                if (existingPixelColor == _gbcBgPalettes[i]) {
                     isInitialWhite = true; // It matches a color 0 of some palette.
                     // This check is still flawed because any GBC color can be white.
                     // A true check needs the original colorId.
                     break;
                } else {
                    isInitialWhite = false;
                }
            }
            // If LCDC.0 is set (BG priority), then we also need to check BG attribute map for priority.
            // This is getting very complex without more infrastructure.
            // Simplification: if the pixel isn't one of the "transparent" base colors, it's opaque.
            // The most reliable way to check if BG pixel is color 0 is to re-fetch that BG pixel's colorId.
            // This is computationally expensive. A common optimization is to store the colorId in an auxiliary buffer.

            // Fallback for GBC: if not fully transparent black (0xFF000000), treat as opaque.
            // Or, ideally, re-fetch the BG color ID if performance allows.
            // Let's call the full BG/Win rendering logic for that one pixel (expensive way)
            // Or use a simplified check as above.
            // For now, return 1 (opaque) if not the PPU's initial clear color.
            uint clearCol = GetDMGColor(0); // Using DMG color 0 as a baseline for "transparent background"
             if ( ((clearCol >> 16) & 0xFF) == _frameBuffer[pixelOffset + 0] &&
                  ((clearCol >> 8) & 0xFF) == _frameBuffer[pixelOffset + 1] &&
                  (clearCol & 0xFF) == _frameBuffer[pixelOffset + 2])
             {
                 return 0; // BG is transparent color 0
             }
             return 1; // BG is opaque (color 1,2,3)
        }
    }
    

    private uint GetGBCSpriteColor(int paletteNum, int colorId)
    {
        // This function is still used by the original sprite renderer.
        // It should be fine as is, assuming _gbcObjPalettes is correctly populated.
        if (!_isGbcMode) // Should not be called in DMG mode if RenderSprites handles DMG palettes directly
        {
            // Fallback or error, DMG sprite colors are usually handled directly in RenderSprites
            return (paletteNum == 0 ? _objPalette0[colorId] : _objPalette1[colorId]);
        }

        int paletteIndex = (paletteNum * 4) + colorId;
        if (paletteIndex < 0 || paletteIndex >= _gbcObjPalettes.Length)
        {
            return 0xFFFF00FF; // Magenta for error
        }
        return _gbcObjPalettes[paletteIndex]; 
    }
    
    private void DrawTestPattern() {/* ... */}
    private void DrawDebugMarkers() {/* ... */}
    private void DrawFullDebugPattern() {/* ... */}
    private void DrawCornerMarker(int startX, int startY, byte r, byte g, byte b) {/* ... */}
    private void AddGridOverlay() {/* ... */}
}

