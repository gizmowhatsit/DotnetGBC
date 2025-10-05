using DotnetGBC.Memory;

namespace DotnetGBC.CPU;

/// <summary>
/// Implementation of the Sharp SM83 CPU used in Game Boy and Game Boy Color.
/// This CPU is a modified version of the Zilog Z80 processor.
/// </summary>
public class SM83Cpu
{
    // Memory access
    private readonly MMU _mmu;

    // Registers
    public Registers Registers;

    // CPU state
    private bool _isHalted;
    private bool _isStopped;
    private bool _interruptsEnabled;
    private InterruptDelayState _interruptDelayState = InterruptDelayState.None;
    
    private bool _justReturnedFromInterrupt = false;
    private int _instructionAfterRetiCount = 0;

    // For GBC only
    private bool _doubleSpeedMode;
    
    // Debug
    private bool _verboseLogging = false;

    /// <summary>
    /// Creates a new instance of the SM83 CPU.
    /// </summary>
    /// <param name="mmu">Memory Management Unit for memory access.</param>
    public SM83Cpu(MMU mmu)
    {
        _mmu = mmu ?? throw new ArgumentNullException(nameof(mmu));
        Registers = new Registers();
        Reset();
    }

    /// <summary>
    /// Resets the CPU to its initial state.
    /// </summary>
    public void Reset()
    {
        // Reset registers based on post-boot ROM values
        if (_mmu.IsGbcMode)
        {
            Registers.ResetGbc();
        }
        else
        {
            Registers.Reset();
        }

        _isHalted = false;
        _isStopped = false;
        _interruptsEnabled = true;
        _doubleSpeedMode = false;
    }

    /// <summary>
    /// Executes a single CPU instruction.
    /// </summary>
    /// <returns>The number of cycles consumed by the instruction.</returns>
    public int Step()
    {
        // Handle HALT/STOP logic first... (ensure HALT check uses corrected ieRegHalt variable)
        if (_isHalted)
        {
            byte ieRegHalt = _mmu.ReadByte(0xFFFF);
            byte ifRegisterHalt = _mmu.ReadByte(0xFF0F);
            if ((ieRegHalt & ifRegisterHalt & 0x1F) != 0) {
                _isHalted = false;
            } else {
                return 4; // Still halted
            }
        }
        if (_isStopped) { /* ... Stop logic ... */ return 4; }


        // Fetch and Execute Instruction
        int instructionCycles = 0;
        ushort currentPC = Registers.PC; // PC before fetch
        byte opcode = _mmu.ReadByte(Registers.PC++);
        instructionCycles = ExecuteOpcode(opcode); // Might schedule IME enable via RETI/EI
        
        if (_justReturnedFromInterrupt)
        {
            _instructionAfterRetiCount++;
            // Log opcode and instructionCycles here
            if (_verboseLogging)
            {
                Console.WriteLine($"[CPU POST-RETI INSTR {_instructionAfterRetiCount}] PC=0x{currentPC:X4}, Opcode=0x{opcode:X2}, Returned Cycles={instructionCycles}");
            }
            if (_instructionAfterRetiCount >= 2) {
                _justReturnedFromInterrupt = false; // Stop logging after 2
            }
        }

        // --- Process Interrupt Delay State Machine *AFTER* Instruction Execution ---
        bool allowInterruptCheckThisCycle = true;

        if (_interruptDelayState == InterruptDelayState.PendingEnable)
        {
            // Apply scheduled enable, transition state, disallow check *this* cycle
            _interruptsEnabled = true;
            _interruptDelayState = InterruptDelayState.EnabledWithDelay;
            allowInterruptCheckThisCycle = false;
            if (_verboseLogging)
            {
                Console.WriteLine($"[CPU State PC={currentPC:X4}] Applied Pending IME Enable. IME=True. Interrupt check DELAYED 1 cycle.");
            }
        }
        else if (_interruptDelayState == InterruptDelayState.EnabledWithDelay)
        {
            // Delay is over, allow check this cycle
            _interruptDelayState = InterruptDelayState.None;
            allowInterruptCheckThisCycle = _interruptsEnabled; // Check only if IME is still enabled
            
            if (_verboseLogging)
            {
                Console.WriteLine($"[CPU State PC={currentPC:X4}] EI/RETI Delay Over. Interrupt check ALLOWED (IME={_interruptsEnabled}).");
            }
        }
        else // _interruptDelayState == InterruptDelayState.None
        {
            allowInterruptCheckThisCycle = _interruptsEnabled; // Allow check if IME is normally enabled
        }


        // Check for and Handle Interrupts (Conditional)
        // Only check if IME is on AND the EI/RETI delay mechanism allows it
        if (allowInterruptCheckThisCycle && _interruptsEnabled)
        {
            var interruptCycles = HandleInterrupts();
            if (interruptCycles > 0)
            {
                // Interrupt was serviced
                return interruptCycles; // Return the 20 cycles for interrupt handling
            }
        }

        // Return Instruction Cycles
        return instructionCycles;
    }

    /// <summary>
    /// Checks for and handles interrupts.
    /// </summary>
    /// <returns>The number of cycles consumed by interrupt handling, or 0 if no interrupt was handled.</returns>
private int HandleInterrupts()
{
    // Initial check if IME is enabled (redundant with Step logic, but safe)
    if (!_interruptsEnabled) return 0;
    
    _justReturnedFromInterrupt = false;

    byte ieRegister = _mmu.ReadByte(0xFFFF);
    byte ifRegisterInitial = _mmu.ReadByte(0xFF0F); // Read IF at start

    byte pendingInterrupts = (byte)(ieRegister & ifRegisterInitial & 0x1F);
    if (pendingInterrupts == 0) return 0; // No enabled & requested interrupts

    // Log only when pending interrupts ARE found
    if (_verboseLogging)
    {
        Console.WriteLine($"[CPU IRQ Check PC={Registers.PC:X4}] Found Pending=0x{pendingInterrupts:X2} (IF=0x{ifRegisterInitial:X2}, IE=0x{ieRegister:X2}, IME=True)");
    }

    _isHalted = false; // Wake from halt
    _interruptsEnabled = false; // <<< Disable IME DURING handling
    _interruptDelayState = InterruptDelayState.None; // Handling an interrupt cancels any pending EI/RETI schedule/delay

    for (int i = 0; i < 5; i++) {
        if ((pendingInterrupts & (1 << i)) != 0) {
            InterruptType type = (InterruptType)i;
            if (_verboseLogging)
            {
                Console.WriteLine($"[CPU PC={Registers.PC:X4}] ---> Handling Interrupt: {type}");
            }

            byte ifClearedValue = (byte)(ifRegisterInitial & ~(1 << i)); // Use initial read value for clearing calculation
            _mmu.WriteByte(0xFF0F, ifClearedValue); // Clear IF bit
            byte ifAfterClear = _mmu.ReadByte(0xFF0F); // Read back immediately
            // Log IF Clear Success/Failure clearly
            if (_verboseLogging)
            {
                Console.WriteLine($"[CPU] Cleared IF bit {i}. IF was=0x{ifRegisterInitial:X2}, Wrote=0x{ifClearedValue:X2}, READ BACK IF=0x{ifAfterClear:X2}");
            }

            ushort returnPc = Registers.PC; // PC of instruction AFTER the interrupted one
            Push(returnPc); // Push return address

            ushort handlerAddress = (ushort)(0x0040 + (i * 0x0008));
            if (_verboseLogging)
            {
                Console.WriteLine($"[CPU] Interrupt Push PC=0x{returnPc:X4}. Jumping to 0x{handlerAddress:X4}");
            }
            Registers.PC = handlerAddress; // Set PC to handler

            return 20; // Interrupt cost
        }
    }
    return 0;
}

    /// <summary>
    /// Executes a single opcode.
    /// </summary>
    /// <param name="opcode">The opcode to execute.</param>
    /// <returns>The number of cycles consumed by the instruction.</returns>
    private int ExecuteOpcode(byte opcode)
    {
        switch (opcode)
        {
            // NOP - No operation
            case 0x00:
                return 4;
            
            // LD BC, d16 - Load 16-bit immediate into BC
            case 0x01:
                Registers.C = ReadNextByte();
                Registers.B = ReadNextByte();
                return 12;

            // LD (BC), A - Store A to address in BC
            case 0x02:
                _mmu.WriteByte(Registers.BC, Registers.A);
                return 8;

            // INC BC - Increment BC
            case 0x03:
                Registers.BC++;
                return 8;

            // INC B - Increment B
            case 0x04:
                Registers.B = Increment(Registers.B);
                return 4;

            // DEC B - Decrement B
            case 0x05:
                Registers.B = Decrement(Registers.B);
                return 4;

            // LD B, d8 - Load 8-bit immediate into B
            case 0x06:
                Registers.B = ReadNextByte();
                return 8;

            // RLCA - Rotate A left with carry
            case 0x07:
                RotateALeftWithCarry();
                return 4;

            // LD (a16), SP - Store SP at 16-bit address
            case 0x08:
            {
                ushort address = ReadNextWord();
                _mmu.WriteWord(address, Registers.SP);
                return 20;
            }

            // ADD HL, BC - Add BC to HL
            case 0x09:
                AddToHL(Registers.BC);
                return 8;

            // LD A, (BC) - Load A from address in BC
            case 0x0A:
                Registers.A = _mmu.ReadByte(Registers.BC);
                return 8;

            // DEC BC - Decrement BC
            case 0x0B:
                Registers.BC--;
                return 8;

            // INC C - Increment C
            case 0x0C:
                Registers.C = Increment(Registers.C);
                return 4;

            // DEC C - Decrement C
            case 0x0D:
                Registers.C = Decrement(Registers.C);
                return 4;

            // LD C, d8 - Load 8-bit immediate into C
            case 0x0E:
                Registers.C = ReadNextByte();
                return 8;

            // RRCA - Rotate A right with carry
            case 0x0F:
                RotateARightWithCarry();
                return 4;

            // STOP - Enter STOP mode
            case 0x10:
                _isStopped = true;
                // In GBC, STOP can be used to toggle double speed mode
                if (_mmu.IsGbcMode && _mmu.ReadByte(0xFF4D) == 0x01)
                {
                    _doubleSpeedMode = !_doubleSpeedMode;
                    _mmu.WriteByte(0xFF4D, (byte)(_doubleSpeedMode ? 0x80 : 0x00));
                    _isStopped = false; // Continue execution after toggling speed
                }

                Registers.PC++; // STOP is a 2-byte instruction (0x10 0x00)
                return 4;

            // LD DE, d16 - Load 16-bit immediate into DE
            case 0x11:
                Registers.E = ReadNextByte();
                Registers.D = ReadNextByte();
                return 12;

            // LD (DE), A - Store A to address in DE
            case 0x12:
                _mmu.WriteByte(Registers.DE, Registers.A);
                return 8;

            // INC DE - Increment DE
            case 0x13:
                Registers.DE++;
                return 8;

            // INC D - Increment D
            case 0x14:
                Registers.D = Increment(Registers.D);
                return 4;

            // DEC D - Decrement D
            case 0x15:
                Registers.D = Decrement(Registers.D);
                return 4;

            // LD D, d8 - Load 8-bit immediate into D
            case 0x16:
                Registers.D = ReadNextByte();
                return 8;

            // RLA - Rotate A left through carry
            case 0x17:
                RotateALeftThroughCarry();
                return 4;

            // JR r8 - Relative jump by signed immediate
            case 0x18:
            {
                sbyte offset = (sbyte)ReadNextByte();
                Registers.PC = (ushort)(Registers.PC + offset);
                return 12;
            }

            // ADD HL, DE - Add DE to HL
            case 0x19:
                AddToHL(Registers.DE);
                return 8;

            // LD A, (DE) - Load A from address in DE
            case 0x1A:
                Registers.A = _mmu.ReadByte(Registers.DE);
                return 8;

            // DEC DE - Decrement DE
            case 0x1B:
                Registers.DE--;
                return 8;

            // INC E - Increment E
            case 0x1C:
                Registers.E = Increment(Registers.E);
                return 4;

            // DEC E - Decrement E
            case 0x1D:
                Registers.E = Decrement(Registers.E);
                return 4;

            // LD E, d8 - Load 8-bit immediate into E
            case 0x1E:
                Registers.E = ReadNextByte();
                return 8;

            // RRA - Rotate A right through carry
            case 0x1F:
                RotateARightThroughCarry();
                return 4;

            // JR NZ, r8 - Relative jump if Z flag is reset
            case 0x20:
            {
                sbyte offset = (sbyte)ReadNextByte();
                if (!Registers.ZeroFlag)
                {
                    Registers.PC = (ushort)(Registers.PC + offset);
                    return 12;
                }

                return 8;
            }

            // LD HL, d16 - Load 16-bit immediate into HL
            case 0x21:
                Registers.L = ReadNextByte();
                Registers.H = ReadNextByte();
                return 12;

            // LD (HL+), A - Store A at address in HL, then increment HL
            case 0x22:
                _mmu.WriteByte(Registers.HL, Registers.A);
                Registers.HL++;
                return 8;

            // INC HL - Increment HL
            case 0x23:
                Registers.HL++;
                return 8;

            // INC H - Increment H
            case 0x24:
                Registers.H = Increment(Registers.H);
                return 4;

            // DEC H - Decrement H
            case 0x25:
                Registers.H = Decrement(Registers.H);
                return 4;

            // LD H, d8 - Load 8-bit immediate into H
            case 0x26:
                Registers.H = ReadNextByte();
                return 8;

            // DAA - Decimal adjust A for BCD arithmetic
            case 0x27:
                DecimalAdjustA();
                return 4;

            // JR Z, r8 - Relative jump if Z flag is set
            case 0x28:
            {
                sbyte offset = (sbyte)ReadNextByte();
                if (Registers.ZeroFlag)
                {
                    Registers.PC = (ushort)(Registers.PC + offset);
                    return 12;
                }

                return 8;
            }

            // ADD HL, HL - Add HL to HL
            case 0x29:
                AddToHL(Registers.HL);
                return 8;

            // LD A, (HL+) - Load A from address in HL, then increment HL
            case 0x2A:
                Registers.A = _mmu.ReadByte(Registers.HL);
                Registers.HL++;
                return 8;

            // DEC HL - Decrement HL
            case 0x2B:
                Registers.HL--;
                return 8;

            // INC L - Increment L
            case 0x2C:
                Registers.L = Increment(Registers.L);
                return 4;

            // DEC L - Decrement L
            case 0x2D:
                Registers.L = Decrement(Registers.L);
                return 4;

            // LD L, d8 - Load 8-bit immediate into L
            case 0x2E:
                Registers.L = ReadNextByte();
                return 8;

            // CPL - Complement A (flip all bits)
            case 0x2F:
                Registers.A = (byte)~Registers.A;
                Registers.SubtractFlag = true;
                Registers.HalfCarryFlag = true;
                return 4;

            // JR NC, r8 - Relative jump if C flag is reset
            case 0x30:
            {
                sbyte offset = (sbyte)ReadNextByte();
                if (!Registers.CarryFlag)
                {
                    Registers.PC = (ushort)(Registers.PC + offset);
                    return 12;
                }

                return 8;
            }

            // LD SP, d16 - Load 16-bit immediate into SP
            case 0x31:
            {
                ushort value = ReadNextWord();
                Registers.SP = value;
                return 12;
            }

            // LD (HL-), A - Store A at address in HL, then decrement HL
            case 0x32:
                _mmu.WriteByte(Registers.HL, Registers.A);
                Registers.HL--;
                return 8;

            // INC SP - Increment SP
            case 0x33:
                Registers.SP++;
                return 8;

            // INC (HL) - Increment value at address in HL
            case 0x34:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = Increment(value);
                _mmu.WriteByte(Registers.HL, value);
                return 12;
            }

            // DEC (HL) - Decrement value at address in HL
            case 0x35:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = Decrement(value);
                _mmu.WriteByte(Registers.HL, value);
                return 12;
            }

            // LD (HL), d8 - Load 8-bit immediate into address in HL
            case 0x36:
            {
                byte value = ReadNextByte();
                _mmu.WriteByte(Registers.HL, value);
                return 12;
            }

            // SCF - Set Carry Flag
            case 0x37:
                Registers.SubtractFlag = false;
                Registers.HalfCarryFlag = false;
                Registers.CarryFlag = true;
                return 4;

            // JR C, r8 - Relative jump if C flag is set
            case 0x38:
            {
                sbyte offset = (sbyte)ReadNextByte();
                if (Registers.CarryFlag)
                {
                    Registers.PC = (ushort)(Registers.PC + offset);
                    return 12;
                }

                return 8;
            }

            // ADD HL, SP - Add SP to HL
            case 0x39:
                AddToHL(Registers.SP);
                return 8;

            // LD A, (HL-) - Load A from address in HL, then decrement HL
            case 0x3A:
                Registers.A = _mmu.ReadByte(Registers.HL);
                Registers.HL--;
                return 8;

            // DEC SP - Decrement SP
            case 0x3B:
                Registers.SP--;
                return 8;

            // INC A - Increment A
            case 0x3C:
                Registers.A = Increment(Registers.A);
                return 4;

            // DEC A - Decrement A
            case 0x3D:
                Registers.A = Decrement(Registers.A);
                return 4;

            // LD A, d8 - Load 8-bit immediate into A
            case 0x3E:
                Registers.A = ReadNextByte();
                return 8;

            // CCF - Complement Carry Flag
            case 0x3F:
                Registers.SubtractFlag = false;
                Registers.HalfCarryFlag = false;
                Registers.CarryFlag = !Registers.CarryFlag;
                return 4;

            // LD B, B - Load B into B (no-op)
            case 0x40:
                return 4;

            // LD B, C - Load C into B
            case 0x41:
                Registers.B = Registers.C;
                return 4;

            // LD B, D - Load D into B
            case 0x42:
                Registers.B = Registers.D;
                return 4;

            // LD B, E - Load E into B
            case 0x43:
                Registers.B = Registers.E;
                return 4;

            // LD B, H - Load H into B
            case 0x44:
                Registers.B = Registers.H;
                return 4;

            // LD B, L - Load L into B
            case 0x45:
                Registers.B = Registers.L;
                return 4;

            // LD B, (HL) - Load value at address in HL into B
            case 0x46:
                Registers.B = _mmu.ReadByte(Registers.HL);
                return 8;

            // LD B, A - Load A into B
            case 0x47:
                Registers.B = Registers.A;
                return 4;

            // LD C, B - Load B into C
            case 0x48:
                Registers.C = Registers.B;
                return 4;

            // LD C, C - Load C into C (no-op)
            case 0x49:
                return 4;

            // LD C, D - Load D into C
            case 0x4A:
                Registers.C = Registers.D;
                return 4;

            // LD C, E - Load E into C
            case 0x4B:
                Registers.C = Registers.E;
                return 4;

            // LD C, H - Load H into C
            case 0x4C:
                Registers.C = Registers.H;
                return 4;

            // LD C, L - Load L into C
            case 0x4D:
                Registers.C = Registers.L;
                return 4;

            // LD C, (HL) - Load value at address in HL into C
            case 0x4E:
                Registers.C = _mmu.ReadByte(Registers.HL);
                return 8;

            // LD C, A - Load A into C
            case 0x4F:
                Registers.C = Registers.A;
                return 4;

            // LD D, B - Load B into D
            case 0x50:
                Registers.D = Registers.B;
                return 4;

            // LD D, C - Load C into D
            case 0x51:
                Registers.D = Registers.C;
                return 4;

            // LD D, D - Load D into D (no-op)
            case 0x52:
                return 4;

            // LD D, E - Load E into D
            case 0x53:
                Registers.D = Registers.E;
                return 4;

            // LD D, H - Load H into D
            case 0x54:
                Registers.D = Registers.H;
                return 4;

            // LD D, L - Load L into D
            case 0x55:
                Registers.D = Registers.L;
                return 4;

            // LD D, (HL) - Load value at address in HL into D
            case 0x56:
                Registers.D = _mmu.ReadByte(Registers.HL);
                return 8;

            // LD D, A - Load A into D
            case 0x57:
                Registers.D = Registers.A;
                return 4;

            // LD E, B - Load B into E
            case 0x58:
                Registers.E = Registers.B;
                return 4;

            // LD E, C - Load C into E
            case 0x59:
                Registers.E = Registers.C;
                return 4;

            // LD E, D - Load D into E
            case 0x5A:
                Registers.E = Registers.D;
                return 4;

            // LD E, E - Load E into E (no-op)
            case 0x5B:
                return 4;

            // LD E, H - Load H into E
            case 0x5C:
                Registers.E = Registers.H;
                return 4;

            // LD E, L - Load L into E
            case 0x5D:
                Registers.E = Registers.L;
                return 4;

            // LD E, (HL) - Load value at address in HL into E
            case 0x5E:
                Registers.E = _mmu.ReadByte(Registers.HL);
                return 8;

            // LD E, A - Load A into E
            case 0x5F:
                Registers.E = Registers.A;
                return 4;

            // LD H, B - Load B into H
            case 0x60:
                Registers.H = Registers.B;
                return 4;

            // LD H, C - Load C into H
            case 0x61:
                Registers.H = Registers.C;
                return 4;

            // LD H, D - Load D into H
            case 0x62:
                Registers.H = Registers.D;
                return 4;

            // LD H, E - Load E into H
            case 0x63:
                Registers.H = Registers.E;
                return 4;

            // LD H, H - Load H into H (no-op)
            case 0x64:
                return 4;

            // LD H, L - Load L into H
            case 0x65:
                Registers.H = Registers.L;
                return 4;

            // LD H, (HL) - Load value at address in HL into H
            case 0x66:
                Registers.H = _mmu.ReadByte(Registers.HL);
                return 8;

            // LD H, A - Load A into H
            case 0x67:
                Registers.H = Registers.A;
                return 4;

            // LD L, B - Load B into L
            case 0x68:
                Registers.L = Registers.B;
                return 4;

            // LD L, C - Load C into L
            case 0x69:
                Registers.L = Registers.C;
                return 4;

            // LD L, D - Load D into L
            case 0x6A:
                Registers.L = Registers.D;
                return 4;

            // LD L, E - Load E into L
            case 0x6B:
                Registers.L = Registers.E;
                return 4;

            // LD L, H - Load H into L
            case 0x6C:
                Registers.L = Registers.H;
                return 4;

            // LD L, L - Load L into L (no-op)
            case 0x6D:
                return 4;

            // LD L, (HL) - Load value at address in HL into L
            case 0x6E:
                Registers.L = _mmu.ReadByte(Registers.HL);
                return 8;

            // LD L, A - Load A into L
            case 0x6F:
                Registers.L = Registers.A;
                return 4;

            // LD (HL), B - Store B at address in HL
            case 0x70:
                _mmu.WriteByte(Registers.HL, Registers.B);
                return 8;

            // LD (HL), C - Store C at address in HL
            case 0x71:
                _mmu.WriteByte(Registers.HL, Registers.C);
                return 8;

            // LD (HL), D - Store D at address in HL
            case 0x72:
                _mmu.WriteByte(Registers.HL, Registers.D);
                return 8;

            // LD (HL), E - Store E at address in HL
            case 0x73:
                _mmu.WriteByte(Registers.HL, Registers.E);
                return 8;

            // LD (HL), H - Store H at address in HL
            case 0x74:
                _mmu.WriteByte(Registers.HL, Registers.H);
                return 8;

            // LD (HL), L - Store L at address in HL
            case 0x75:
                _mmu.WriteByte(Registers.HL, Registers.L);
                return 8;

            // HALT - Halt until interrupt occurs
            case 0x76:
                _isHalted = true;
                return 4;

            // LD (HL), A - Store A at address in HL
            case 0x77:
                _mmu.WriteByte(Registers.HL, Registers.A);
                return 8;

            // LD A, B - Load B into A
            case 0x78:
                Registers.A = Registers.B;
                return 4;

            // LD A, C - Load C into A
            case 0x79:
                Registers.A = Registers.C;
                return 4;

            // LD A, D - Load D into A
            case 0x7A:
                Registers.A = Registers.D;
                return 4;

            // LD A, E - Load E into A
            case 0x7B:
                Registers.A = Registers.E;
                return 4;

            // LD A, H - Load H into A
            case 0x7C:
                Registers.A = Registers.H;
                return 4;

            // LD A, L - Load L into A
            case 0x7D:
                Registers.A = Registers.L;
                return 4;

            // LD A, (HL) - Load value at address in HL into A
            case 0x7E:
                Registers.A = _mmu.ReadByte(Registers.HL);
                return 8;

            // LD A, A - Load A into A (no-op)
            case 0x7F:
                return 4;

            // ADD A, B - Add B to A
            case 0x80:
                AddToA(Registers.B);
                return 4;

            // ADD A, C - Add C to A
            case 0x81:
                AddToA(Registers.C);
                return 4;

            // ADD A, D - Add D to A
            case 0x82:
                AddToA(Registers.D);
                return 4;

            // ADD A, E - Add E to A
            case 0x83:
                AddToA(Registers.E);
                return 4;

            // ADD A, H - Add H to A
            case 0x84:
                AddToA(Registers.H);
                return 4;

            // ADD A, L - Add L to A
            case 0x85:
                AddToA(Registers.L);
                return 4;

            // ADD A, (HL) - Add value at address in HL to A
            case 0x86:
                AddToA(_mmu.ReadByte(Registers.HL));
                return 8;

            // ADD A, A - Add A to A
            case 0x87:
                AddToA(Registers.A);
                return 4;

            // ADC A, B - Add B and carry flag to A
            case 0x88:
                AddToA(Registers.B, true);
                return 4;

            // ADC A, C - Add C and carry flag to A
            case 0x89:
                AddToA(Registers.C, true);
                return 4;

            // ADC A, D - Add D and carry flag to A
            case 0x8A:
                AddToA(Registers.D, true);
                return 4;

            // ADC A, E - Add E and carry flag to A
            case 0x8B:
                AddToA(Registers.E, true);
                return 4;

            // ADC A, H - Add H and carry flag to A
            case 0x8C:
                AddToA(Registers.H, true);
                return 4;

            // ADC A, L - Add L and carry flag to A
            case 0x8D:
                AddToA(Registers.L, true);
                return 4;

            // ADC A, (HL) - Add value at address in HL and carry flag to A
            case 0x8E:
                AddToA(_mmu.ReadByte(Registers.HL), true);
                return 8;

            // ADC A, A - Add A and carry flag to A
            case 0x8F:
                AddToA(Registers.A, true);
                return 4;

            // SUB B - Subtract B from A
            case 0x90:
                SubFromA(Registers.B);
                return 4;

            // SUB C - Subtract C from A
            case 0x91:
                SubFromA(Registers.C);
                return 4;

            // SUB D - Subtract D from A
            case 0x92:
                SubFromA(Registers.D);
                return 4;

            // SUB E - Subtract E from A
            case 0x93:
                SubFromA(Registers.E);
                return 4;

            // SUB H - Subtract H from A
            case 0x94:
                SubFromA(Registers.H);
                return 4;

            // SUB L - Subtract L from A
            case 0x95:
                SubFromA(Registers.L);
                return 4;

            // SUB (HL) - Subtract value at address in HL from A
            case 0x96:
                SubFromA(_mmu.ReadByte(Registers.HL));
                return 8;

            // SUB A - Subtract A from A
            case 0x97:
                SubFromA(Registers.A);
                return 4;

            // SBC A, B - Subtract B and carry flag from A
            case 0x98:
                SubFromA(Registers.B, true);
                return 4;

            // SBC A, C - Subtract C and carry flag from A
            case 0x99:
                SubFromA(Registers.C, true);
                return 4;

            // SBC A, D - Subtract D and carry flag from A
            case 0x9A:
                SubFromA(Registers.D, true);
                return 4;

            // SBC A, E - Subtract E and carry flag from A
            case 0x9B:
                SubFromA(Registers.E, true);
                return 4;

            // SBC A, H - Subtract H and carry flag from A
            case 0x9C:
                SubFromA(Registers.H, true);
                return 4;

            // SBC A, L - Subtract L and carry flag from A
            case 0x9D:
                SubFromA(Registers.L, true);
                return 4;

            // SBC A, (HL) - Subtract value at address in HL and carry flag from A
            case 0x9E:
                SubFromA(_mmu.ReadByte(Registers.HL), true);
                return 8;

            // SBC A, A - Subtract A and carry flag from A
            case 0x9F:
                SubFromA(Registers.A, true);
                return 4;

            // AND B - Logical AND B with A
            case 0xA0:
                AndWithA(Registers.B);
                return 4;

            // AND C - Logical AND C with A
            case 0xA1:
                AndWithA(Registers.C);
                return 4;

            // AND D - Logical AND D with A
            case 0xA2:
                AndWithA(Registers.D);
                return 4;

            // AND E - Logical AND E with A
            case 0xA3:
                AndWithA(Registers.E);
                return 4;

            // AND H - Logical AND H with A
            case 0xA4:
                AndWithA(Registers.H);
                return 4;

            // AND L - Logical AND L with A
            case 0xA5:
                AndWithA(Registers.L);
                return 4;

            // AND (HL) - Logical AND value at address in HL with A
            case 0xA6:
                AndWithA(_mmu.ReadByte(Registers.HL));
                return 8;

            // AND A - Logical AND A with A
            case 0xA7:
                AndWithA(Registers.A);
                return 4;

            // XOR B - Logical XOR B with A
            case 0xA8:
                XorWithA(Registers.B);
                return 4;

            // XOR C - Logical XOR C with A
            case 0xA9:
                XorWithA(Registers.C);
                return 4;

            // XOR D - Logical XOR D with A
            case 0xAA:
                XorWithA(Registers.D);
                return 4;

            // XOR E - Logical XOR E with A
            case 0xAB:
                XorWithA(Registers.E);
                return 4;

            // XOR H - Logical XOR H with A
            case 0xAC:
                XorWithA(Registers.H);
                return 4;

            // XOR L - Logical XOR L with A
            case 0xAD:
                XorWithA(Registers.L);
                return 4;

            // XOR (HL) - Logical XOR value at address in HL with A
            case 0xAE:
                XorWithA(_mmu.ReadByte(Registers.HL));
                return 8;

            // XOR A - Logical XOR A with A
            case 0xAF:
                XorWithA(Registers.A);
                return 4;

            // OR B - Logical OR B with A
            case 0xB0:
                OrWithA(Registers.B);
                return 4;

            // OR C - Logical OR C with A
            case 0xB1:
                OrWithA(Registers.C);
                return 4;

            // OR D - Logical OR D with A
            case 0xB2:
                OrWithA(Registers.D);
                return 4;

            // OR E - Logical OR E with A
            case 0xB3:
                OrWithA(Registers.E);
                return 4;

            // OR H - Logical OR H with A
            case 0xB4:
                OrWithA(Registers.H);
                return 4;

            // OR L - Logical OR L with A
            case 0xB5:
                OrWithA(Registers.L);
                return 4;

            // OR (HL) - Logical OR value at address in HL with A
            case 0xB6:
                OrWithA(_mmu.ReadByte(Registers.HL));
                return 8;

            // OR A - Logical OR A with A
            case 0xB7:
                OrWithA(Registers.A);
                return 4;

            // CP B - Compare B with A
            case 0xB8:
                CompareWithA(Registers.B);
                return 4;

            // CP C - Compare C with A
            case 0xB9:
                CompareWithA(Registers.C);
                return 4;

            // CP D - Compare D with A
            case 0xBA:
                CompareWithA(Registers.D);
                return 4;

            // CP E - Compare E with A
            case 0xBB:
                CompareWithA(Registers.E);
                return 4;

            // CP H - Compare H with A
            case 0xBC:
                CompareWithA(Registers.H);
                return 4;

            // CP L - Compare L with A
            case 0xBD:
                CompareWithA(Registers.L);
                return 4;

            // CP (HL) - Compare value at address in HL with A
            case 0xBE:
                CompareWithA(_mmu.ReadByte(Registers.HL));
                return 8;

            // CP A - Compare A with A
            case 0xBF:
                CompareWithA(Registers.A);
                return 4;

            // RET NZ - Return if Z flag is reset
            case 0xC0:
                if (!Registers.ZeroFlag)
                {
                    Return();
                    return 20;
                }

                return 8;

            // POP BC - Pop value from stack into BC
            case 0xC1:
                Registers.BC = Pop();
                return 12;

            // JP NZ, a16 - Jump to address if Z flag is reset
            case 0xC2:
            {
                ushort address = ReadNextWord();
                if (!Registers.ZeroFlag)
                {
                    Registers.PC = address;
                    return 16;
                }

                return 12;
            }

            // JP a16 - Jump to address
            case 0xC3:
                Registers.PC = ReadNextWord();
                return 16;

            // CALL NZ, a16 - Call address if Z flag is reset
            case 0xC4:
            {
                ushort address = ReadNextWord();
                if (!Registers.ZeroFlag)
                {
                    Call(address);
                    return 24;
                }

                return 12;
            }

            // PUSH BC - Push BC onto stack
            case 0xC5:
                Push(Registers.BC);
                return 16;

            // ADD A, d8 - Add immediate to A
            case 0xC6:
                AddToA(ReadNextByte());
                return 8;

            // RST 00H - Restart to address 0x0000
            case 0xC7:
                Restart(0x00);
                return 16;

            // RET Z - Return if Z flag is set
            case 0xC8:
                if (Registers.ZeroFlag)
                {
                    Return();
                    return 20;
                }

                return 8;

            // RET - Return unconditionally
            case 0xC9:
                Return();
                return 16;

            // JP Z, a16 - Jump to address if Z flag is set
            case 0xCA:
            {
                ushort address = ReadNextWord();
                if (Registers.ZeroFlag)
                {
                    Registers.PC = address;
                    return 16;
                }

                return 12;
            }

            // CB prefix - Extended opcode set
            case 0xCB:
                return ExecuteCBPrefixedOpcode();

            // CALL Z, a16 - Call address if Z flag is set
            case 0xCC:
            {
                ushort address = ReadNextWord();
                if (Registers.ZeroFlag)
                {
                    Call(address);
                    return 24;
                }

                return 12;
            }

            // CALL a16 - Call address unconditionally
            case 0xCD:
                Call(ReadNextWord());
                return 24;

            // ADC A, d8 - Add immediate and carry flag to A
            case 0xCE:
                AddToA(ReadNextByte(), true);
                return 8;

            // RST 08H - Restart to address 0x0008
            case 0xCF:
                Restart(0x08);
                return 16;

            // RET NC - Return if C flag is reset
            case 0xD0:
                if (!Registers.CarryFlag)
                {
                    Return();
                    return 20;
                }

                return 8;

            // POP DE - Pop value from stack into DE
            case 0xD1:
                Registers.DE = Pop();
                return 12;

            // JP NC, a16 - Jump to address if C flag is reset
            case 0xD2:
            {
                ushort address = ReadNextWord();
                if (!Registers.CarryFlag)
                {
                    Registers.PC = address;
                    return 16;
                }

                return 12;
            }

            // Invalid opcode (0xD3)
            case 0xD3:
                return 4;

            // CALL NC, a16 - Call address if C flag is reset
            case 0xD4:
            {
                ushort address = ReadNextWord();
                if (!Registers.CarryFlag)
                {
                    Call(address);
                    return 24;
                }

                return 12;
            }

            // PUSH DE - Push DE onto stack
            case 0xD5:
                Push(Registers.DE);
                return 16;

            // SUB d8 - Subtract immediate from A
            case 0xD6:
                SubFromA(ReadNextByte());
                return 8;

            // RST 10H - Restart to address 0x0010
            case 0xD7:
                Restart(0x10);
                return 16;

            // RET C - Return if C flag is set
            case 0xD8:
                if (Registers.CarryFlag)
                {
                    Return();
                    return 20;
                }

                return 8;

            // RETI - Return and enable interrupts
            case 0xD9:
                
                _justReturnedFromInterrupt = true;
                _instructionAfterRetiCount = 0;
                
                ushort returnAddress = Pop(); // Pop return address first
                if (_verboseLogging)
                {
                    Console.WriteLine($"[CPU Opcode] RETI executed at 0x{Registers.PC-1:X4}. Returning to 0x{returnAddress:X4}. Scheduling IME Enable.");
                }
                Registers.PC = returnAddress;
                // Schedule IME enable if not already pending/active with delay
                if (_interruptDelayState == InterruptDelayState.None && !_interruptsEnabled)
                {
                    _interruptDelayState = InterruptDelayState.PendingEnable;
                }
                return 16;

            // JP C, a16 - Jump to address if C flag is set
            case 0xDA:
            {
                ushort address = ReadNextWord();
                if (Registers.CarryFlag)
                {
                    Registers.PC = address;
                    return 16;
                }

                return 12;
            }

            // Invalid opcode (0xDB)
            case 0xDB:
                return 4;

            // CALL C, a16 - Call address if C flag is set
            case 0xDC:
            {
                ushort address = ReadNextWord();
                if (Registers.CarryFlag)
                {
                    Call(address);
                    return 24;
                }

                return 12;
            }

            // Invalid opcode (0xDD)
            case 0xDD:
                return 4;

            // SBC A, d8 - Subtract immediate and carry flag from A
            case 0xDE:
                SubFromA(ReadNextByte(), true);
                return 8;

            // RST 18H - Restart to address 0x0018
            case 0xDF:
                Restart(0x18);
                return 16;

            // LDH (a8), A - Store A at address 0xFF00 + immediate
            case 0xE0:
            {
                byte offset = ReadNextByte();
                _mmu.WriteByte((ushort)(0xFF00 + offset), Registers.A);
                return 12;
            }

            // POP HL - Pop value from stack into HL
            case 0xE1:
                Registers.HL = Pop();
                return 12;

            // LD (C), A - Store A at address 0xFF00 + C
            case 0xE2:
                _mmu.WriteByte((ushort)(0xFF00 + Registers.C), Registers.A);
                return 8;

            // Invalid opcode (0xE3)
            case 0xE3:
                return 4;

            // Invalid opcode (0xE4)
            case 0xE4:
                return 4;

            // PUSH HL - Push HL onto stack
            case 0xE5:
                Push(Registers.HL);
                return 16;

            // AND d8 - Logical AND immediate with A
            case 0xE6:
                AndWithA(ReadNextByte());
                return 8;

            // RST 20H - Restart to address 0x0020
            case 0xE7:
                Restart(0x20);
                return 16;

            // ADD SP, r8 - Add signed immediate to SP
            case 0xE8:
            {
                sbyte offset = (sbyte)ReadNextByte();

                // Calculate result
                int result = Registers.SP + offset;

                // Set flags
                Registers.ZeroFlag = false;
                Registers.SubtractFlag = false;

                // Half-carry occurs on bit 3
                Registers.HalfCarryFlag = ((Registers.SP & 0x0F) + (offset & 0x0F)) > 0x0F;

                // Carry occurs on bit 7
                Registers.CarryFlag = ((Registers.SP & 0xFF) + (offset & 0xFF)) > 0xFF;

                // Store result
                Registers.SP = (ushort)result;

                return 16;
            }

            // JP (HL) - Jump to address in HL
            case 0xE9:
                Registers.PC = Registers.HL;
                return 4;

            // LD (a16), A - Store A at immediate address
            case 0xEA:
            {
                ushort address = ReadNextWord();
                _mmu.WriteByte(address, Registers.A);
                return 16;
            }

            // Invalid opcode (0xEB)
            case 0xEB:
                return 4;

            // Invalid opcode (0xEC)
            case 0xEC:
                return 4;

            // Invalid opcode (0xED)
            case 0xED:
                return 4;

            // XOR d8 - Logical XOR immediate with A
            case 0xEE:
                XorWithA(ReadNextByte());
                return 8;

            // RST 28H - Restart to address 0x0028
            case 0xEF:
                Restart(0x28);
                return 16;

            // LDH A, (a8) - Load A from address 0xFF00 + immediate
            case 0xF0:
            {
                byte offset = ReadNextByte();
                Registers.A = _mmu.ReadByte((ushort)(0xFF00 + offset));
                return 12;
            }

            // POP AF - Pop value from stack into AF
            case 0xF1:
                Registers.AF = Pop();
                return 12;

            // LD A, (C) - Load A from address 0xFF00 + C
            case 0xF2:
                Registers.A = _mmu.ReadByte((ushort)(0xFF00 + Registers.C));
                return 8;

            // DI - Disable interrupts
            case 0xF3:
                _interruptsEnabled = false;
                _interruptDelayState = InterruptDelayState.None; // Cancel any pending enable/delay
                return 4;

            // Invalid opcode (0xF4)
            case 0xF4:
                return 4;

            // PUSH AF - Push AF onto stack
            case 0xF5:
                Push(Registers.AF);
                return 16;

            // OR d8 - Logical OR immediate with A
            case 0xF6:
                OrWithA(ReadNextByte());
                return 8;

            // RST 30H - Restart to address 0x0030
            case 0xF7:
                Restart(0x30);
                return 16;

            // LD HL, SP+r8 - Add signed immediate to SP and store in HL
            case 0xF8:
            {
                sbyte offset = (sbyte)ReadNextByte();

                // Calculate result
                int result = Registers.SP + offset;

                // Set flags
                Registers.ZeroFlag = false;
                Registers.SubtractFlag = false;

                // Half-carry occurs on bit 3
                Registers.HalfCarryFlag = ((Registers.SP & 0x0F) + (offset & 0x0F)) > 0x0F;

                // Carry occurs on bit 7
                Registers.CarryFlag = ((Registers.SP & 0xFF) + (offset & 0xFF)) > 0xFF;

                // Store result
                Registers.HL = (ushort)result;

                return 12;
            }

            // LD SP, HL - Load HL into SP
            case 0xF9:
                Registers.SP = Registers.HL;
                return 8;

            // LD A, (a16) - Load A from immediate address
            case 0xFA:
            {
                ushort address = ReadNextWord();
                Registers.A = _mmu.ReadByte(address);
                return 16;
            }

            // case 0xFB: // EI - Enable interrupts (schedule it)
            case 0xFB:
                // Schedule IME enable if not already pending/active with delay
                if (_interruptDelayState == InterruptDelayState.None && !_interruptsEnabled)
                {
                    _interruptDelayState = InterruptDelayState.PendingEnable;
                    
                }
                return 4;

            // Invalid opcode (0xFC)
            case 0xFC:
                return 4;

            // Invalid opcode (0xFD)
            case 0xFD:
                return 4;

            // CP d8 - Compare immediate with A
            case 0xFE:
                CompareWithA(ReadNextByte());
                return 8;

            // RST 38H - Restart to address 0x0038
            case 0xFF:
                Restart(0x38);
                return 16;

            // Default case for any unimplemented opcodes
            default:
                throw new NotImplementedException($"Opcode 0x{opcode:X2} not implemented");
        }
    }

    /// <summary>
    /// Executes a CB-prefixed opcode.
    /// These are typically bit manipulation instructions.
    /// </summary>
    /// <returns>The number of cycles consumed by the instruction.</returns>
    private int ExecuteCBPrefixedOpcode()
    {
        byte opcode = ReadNextByte();

        switch (opcode)
        {
            // RLC B - Rotate B left with carry
            case 0x00:
                Registers.B = RotateLeftWithCarry(Registers.B);
                return 8;

            // RLC C - Rotate C left with carry
            case 0x01:
                Registers.C = RotateLeftWithCarry(Registers.C);
                return 8;

            // RLC D - Rotate D left with carry
            case 0x02:
                Registers.D = RotateLeftWithCarry(Registers.D);
                return 8;

            // RLC E - Rotate E left with carry
            case 0x03:
                Registers.E = RotateLeftWithCarry(Registers.E);
                return 8;

            // RLC H - Rotate H left with carry
            case 0x04:
                Registers.H = RotateLeftWithCarry(Registers.H);
                return 8;

            // RLC L - Rotate L left with carry
            case 0x05:
                Registers.L = RotateLeftWithCarry(Registers.L);
                return 8;

            // RLC (HL) - Rotate value at address in HL left with carry
            case 0x06:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = RotateLeftWithCarry(value);
                _mmu.WriteByte(Registers.HL, value);
                return 16;
            }

            // RLC A - Rotate A left with carry
            case 0x07:
                Registers.A = RotateLeftWithCarry(Registers.A);
                return 8;

            // RRC B - Rotate B right with carry
            case 0x08:
                Registers.B = RotateRightWithCarry(Registers.B);
                return 8;

            // RRC C - Rotate C right with carry
            case 0x09:
                Registers.C = RotateRightWithCarry(Registers.C);
                return 8;

            // RRC D - Rotate D right with carry
            case 0x0A:
                Registers.D = RotateRightWithCarry(Registers.D);
                return 8;

            // RRC E - Rotate E right with carry
            case 0x0B:
                Registers.E = RotateRightWithCarry(Registers.E);
                return 8;

            // RRC H - Rotate H right with carry
            case 0x0C:
                Registers.H = RotateRightWithCarry(Registers.H);
                return 8;

            // RRC L - Rotate L right with carry
            case 0x0D:
                Registers.L = RotateRightWithCarry(Registers.L);
                return 8;

            // RRC (HL) - Rotate value at address in HL right with carry
            case 0x0E:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = RotateRightWithCarry(value);
                _mmu.WriteByte(Registers.HL, value);
                return 16;
            }

            // RRC A - Rotate A right with carry
            case 0x0F:
                Registers.A = RotateRightWithCarry(Registers.A);
                return 8;

            // RL B - Rotate B left through carry
            case 0x10:
                Registers.B = RotateLeftThroughCarry(Registers.B);
                return 8;

            // RL C - Rotate C left through carry
            case 0x11:
                Registers.C = RotateLeftThroughCarry(Registers.C);
                return 8;

            // RL D - Rotate D left through carry
            case 0x12:
                Registers.D = RotateLeftThroughCarry(Registers.D);
                return 8;

            // RL E - Rotate E left through carry
            case 0x13:
                Registers.E = RotateLeftThroughCarry(Registers.E);
                return 8;

            // RL H - Rotate H left through carry
            case 0x14:
                Registers.H = RotateLeftThroughCarry(Registers.H);
                return 8;

            // RL L - Rotate L left through carry
            case 0x15:
                Registers.L = RotateLeftThroughCarry(Registers.L);
                return 8;

            // RL (HL) - Rotate value at address in HL left through carry
            case 0x16:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = RotateLeftThroughCarry(value);
                _mmu.WriteByte(Registers.HL, value);
                return 16;
            }

            // RL A - Rotate A left through carry
            case 0x17:
                Registers.A = RotateLeftThroughCarry(Registers.A);
                return 8;

            // RR B - Rotate B right through carry
            case 0x18:
                Registers.B = RotateRightThroughCarry(Registers.B);
                return 8;

            // RR C - Rotate C right through carry
            case 0x19:
                Registers.C = RotateRightThroughCarry(Registers.C);
                return 8;

            // RR D - Rotate D right through carry
            case 0x1A:
                Registers.D = RotateRightThroughCarry(Registers.D);
                return 8;

            // RR E - Rotate E right through carry
            case 0x1B:
                Registers.E = RotateRightThroughCarry(Registers.E);
                return 8;

            // RR H - Rotate H right through carry
            case 0x1C:
                Registers.H = RotateRightThroughCarry(Registers.H);
                return 8;

            // RR L - Rotate L right through carry
            case 0x1D:
                Registers.L = RotateRightThroughCarry(Registers.L);
                return 8;

            // RR (HL) - Rotate value at address in HL right through carry
            case 0x1E:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = RotateRightThroughCarry(value);
                _mmu.WriteByte(Registers.HL, value);
                return 16;
            }

            // RR A - Rotate A right through carry
            case 0x1F:
                Registers.A = RotateRightThroughCarry(Registers.A);
                return 8;

            // SLA B - Shift B left arithmetic (b0 = 0)
            case 0x20:
                Registers.B = ShiftLeftArithmetic(Registers.B);
                return 8;

            // SLA C - Shift C left arithmetic (b0 = 0)
            case 0x21:
                Registers.C = ShiftLeftArithmetic(Registers.C);
                return 8;

            // SLA D - Shift D left arithmetic (b0 = 0)
            case 0x22:
                Registers.D = ShiftLeftArithmetic(Registers.D);
                return 8;

            // SLA E - Shift E left arithmetic (b0 = 0)
            case 0x23:
                Registers.E = ShiftLeftArithmetic(Registers.E);
                return 8;

            // SLA H - Shift H left arithmetic (b0 = 0)
            case 0x24:
                Registers.H = ShiftLeftArithmetic(Registers.H);
                return 8;

            // SLA L - Shift L left arithmetic (b0 = 0)
            case 0x25:
                Registers.L = ShiftLeftArithmetic(Registers.L);
                return 8;

            // SLA (HL) - Shift value at address in HL left arithmetic (b0 = 0)
            case 0x26:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = ShiftLeftArithmetic(value);
                _mmu.WriteByte(Registers.HL, value);
                return 16;
            }

            // SLA A - Shift A left arithmetic (b0 = 0)
            case 0x27:
                Registers.A = ShiftLeftArithmetic(Registers.A);
                return 8;

            // SRA B - Shift B right arithmetic (b7 unchanged)
            case 0x28:
                Registers.B = ShiftRightArithmetic(Registers.B);
                return 8;

            // SRA C - Shift C right arithmetic (b7 unchanged)
            case 0x29:
                Registers.C = ShiftRightArithmetic(Registers.C);
                return 8;

            // SRA D - Shift D right arithmetic (b7 unchanged)
            case 0x2A:
                Registers.D = ShiftRightArithmetic(Registers.D);
                return 8;

            // SRA E - Shift E right arithmetic (b7 unchanged)
            case 0x2B:
                Registers.E = ShiftRightArithmetic(Registers.E);
                return 8;

            // SRA H - Shift H right arithmetic (b7 unchanged)
            case 0x2C:
                Registers.H = ShiftRightArithmetic(Registers.H);
                return 8;

            // SRA L - Shift L right arithmetic (b7 unchanged)
            case 0x2D:
                Registers.L = ShiftRightArithmetic(Registers.L);
                return 8;

            // SRA (HL) - Shift value at address in HL right arithmetic (b7 unchanged)
            case 0x2E:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = ShiftRightArithmetic(value);
                _mmu.WriteByte(Registers.HL, value);
                return 16;
            }

            // SRA A - Shift A right arithmetic (b7 unchanged)
            case 0x2F:
                Registers.A = ShiftRightArithmetic(Registers.A);
                return 8;

            // SWAP B - Swap nibbles in B
            case 0x30:
                Registers.B = SwapNibbles(Registers.B);
                return 8;

            // SWAP C - Swap nibbles in C
            case 0x31:
                Registers.C = SwapNibbles(Registers.C);
                return 8;

            // SWAP D - Swap nibbles in D
            case 0x32:
                Registers.D = SwapNibbles(Registers.D);
                return 8;

            // SWAP E - Swap nibbles in E
            case 0x33:
                Registers.E = SwapNibbles(Registers.E);
                return 8;

            // SWAP H - Swap nibbles in H
            case 0x34:
                Registers.H = SwapNibbles(Registers.H);
                return 8;

            // SWAP L - Swap nibbles in L
            case 0x35:
                Registers.L = SwapNibbles(Registers.L);
                return 8;

            // SWAP (HL) - Swap nibbles in value at address in HL
            case 0x36:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = SwapNibbles(value);
                _mmu.WriteByte(Registers.HL, value);
                return 16;
            }

            // SWAP A - Swap nibbles in A
            case 0x37:
                Registers.A = SwapNibbles(Registers.A);
                return 8;

            // SRL B - Shift B right logical (b7 = 0)
            case 0x38:
                Registers.B = ShiftRightLogical(Registers.B);
                return 8;

            // SRL C - Shift C right logical (b7 = 0)
            case 0x39:
                Registers.C = ShiftRightLogical(Registers.C);
                return 8;

            // SRL D - Shift D right logical (b7 = 0)
            case 0x3A:
                Registers.D = ShiftRightLogical(Registers.D);
                return 8;

            // SRL E - Shift E right logical (b7 = 0)
            case 0x3B:
                Registers.E = ShiftRightLogical(Registers.E);
                return 8;

            // SRL H - Shift H right logical (b7 = 0)
            case 0x3C:
                Registers.H = ShiftRightLogical(Registers.H);
                return 8;

            // SRL L - Shift L right logical (b7 = 0)
            case 0x3D:
                Registers.L = ShiftRightLogical(Registers.L);
                return 8;

            // SRL (HL) - Shift value at address in HL right logical (b7 = 0)
            case 0x3E:
            {
                byte value = _mmu.ReadByte(Registers.HL);
                value = ShiftRightLogical(value);
                _mmu.WriteByte(Registers.HL, value);
                return 16;
            }

            // SRL A - Shift A right logical (b7 = 0)
            case 0x3F:
                Registers.A = ShiftRightLogical(Registers.A);
                return 8;

            // BIT instructions (0x40-0x7F)
            // BIT n, r - Test bit n in register r
            case >= 0x40 and <= 0x7F:
            {
                int bit = (opcode >> 3) & 0x07;

                // Get the value to test based on the opcode
                byte value = GetRegisterValueForBitOpcodes(opcode);

                // Test the bit
                bool bitSet = (value & (1 << bit)) != 0;

                // Set flags
                Registers.ZeroFlag = !bitSet;
                Registers.SubtractFlag = false;
                Registers.HalfCarryFlag = true;

                // Return the cycle count (different for (HL))
                return (opcode & 0x07) == 0x06 ? 12 : 8;
            }

            // RES instructions (0x80-0xBF)
            // RES n, r - Reset bit n in register r
            case >= 0x80 and <= 0xBF:
            {
                int bit = (opcode >> 3) & 0x07;
                byte mask = (byte)~(1 << bit);

                // Reset the bit and store the result
                if ((opcode & 0x07) == 0x06)
                {
                    // (HL) case
                    byte value = _mmu.ReadByte(Registers.HL);
                    value &= mask;
                    _mmu.WriteByte(Registers.HL, value);
                    return 16;
                }
                else
                {
                    // Register case
                    SetRegisterValueForBitOpcodes(opcode, (byte)(GetRegisterValueForBitOpcodes(opcode) & mask));
                    return 8;
                }
            }

            // SET instructions (0xC0-0xFF)
            // SET n, r - Set bit n in register r
            case >= 0xC0 and <= 0xFF:
            {
                int bit = (opcode >> 3) & 0x07;
                byte mask = (byte)(1 << bit);

                // Set the bit and store the result
                if ((opcode & 0x07) == 0x06)
                {
                    // (HL) case
                    byte value = _mmu.ReadByte(Registers.HL);
                    value |= mask;
                    _mmu.WriteByte(Registers.HL, value);
                    return 16;
                }
                else
                {
                    // Register case
                    SetRegisterValueForBitOpcodes(opcode, (byte)(GetRegisterValueForBitOpcodes(opcode) | mask));
                    return 8;
                }
            }

            // Default case (shouldn't be reached)
            default:
                throw new NotImplementedException($"CB-prefixed opcode 0x{opcode:X2} not implemented");
        }
    }

    /// <summary>
    /// Gets the value of a register based on the opcode for bit operations.
    /// </summary>
    /// <param name="opcode">The bit operation opcode.</param>
    /// <returns>The value of the register specified by the opcode.</returns>
    private byte GetRegisterValueForBitOpcodes(byte opcode)
    {
        // Register is encoded in bits 0-2 of the opcode
        switch (opcode & 0x07)
        {
            case 0x00: return Registers.B;
            case 0x01: return Registers.C;
            case 0x02: return Registers.D;
            case 0x03: return Registers.E;
            case 0x04: return Registers.H;
            case 0x05: return Registers.L;
            case 0x06: return _mmu.ReadByte(Registers.HL);
            case 0x07: return Registers.A;
            default: throw new ArgumentOutOfRangeException(nameof(opcode));
        }
    }

    /// <summary>
    /// Sets the value of a register based on the opcode for bit operations.
    /// </summary>
    /// <param name="opcode">The bit operation opcode.</param>
    /// <param name="value">The value to set.</param>
    private void SetRegisterValueForBitOpcodes(byte opcode, byte value)
    {
        // Register is encoded in bits 0-2 of the opcode
        switch (opcode & 0x07)
        {
            case 0x00: Registers.B = value; break;
            case 0x01: Registers.C = value; break;
            case 0x02: Registers.D = value; break;
            case 0x03: Registers.E = value; break;
            case 0x04: Registers.H = value; break;
            case 0x05: Registers.L = value; break;
            case 0x06: _mmu.WriteByte(Registers.HL, value); break;
            case 0x07: Registers.A = value; break;
            default: throw new ArgumentOutOfRangeException(nameof(opcode));
        }
    }

    /// <summary>
    /// Rotates a byte left and sets the carry flag to the bit that was rotated out.
    /// Sets Z flag based on the result.
    /// </summary>
    /// <param name="value">The value to rotate.</param>
    /// <returns>The rotated value.</returns>
    private byte RotateLeftWithCarry(byte value)
    {
        // Get the highest bit
        bool highBit = (value & 0x80) != 0;

        // Rotate left
        value = (byte)((value << 1) | (highBit ? 1 : 0));

        // Set flags
        Registers.ZeroFlag = value == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = highBit;

        return value;
    }

    /// <summary>
    /// Rotates a byte right and sets the carry flag to the bit that was rotated out.
    /// Sets Z flag based on the result.
    /// </summary>
    /// <param name="value">The value to rotate.</param>
    /// <returns>The rotated value.</returns>
    private byte RotateRightWithCarry(byte value)
    {
        // Get the lowest bit
        bool lowBit = (value & 0x01) != 0;

        // Rotate right
        value = (byte)((value >> 1) | (lowBit ? 0x80 : 0));

        // Set flags
        Registers.ZeroFlag = value == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = lowBit;

        return value;
    }

    /// <summary>
    /// Rotates a byte left through carry.
    /// The old carry flag becomes the new bit 0, and the old bit 7 becomes the new carry flag.
    /// Sets Z flag based on the result.
    /// </summary>
    /// <param name="value">The value to rotate.</param>
    /// <returns>The rotated value.</returns>
    private byte RotateLeftThroughCarry(byte value)
    {
        // Get the highest bit
        bool highBit = (value & 0x80) != 0;

        // Rotate left through carry
        value = (byte)((value << 1) | (Registers.CarryFlag ? 1 : 0));

        // Set flags
        Registers.ZeroFlag = value == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = highBit;

        return value;
    }

    /// <summary>
    /// Rotates a byte right through carry.
    /// The old carry flag becomes the new bit 7, and the old bit 0 becomes the new carry flag.
    /// Sets Z flag based on the result.
    /// </summary>
    /// <param name="value">The value to rotate.</param>
    /// <returns>The rotated value.</returns>
    private byte RotateRightThroughCarry(byte value)
    {
        // Get the lowest bit
        bool lowBit = (value & 0x01) != 0;

        // Rotate right through carry
        value = (byte)((value >> 1) | (Registers.CarryFlag ? 0x80 : 0));

        // Set flags
        Registers.ZeroFlag = value == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = lowBit;

        return value;
    }

    /// <summary>
    /// Shifts a byte left arithmetic (b0 = 0).
    /// The old bit 7 becomes the new carry flag.
    /// </summary>
    /// <param name="value">The value to shift.</param>
    /// <returns>The shifted value.</returns>
    private byte ShiftLeftArithmetic(byte value)
    {
        // Get the highest bit
        bool highBit = (value & 0x80) != 0;

        // Shift left arithmetic
        value = (byte)(value << 1);

        // Set flags
        Registers.ZeroFlag = value == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = highBit;

        return value;
    }

    /// <summary>
    /// Shifts a byte right arithmetic (b7 unchanged).
    /// The old bit 0 becomes the new carry flag.
    /// </summary>
    /// <param name="value">The value to shift.</param>
    /// <returns>The shifted value.</returns>
    private byte ShiftRightArithmetic(byte value)
    {
        // Get the lowest bit
        bool lowBit = (value & 0x01) != 0;

        // Get the highest bit
        bool highBit = (value & 0x80) != 0;

        // Shift right arithmetic
        value = (byte)((value >> 1) | (highBit ? 0x80 : 0));

        // Set flags
        Registers.ZeroFlag = value == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = lowBit;

        return value;
    }

    /// <summary>
    /// Shifts a byte right logical (b7 = 0).
    /// The old bit 0 becomes the new carry flag.
    /// </summary>
    /// <param name="value">The value to shift.</param>
    /// <returns>The shifted value.</returns>
    private byte ShiftRightLogical(byte value)
    {
        // Get the lowest bit
        bool lowBit = (value & 0x01) != 0;

        // Shift right logical
        value = (byte)(value >> 1);

        // Set flags
        Registers.ZeroFlag = value == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = lowBit;

        return value;
    }

    /// <summary>
    /// Swaps the high and low nibbles of a byte.
    /// </summary>
    /// <param name="value">The value to swap.</param>
    /// <returns>The swapped value.</returns>
    private byte SwapNibbles(byte value)
    {
        // Swap nibbles
        value = (byte)(((value & 0x0F) << 4) | ((value & 0xF0) >> 4));

        // Set flags
        Registers.ZeroFlag = value == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = false;

        return value;
    }

    /// <summary>
    /// Rotates A right and sets the carry flag to the bit that was rotated out.
    /// </summary>
    private void RotateARightWithCarry()
    {
        byte value = Registers.A;

        // Get the lowest bit
        bool lowBit = (value & 0x01) != 0;

        // Rotate right
        value = (byte)((value >> 1) | (lowBit ? 0x80 : 0));

        // Store the result
        Registers.A = value;

        // Set flags
        Registers.ZeroFlag = false;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = lowBit;
    }

    /// <summary>
    /// Rotates A left through carry.
    /// The old carry flag becomes the new bit 0, and the old bit 7 becomes the new carry flag.
    /// </summary>
    private void RotateALeftThroughCarry()
    {
        byte value = Registers.A;

        // Get the highest bit
        bool highBit = (value & 0x80) != 0;

        // Rotate left through carry
        value = (byte)((value << 1) | (Registers.CarryFlag ? 1 : 0));

        // Store the result
        Registers.A = value;

        // Set flags
        Registers.ZeroFlag = false;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = highBit;
    }

    /// <summary>
    /// Rotates A right through carry.
    /// The old carry flag becomes the new bit 7, and the old bit 0 becomes the new carry flag.
    /// </summary>
    private void RotateARightThroughCarry()
    {
        byte value = Registers.A;

        // Get the lowest bit
        bool lowBit = (value & 0x01) != 0;

        // Rotate right through carry
        value = (byte)((value >> 1) | (Registers.CarryFlag ? 0x80 : 0));

        // Store the result
        Registers.A = value;

        // Set flags
        Registers.ZeroFlag = false;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = lowBit;
    }

    /// <summary>
    /// Performs decimal adjustment on the A register for BCD arithmetic.
    /// </summary>
    private void DecimalAdjustA()
    {
        int val_a = Registers.A;
        bool n_flag = Registers.SubtractFlag;
        bool h_flag = Registers.HalfCarryFlag;
        bool c_flag_original = Registers.CarryFlag;

        bool final_c_flag_to_set = false;

        if (!n_flag)
        {
            if (c_flag_original || val_a > 0x99)
            {
                val_a += 0x60;
                final_c_flag_to_set = true;
            }

            if (h_flag || (Registers.A & 0x0F) > 0x09) val_a += 0x06;
        }
        else
        {
            if (c_flag_original) val_a -= 0x60;
            if (h_flag) val_a -= 0x06;

            final_c_flag_to_set = c_flag_original;
        }

        Registers.A = (byte)val_a;
        Registers.ZeroFlag = (Registers.A == 0);
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = final_c_flag_to_set;
    }

    /// <summary>
    /// Adds a value to A and sets flags accordingly.
    /// </summary>
    /// <param name="value">The value to add to A.</param>
    /// <param name="includeCarry">Whether to include the carry flag in the addition.</param>
    private void AddToA(byte value, bool includeCarry = false)
    {
        byte a = Registers.A;
        byte carryValue = (byte)(includeCarry && Registers.CarryFlag ? 1 : 0);

        // Calculate result
        int result = a + value + carryValue;

        // Set flags
        Registers.ZeroFlag = (result & 0xFF) == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = ((a & 0x0F) + (value & 0x0F) + carryValue) > 0x0F;
        Registers.CarryFlag = result > 0xFF;

        // Store result
        Registers.A = (byte)result;
    }

    /// <summary>
    /// Subtracts a value from A and sets flags accordingly.
    /// </summary>
    /// <param name="value">The value to subtract from A.</param>
    /// <param name="includeCarry">Whether to include the carry flag in the subtraction.</param>
    private void SubFromA(byte value, bool includeCarry = false)
    {
        byte a = Registers.A;
        byte carryValue = (byte)(includeCarry && Registers.CarryFlag ? 1 : 0);

        // Calculate result
        int result = a - value - carryValue;

        // Set flags
        Registers.ZeroFlag = (result & 0xFF) == 0;
        Registers.SubtractFlag = true;
        Registers.HalfCarryFlag = ((a & 0x0F) - (value & 0x0F) - carryValue) < 0;
        Registers.CarryFlag = result < 0;

        // Store result
        Registers.A = (byte)result;
    }

    /// <summary>
    /// Performs a bitwise AND operation between A and the given value.
    /// </summary>
    /// <param name="value">The value to AND with A.</param>
    private void AndWithA(byte value)
    {
        // Calculate result
        byte result = (byte)(Registers.A & value);

        // Set flags
        Registers.ZeroFlag = result == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = true;
        Registers.CarryFlag = false;

        // Store result
        Registers.A = result;
    }

    /// <summary>
    /// Performs a bitwise OR operation between A and the given value.
    /// </summary>
    /// <param name="value">The value to OR with A.</param>
    private void OrWithA(byte value)
    {
        // Calculate result
        byte result = (byte)(Registers.A | value);

        // Set flags
        Registers.ZeroFlag = result == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = false;

        // Store result
        Registers.A = result;
    }

    /// <summary>
    /// Performs a bitwise XOR operation between A and the given value.
    /// </summary>
    /// <param name="value">The value to XOR with A.</param>
    private void XorWithA(byte value)
    {
        // Calculate result
        byte result = (byte)(Registers.A ^ value);

        // Set flags
        Registers.ZeroFlag = result == 0;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = false;

        // Store result
        Registers.A = result;
    }

    /// <summary>
    /// Compares A with the given value (subtracts without storing the result).
    /// </summary>
    /// <param name="value">The value to compare with A.</param>
    private void CompareWithA(byte value)
    {
        byte a = Registers.A;

        // Calculate result (don't store it)
        int result = a - value;

        // Set flags
        Registers.ZeroFlag = (result & 0xFF) == 0;
        Registers.SubtractFlag = true;
        Registers.HalfCarryFlag = ((a & 0x0F) - (value & 0x0F)) < 0;
        Registers.CarryFlag = result < 0;
    }

    /// <summary>
    /// Pushes a 16-bit value onto the stack.
    /// </summary>
    /// <param name="value">The value to push.</param>
    private void Push(ushort value)
    {
        // Decrement SP by 2
        Registers.SP -= 2;

        // Write the value
        _mmu.WriteWord(Registers.SP, value);
    }

    /// <summary>
    /// Pops a 16-bit value from the stack.
    /// </summary>
    /// <returns>The popped value.</returns>
    private ushort Pop()
    {
        // Read the value
        ushort value = _mmu.ReadWord(Registers.SP);

        // Increment SP by 2
        Registers.SP += 2;

        return value;
    }

    /// <summary>
    /// Calls a subroutine at the given address.
    /// </summary>
    /// <param name="address">The address to call.</param>
    private void Call(ushort address)
    {
        // Push the current PC onto the stack
        Push(Registers.PC);

        // Set PC to the call address
        Registers.PC = address;
    }

    /// <summary>
    /// Returns from a subroutine.
    /// </summary>
    private void Return()
    {
        // Pop the return address from the stack
        Registers.PC = Pop();
    }

    /// <summary>
    /// Restarts execution at the given address.
    /// </summary>
    /// <param name="address">The address to restart at (0x00, 0x08, 0x10, etc.).</param>
    private void Restart(byte address)
    {
        // Push the current PC onto the stack
        Push(Registers.PC);

        // Set PC to the restart address
        Registers.PC = address;
    }

    /// <summary>
    /// Reads the next byte from memory and increments PC.
    /// </summary>
    private byte ReadNextByte()
    {
        byte value = _mmu.ReadByte(Registers.PC++);
        return value;
    }

    /// <summary>
    /// Reads the next word (2 bytes) from memory and increments PC by 2.
    /// </summary>
    private ushort ReadNextWord()
    {
        byte low = ReadNextByte();
        byte high = ReadNextByte();
        return (ushort)((high << 8) | low);
    }

    /// <summary>
    /// Increments a byte value and sets the appropriate flags.
    /// </summary>
    private byte Increment(byte value)
    {
        // Check for half-carry (bit 3 to bit 4)
        Registers.HalfCarryFlag = (value & 0x0F) == 0x0F;

        value++;

        // Set zero flag if result is zero
        Registers.ZeroFlag = value == 0;

        // Reset subtract flag
        Registers.SubtractFlag = false;

        return value;
    }

    /// <summary>
    /// Decrements a byte value and sets the appropriate flags.
    /// </summary>
    private byte Decrement(byte value)
    {
        // Check for half-carry (borrow from bit 4)
        Registers.HalfCarryFlag = (value & 0x0F) == 0;

        value--;

        // Set zero flag if result is zero
        Registers.ZeroFlag = value == 0;

        // Set subtract flag
        Registers.SubtractFlag = true;

        return value;
    }

    /// <summary>
    /// Adds a 16-bit value to HL and sets the appropriate flags.
    /// </summary>
    private void AddToHL(ushort value)
    {
        int result = Registers.HL + value;

        // Check for half-carry (bit 11 to bit 12)
        Registers.HalfCarryFlag = ((Registers.HL & 0x0FFF) + (value & 0x0FFF)) > 0x0FFF;

        // Check for carry (result > 16 bits)
        Registers.CarryFlag = result > 0xFFFF;

        // Reset subtract flag
        Registers.SubtractFlag = false;

        // Store the result
        Registers.HL = (ushort)result;
    }

    /// <summary>
    /// Rotates A left and sets the carry flag to the bit that was rotated out.
    /// </summary>
    private void RotateALeftWithCarry()
    {
        byte value = Registers.A;

        // Get the highest bit
        bool highBit = (value & 0x80) != 0;

        // Rotate left
        value = (byte)((value << 1) | (highBit ? 1 : 0));

        // Store the result
        Registers.A = value;

        // Set flags
        Registers.ZeroFlag = false;
        Registers.SubtractFlag = false;
        Registers.HalfCarryFlag = false;
        Registers.CarryFlag = highBit;
    }
    
    private enum InterruptDelayState
    {
        None,           // Normal execution, check interrupts if IME is set
        PendingEnable,  // EI or RETI executed, enable IME after the *next* instruction
        EnabledWithDelay // IME was just enabled, execute one instruction *before* checking interrupts
    }
}

