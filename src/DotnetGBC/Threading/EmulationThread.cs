using System.Diagnostics;
using DotnetGBC.Audio;
using DotnetGBC.Memory;
using DotnetGBC.CPU;
using DotnetGBC.FileSystem;
using DotnetGBC.Graphics;
using DotnetGBC.SDL2.Input;
using DotnetGBC.SDL2.Rendering;

namespace DotnetGBC.Threading;

public class EmulationThread : IDisposable
{
    public const double FRAME_RATE = 59.7275;
    // public const double FRAME_RATE = 1;
    public const int CYCLES_PER_FRAME = 70224;

    private const double MS_PER_FRAME = 1000 / FRAME_RATE;
    
    // Game Boy PPU/LCD register addresses
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

    // LCD Control bit masks
    private const byte LCDC_BG_ENABLE = 0x01;       // Bit 0: BG Display Enable
    private const byte LCDC_OBJ_ENABLE = 0x02;      // Bit 1: OBJ Display Enable
    private const byte LCDC_OBJ_SIZE = 0x04;        // Bit 2: OBJ Size (0=8x8, 1=8x16)
    private const byte LCDC_BG_MAP = 0x08;          // Bit 3: BG Tile Map (0=9800-9BFF, 1=9C00-9FFF)
    private const byte LCDC_TILE_DATA = 0x10;       // Bit 4: BG & Window Tile Data (0=8800-97FF, 1=8000-8FFF)
    private const byte LCDC_WINDOW_ENABLE = 0x20;   // Bit 5: Window Display Enable
    private const byte LCDC_WINDOW_MAP = 0x40;      // Bit 6: Window Tile Map (0=9800-9BFF, 1=9C00-9FFF)
    private const byte LCDC_DISPLAY_ENABLE = 0x80;  // Bit 7: LCD Display Enable
    
    private SM83Cpu _cpu;
    private MMU _mmu { get; set; } // Made public to allow subscription to event publisher
    private PPU _ppu;
    private APU _apu;
    private GBCSDLRenderer _renderer;
    
    private RomLoader _romLoader;
    
    private Thread _thread;

    private volatile bool _running;
    
    private readonly Lock _frameLock = new Lock();
    private volatile bool _frameReady;
    private readonly Stopwatch _frameTimer = new Stopwatch();

    public static int FrameCount = 0;
    private bool _useBootstrapValues = true; // Initialize hardware with bootstrap values
    private bool _debugMode = false;
    
    private static CancellationTokenSource cts = new CancellationTokenSource();
    private static CancellationToken ct = cts.Token;
    
    public EmulationThread(string romPath, IntPtr rendererPtr)
    {
        _mmu = new MMU();
        _romLoader = new RomLoader();
        RomMetadata metadata = _romLoader.LoadRomIntoMMU(romPath, _mmu);
        _cpu = new SM83Cpu(_mmu);
        _ppu = new PPU(_mmu);
        _apu = new APU(_mmu);
        _renderer = new GBCSDLRenderer(_ppu, rendererPtr);
        
        _frameTimer = new Stopwatch();

        Console.WriteLine("[EmulationThread] Loaded: " + metadata.Title);
        Console.WriteLine("[EmulationThread] GBC compatability: " + metadata.IsGbcCompatible);
        
        // Apply bootstrap/BIOS initial values that would normally be set
        // This simulates what happens at the end of the boot ROM
        InitializeHardwareRegisters();

        _ppu.FrameCompleted += OnFrameCompleted;
        
        // Subscribe the MMU to receive game input events
        SDLEventPublisher.Subscribe(_mmu, SDLInputSubscriptionCategory.GameInputs);
        
        Program.UpdateTitle(metadata.Title);
    }

    /// <summary>
    /// Initialize hardware registers to values they would have after the boot ROM completes
    /// </summary>
    private void InitializeHardwareRegisters()
    {
        if (!_useBootstrapValues) return;
        
        // Standard Game Boy boot ROM register values
        _mmu.WriteByte(LCDC_REG, 0x91); // LCD on, BG enabled, 8x8 sprites, BG at 0x9800, Tiles at 0x8000
        _mmu.WriteByte(SCY_REG, 0x00);  // Scroll Y = 0
        _mmu.WriteByte(SCX_REG, 0x00);  // Scroll X = 0
        _mmu.WriteByte(LYC_REG, 0x00);  // LY Compare = 0
        _mmu.WriteByte(BGP_REG, 0xFC);  // BG Palette: 11 10 00 00 (darkest to lightest)
        _mmu.WriteByte(OBP0_REG, 0xFF); // Obj Palette 0: 11 11 11 11
        _mmu.WriteByte(OBP1_REG, 0xFF); // Obj Palette 1: 11 11 11 11
        _mmu.WriteByte(WY_REG, 0x00);   // Window Y = 0
        _mmu.WriteByte(WX_REG, 0x00);   // Window X = 0
        
        // Set CPU registers to match end of boot sequence
        _cpu.Registers.A = 0x01;
        _cpu.Registers.F = 0xB0;
        _cpu.Registers.B = 0x00;
        _cpu.Registers.C = 0x13;
        _cpu.Registers.D = 0x00;
        _cpu.Registers.E = 0xD8;
        _cpu.Registers.H = 0x01;
        _cpu.Registers.L = 0x4D;
        _cpu.Registers.SP = 0xFFFE;
        _cpu.Registers.PC = 0x0100;
        
        Console.WriteLine("[EmulationThread] Initialized hardware registers to post-boot values");
    }

    public void Start()
    {
        if (_thread != null && _thread.IsAlive)
            return;
        
        _running = true;
        _thread = new Thread(EmulationThreadMain);
        _thread.Name = "GB Emulation";
        _thread.IsBackground = true;
        _thread.Start();
    }

    public void Stop() => cts.Cancel();

    // Call from main thread
    public void UpdateFrame()
    {
        if (_frameReady)
        {
            lock (_frameLock)
            {
                _renderer.Render();
                _frameReady = false;
            }
        }
    }

    private void EmulationThreadMain()
    {
        while (!ct.IsCancellationRequested)
        {
            _frameTimer.Restart();
            RunFrame();

            double elapsed = _frameTimer.Elapsed.TotalMilliseconds;

            if (elapsed < MS_PER_FRAME)
            {
                int sleepTime = (int)(MS_PER_FRAME - elapsed);
                if (sleepTime > 0)
                    Thread.Sleep(sleepTime);
            }
            else if (elapsed > MS_PER_FRAME * 1.2)
            {
                Console.WriteLine($"[EmulationThread] Frames running slow! {elapsed}ms vs {MS_PER_FRAME}ms");
            }
        }
    }

    private void RunFrame()
    {
        int cyclesThisFrame = 0;

        // Run the CPU/PPU/APU for exactly one frame's worth of cycles
        while (cyclesThisFrame < CYCLES_PER_FRAME)
        {
            int cpuCycles = _cpu.Step();
            _ppu.Step(cpuCycles);
            _apu.Step(cpuCycles);
            _mmu.TIMAStep(cpuCycles);

            cyclesThisFrame += cpuCycles;
            _mmu.DivCounter += cpuCycles;

            if (_mmu.DivCounter >= 256)
            {
                _mmu.DivCounter -= 256;
                _mmu.IncrementDivRegister();
            }
        }
        
        // Add debug output periodically
        if (_debugMode && FrameCount % 60 == 0) // Every second at 60fps
        {
            DebugPPUState();
        }
        
        FrameCount++;
    }

    private void OnFrameCompleted(object sender, EventArgs e)
    {
        lock (_frameLock)
        {
            _frameReady = true;
        }
    }

    public void Dispose()
    {
        Stop();
        
        // Unsubscribe MMU from input events
        SDLEventPublisher.Unsubscribe(_mmu);
        
        _renderer.Dispose();
        _ppu.Dispose();
    }
    
    // Debug output for PPU state
    private void DebugPPUState()
    {
        // Check LCDC register (0xFF40)
        byte lcdc = _mmu.ReadByte(LCDC_REG);
        bool lcdEnabled = (lcdc & LCDC_DISPLAY_ENABLE) != 0;
        bool bgEnabled = (lcdc & LCDC_BG_ENABLE) != 0;
        bool spritesEnabled = (lcdc & LCDC_OBJ_ENABLE) != 0;
        bool windowEnabled = (lcdc & LCDC_WINDOW_ENABLE) != 0;
        bool tileDataSelect = (lcdc & LCDC_TILE_DATA) != 0;
        bool bgMapSelect = (lcdc & LCDC_BG_MAP) != 0;
        
        Console.WriteLine($"Frame {FrameCount} - PPU State:");
        Console.WriteLine($"  LCD: {(lcdEnabled ? "ON" : "OFF")}, BG: {(bgEnabled ? "ON" : "OFF")}, " +
                         $"Sprites: {(spritesEnabled ? "ON" : "OFF")}, Window: {(windowEnabled ? "ON" : "OFF")}");
        Console.WriteLine($"  LCDC: 0x{lcdc:X2}, Tile Data: {(tileDataSelect ? "8000-8FFF" : "8800-97FF")}, " +
                         $"BG Map: {(bgMapSelect ? "9C00-9FFF" : "9800-9BFF")}");
    
        // Check other key registers
        byte stat = _mmu.ReadByte(STAT_REG);
        byte mode = (byte)(stat & 0x03);
        byte ly = _mmu.ReadByte(LY_REG);
        byte lyc = _mmu.ReadByte(LYC_REG);
        byte scx = _mmu.ReadByte(SCX_REG);
        byte scy = _mmu.ReadByte(SCY_REG);
        byte wx = _mmu.ReadByte(WX_REG);
        byte wy = _mmu.ReadByte(WY_REG);
        byte bgp = _mmu.ReadByte(BGP_REG);
        
        
    
        Console.WriteLine($"  STAT: 0x{stat:X2} (Mode {mode}), LY: {ly}, LYC: {lyc}");
        Console.WriteLine($"  Scroll: X={scx}, Y={scy}, Window: X={wx}, Y={wy}");
        Console.WriteLine($"  BGP: 0x{bgp:X2}");
        
        // Sample VRAM to see if tiles are initialized
        Console.WriteLine("  VRAM sample:");
        ushort tileAddr = 0x8010; // First tile after Nintendo logo
        for (int i = 0; i < 8; i += 2)
        {
            byte b1 = _mmu.ReadByte((ushort)(tileAddr + i));
            byte b2 = _mmu.ReadByte((ushort)(tileAddr + i + 1));
            Console.WriteLine($"    0x{tileAddr + i:X4}: {b1:X2} {b2:X2}");
        }
    }
}

