using System;

namespace DotnetGBC.CPU;

/// <summary>
/// Represents the registers of the Sharp SM83 CPU.
/// The Game Boy CPU has 8 8-bit registers (A, B, C, D, E, F, H, L),
/// which can be combined to form 4 16-bit registers (AF, BC, DE, HL).
/// It also has a 16-bit program counter (PC) and stack pointer (SP).
/// </summary>
public class Registers
{
    // 8-bit registers
    private byte _a; // Accumulator
    private byte _b;
    private byte _c;
    private byte _d;
    private byte _e;
    private byte _f; // Flags: Z N H C 0 0 0 0 (Zero, Negative, Half-Carry, Carry)
    private byte _h;
    private byte _l;

    // 16-bit registers
    private ushort _pc; // Program Counter
    private ushort _sp; // Stack Pointer

    // Flag bit positions
    private const int ZERO_FLAG_BIT = 7;
    private const int SUBTRACT_FLAG_BIT = 6;
    private const int HALF_CARRY_FLAG_BIT = 5;
    private const int CARRY_FLAG_BIT = 4;

    // Flag masks
    private const byte ZERO_FLAG_MASK = 1 << ZERO_FLAG_BIT;
    private const byte SUBTRACT_FLAG_MASK = 1 << SUBTRACT_FLAG_BIT;
    private const byte HALF_CARRY_FLAG_MASK = 1 << HALF_CARRY_FLAG_BIT;
    private const byte CARRY_FLAG_MASK = 1 << CARRY_FLAG_BIT;
    private const byte ALL_FLAGS_MASK = ZERO_FLAG_MASK | SUBTRACT_FLAG_MASK | HALF_CARRY_FLAG_MASK | CARRY_FLAG_MASK;

    /// <summary>
    /// Initializes a new instance of the Registers class with default values.
    /// </summary>
    public Registers()
    {
        Reset();
    }

    /// <summary>
    /// Resets all registers to their default state after power-on.
    /// This simulates the state after the boot ROM has executed.
    /// </summary>
    public void Reset()
    {
        // These values represent the state of the CPU after the boot ROM
        // has executed and control is transferred to the cartridge.
            
        // For DMG (original Game Boy)
        _a = 0x01;
        _f = 0xB0; // Z:1 N:0 H:1 C:1 0:0 0:0 0:0 0:0
        _b = 0x00;
        _c = 0x13;
        _d = 0x00;
        _e = 0xD8;
        _h = 0x01;
        _l = 0x4D;
        _sp = 0xFFFE;
        _pc = 0x0100; // Starting address of most GB/GBC games

    }

    /// <summary>
    /// Resets registers to Game Boy Color post-boot ROM values.
    /// Only used when in GBC mode.
    /// </summary>
    public void ResetGbc()
    {
        // GBC boot ROM final values
        _a = 0x11;
        _f = 0x80; // Z:1 N:0 H:0 C:0 0:0 0:0 0:0 0:0
        _b = 0x00;
        _c = 0x00;
        _d = 0xFF;
        _e = 0x56;
        _h = 0x00;
        _l = 0x0D;
        _sp = 0xFFFE;
        _pc = 0x0100;
    }

    #region 8-bit Register Properties

    public byte A
    {
        get => _a;
        set => _a = value;
    }

    public byte B
    {
        get => _b;
        set => _b = value;
    }

    public byte C
    {
        get => _c;
        set => _c = value;
    }

    public byte D
    {
        get => _d;
        set => _d = value;
    }

    public byte E
    {
        get => _e;
        set => _e = value;
    }

    public byte F
    {
        get => _f;
        set => _f = (byte)(value & ALL_FLAGS_MASK); // Only bits 4-7 are used
    }

    public byte H
    {
        get => _h;
        set => _h = value;
    }

    public byte L
    {
        get => _l;
        set => _l = value;
    }

    #endregion

    #region 16-bit Register Properties

    public ushort AF
    {
        get => (ushort)((_a << 8) | _f);
        set
        {
            _a = (byte)(value >> 8);
            _f = (byte)(value & ALL_FLAGS_MASK); // Only bits 4-7 are used
        }
    }

    public ushort BC
    {
        get => (ushort)((_b << 8) | _c);
        set
        {
            _b = (byte)(value >> 8);
            _c = (byte)value;
        }
    }

    public ushort DE
    {
        get => (ushort)((_d << 8) | _e);
        set
        {
            _d = (byte)(value >> 8);
            _e = (byte)value;
        }
    }

    public ushort HL
    {
        get => (ushort)((_h << 8) | _l);
        set
        {
            _h = (byte)(value >> 8);
            _l = (byte)value;
        }
    }

    public ushort PC
    {
        get => _pc;
        set => _pc = value;
    }

    public ushort SP
    {
        get => _sp;
        set => _sp = value;
    }

    #endregion

    #region Flag Properties

    public bool ZeroFlag
    {
        get => (_f & ZERO_FLAG_MASK) != 0;
        set => _f = value 
            ? (byte)(_f | ZERO_FLAG_MASK) 
            : (byte)(_f & ~ZERO_FLAG_MASK);
    }

    public bool SubtractFlag
    {
        get => (_f & SUBTRACT_FLAG_MASK) != 0;
        set => _f = value 
            ? (byte)(_f | SUBTRACT_FLAG_MASK) 
            : (byte)(_f & ~SUBTRACT_FLAG_MASK);
    }

    public bool HalfCarryFlag
    {
        get => (_f & HALF_CARRY_FLAG_MASK) != 0;
        set => _f = value 
            ? (byte)(_f | HALF_CARRY_FLAG_MASK) 
            : (byte)(_f & ~HALF_CARRY_FLAG_MASK);
    }

    public bool CarryFlag
    {
        get => (_f & CARRY_FLAG_MASK) != 0;
        set => _f = value 
            ? (byte)(_f | CARRY_FLAG_MASK) 
            : (byte)(_f & ~CARRY_FLAG_MASK);
    }

    #endregion

    /// <summary>
    /// Returns a string representation of the register values.
    /// Useful for debugging.
    /// </summary>
    public override string ToString()
    {
        return $"A: {_a:X2} F: {_f:X2} B: {_b:X2} C: {_c:X2} D: {_d:X2} E: {_e:X2} H: {_h:X2} L: {_l:X2} " +
               $"PC: {_pc:X4} SP: {_sp:X4} " +
               $"Flags: {(ZeroFlag ? 'Z' : '-')}{(SubtractFlag ? 'N' : '-')}{(HalfCarryFlag ? 'H' : '-')}{(CarryFlag ? 'C' : '-')}";
    }
}

