using Xunit;
using DotnetGBC.CPU;

namespace DotnetGBC.Tests.CPU;

public class RegisterTests
{
    private readonly Registers _registers;

    public RegisterTests()
    {
        _registers = new Registers();
    }

    #region Register Initialization Tests

    [Fact]
    public void Constructor_InitializesWithDefaultValues()
    {
        // Act - Constructor was called in setup
            
        // Assert - Should have correct DMG post-boot values
        Assert.Equal(0x01, _registers.A);
        Assert.Equal(0xB0, _registers.F);
        Assert.Equal(0x00, _registers.B);
        Assert.Equal(0x13, _registers.C);
        Assert.Equal(0x00, _registers.D);
        Assert.Equal(0xD8, _registers.E);
        Assert.Equal(0x01, _registers.H);
        Assert.Equal(0x4D, _registers.L);
        Assert.Equal(0xFFFE, _registers.SP);
        Assert.Equal(0x0100, _registers.PC);
    }

    [Fact]
    public void Reset_SetsCorrectDMGValues()
    {
        // Arrange - Change register values
        _registers.A = 0xFF;
        _registers.F = 0xFF;
        _registers.B = 0xFF;
        _registers.PC = 0x1234;
            
        // Act
        _registers.Reset();
            
        // Assert
        Assert.Equal(0x01, _registers.A);
        Assert.Equal(0xB0, _registers.F);
        Assert.Equal(0x00, _registers.B);
        Assert.Equal(0x0100, _registers.PC);
    }

    [Fact]
    public void ResetGbc_SetsCorrectGBCValues()
    {
        // Arrange - Change register values
        _registers.A = 0xFF;
        _registers.F = 0xFF;
        _registers.B = 0xFF;
        _registers.PC = 0x1234;
            
        // Act
        _registers.ResetGbc();
            
        // Assert
        Assert.Equal(0x11, _registers.A);
        Assert.Equal(0x80, _registers.F);
        Assert.Equal(0x00, _registers.B);
        Assert.Equal(0x0100, _registers.PC);
    }

    #endregion

    #region 8-bit Register Tests

    [Fact]
    public void A_Register_SetAndGet()
    {
        // Act
        _registers.A = 0x42;
            
        // Assert
        Assert.Equal(0x42, _registers.A);
    }

    [Fact]
    public void F_Register_OnlyUsesBits4To7()
    {
        // Act
        _registers.F = 0xFF; // Try to set all bits
            
        // Assert
        Assert.Equal(0xF0, _registers.F); // Only bits 4-7 should be set
    }

    [Fact]
    public void AllSingleRegisters_SetAndGet()
    {
        // Arrange
        byte testValue = 0x42;
            
        // Act & Assert
        _registers.A = testValue;
        Assert.Equal(testValue, _registers.A);
            
        _registers.B = testValue;
        Assert.Equal(testValue, _registers.B);
            
        _registers.C = testValue;
        Assert.Equal(testValue, _registers.C);
            
        _registers.D = testValue;
        Assert.Equal(testValue, _registers.D);
            
        _registers.E = testValue;
        Assert.Equal(testValue, _registers.E);
            
        _registers.H = testValue;
        Assert.Equal(testValue, _registers.H);
            
        _registers.L = testValue;
        Assert.Equal(testValue, _registers.L);
            
        // F register is special and masks values
        _registers.F = testValue;
        Assert.Equal(0x40, _registers.F); // Only bit 6 should be set
    }

    #endregion

    #region 16-bit Register Tests

    [Fact]
    public void AF_Register_CombinesTwoRegisters()
    {
        // Arrange
        _registers.A = 0x12;
        _registers.F = 0xF0; // Only bits 4-7 are used
            
        // Act & Assert
        Assert.Equal(0x12F0, _registers.AF);
    }

    [Fact]
    public void AF_Register_SettingUpdatesIndividualRegisters()
    {
        // Act
        _registers.AF = 0x12FF; // F will mask to 0xF0
            
        // Assert
        Assert.Equal(0x12, _registers.A);
        Assert.Equal(0xF0, _registers.F);
    }

    [Fact]
    public void BC_Register_CombinesTwoRegisters()
    {
        // Arrange
        _registers.B = 0x12;
        _registers.C = 0x34;
            
        // Act & Assert
        Assert.Equal(0x1234, _registers.BC);
    }

    [Fact]
    public void BC_Register_SettingUpdatesIndividualRegisters()
    {
        // Act
        _registers.BC = 0x1234;
            
        // Assert
        Assert.Equal(0x12, _registers.B);
        Assert.Equal(0x34, _registers.C);
    }

    [Fact]
    public void DE_Register_CombinesTwoRegisters()
    {
        // Arrange
        _registers.D = 0x12;
        _registers.E = 0x34;
            
        // Act & Assert
        Assert.Equal(0x1234, _registers.DE);
    }

    [Fact]
    public void DE_Register_SettingUpdatesIndividualRegisters()
    {
        // Act
        _registers.DE = 0x1234;
            
        // Assert
        Assert.Equal(0x12, _registers.D);
        Assert.Equal(0x34, _registers.E);
    }

    [Fact]
    public void HL_Register_CombinesTwoRegisters()
    {
        // Arrange
        _registers.H = 0x12;
        _registers.L = 0x34;
            
        // Act & Assert
        Assert.Equal(0x1234, _registers.HL);
    }

    [Fact]
    public void HL_Register_SettingUpdatesIndividualRegisters()
    {
        // Act
        _registers.HL = 0x1234;
            
        // Assert
        Assert.Equal(0x12, _registers.H);
        Assert.Equal(0x34, _registers.L);
    }

    [Fact]
    public void PC_Register_SetAndGet()
    {
        // Act
        _registers.PC = 0x1234;
            
        // Assert
        Assert.Equal(0x1234, _registers.PC);
    }

    [Fact]
    public void SP_Register_SetAndGet()
    {
        // Act
        _registers.SP = 0x1234;
            
        // Assert
        Assert.Equal(0x1234, _registers.SP);
    }

    #endregion

    #region Flag Tests

    [Fact]
    public void ZeroFlag_SetAndGet()
    {
        // Act & Assert
        _registers.ZeroFlag = true;
        Assert.True(_registers.ZeroFlag);
        Assert.Equal(0x80, _registers.F & 0x80); // Bit 7 should be set
            
        _registers.ZeroFlag = false;
        Assert.False(_registers.ZeroFlag);
        Assert.Equal(0x00, _registers.F & 0x80); // Bit 7 should be clear
    }

    [Fact]
    public void SubtractFlag_SetAndGet()
    {
        // Act & Assert
        _registers.SubtractFlag = true;
        Assert.True(_registers.SubtractFlag);
        Assert.Equal(0x40, _registers.F & 0x40); // Bit 6 should be set
            
        _registers.SubtractFlag = false;
        Assert.False(_registers.SubtractFlag);
        Assert.Equal(0x00, _registers.F & 0x40); // Bit 6 should be clear
    }

    [Fact]
    public void HalfCarryFlag_SetAndGet()
    {
        // Act & Assert
        _registers.HalfCarryFlag = true;
        Assert.True(_registers.HalfCarryFlag);
        Assert.Equal(0x20, _registers.F & 0x20); // Bit 5 should be set
            
        _registers.HalfCarryFlag = false;
        Assert.False(_registers.HalfCarryFlag);
        Assert.Equal(0x00, _registers.F & 0x20); // Bit 5 should be clear
    }

    [Fact]
    public void CarryFlag_SetAndGet()
    {
        // Act & Assert
        _registers.CarryFlag = true;
        Assert.True(_registers.CarryFlag);
        Assert.Equal(0x10, _registers.F & 0x10); // Bit 4 should be set
            
        _registers.CarryFlag = false;
        Assert.False(_registers.CarryFlag);
        Assert.Equal(0x00, _registers.F & 0x10); // Bit 4 should be clear
    }

    [Fact]
    public void AllFlags_CanBeSetIndependently()
    {
        // Arrange
        _registers.F = 0x00;
            
        // Act
        _registers.ZeroFlag = true;
        _registers.SubtractFlag = true;
        _registers.HalfCarryFlag = true;
        _registers.CarryFlag = true;
            
        // Assert
        Assert.Equal(0xF0, _registers.F);
        Assert.True(_registers.ZeroFlag);
        Assert.True(_registers.SubtractFlag);
        Assert.True(_registers.HalfCarryFlag);
        Assert.True(_registers.CarryFlag);
            
        // Act - Clear selective flags
        _registers.SubtractFlag = false;
        _registers.CarryFlag = false;
            
        // Assert
        Assert.Equal(0xA0, _registers.F);
        Assert.True(_registers.ZeroFlag);
        Assert.False(_registers.SubtractFlag);
        Assert.True(_registers.HalfCarryFlag);
        Assert.False(_registers.CarryFlag);
    }

    #endregion

    #region ToString Test

    [Fact]
    public void ToString_IncludesAllRegisterValues()
    {
        // Arrange
        _registers.A = 0x12;
        _registers.F = 0xF0;
        _registers.B = 0x34;
        _registers.C = 0x56;
        _registers.D = 0x78;
        _registers.E = 0x9A;
        _registers.H = 0xBC;
        _registers.L = 0xDE;
        _registers.PC = 0xABCD;
        _registers.SP = 0xEF01;
            
        // Act
        string result = _registers.ToString();
            
        // Assert
        Assert.Contains("A: 12", result);
        Assert.Contains("F: F0", result);
        Assert.Contains("B: 34", result);
        Assert.Contains("C: 56", result);
        Assert.Contains("D: 78", result);
        Assert.Contains("E: 9A", result);
        Assert.Contains("H: BC", result);
        Assert.Contains("L: DE", result);
        Assert.Contains("PC: ABCD", result);
        Assert.Contains("SP: EF01", result);
        Assert.Contains("Flags: ZNHC", result); // All flags are set
    }

    [Fact]
    public void ToString_ShowsCorrectFlagRepresentation()
    {
        // Arrange
        _registers.F = 0x00;
            
        // Act & Assert
        Assert.Contains("Flags: ----", _registers.ToString());
            
        _registers.ZeroFlag = true;
        Assert.Contains("Flags: Z---", _registers.ToString());
            
        _registers.SubtractFlag = true;
        Assert.Contains("Flags: ZN--", _registers.ToString());
            
        _registers.HalfCarryFlag = true;
        Assert.Contains("Flags: ZNH-", _registers.ToString());
            
        _registers.CarryFlag = true;
        Assert.Contains("Flags: ZNHC", _registers.ToString());
            
        // Reset selective flags
        _registers.ZeroFlag = false;
        _registers.HalfCarryFlag = false;
        Assert.Contains("Flags: -N-C", _registers.ToString());
    }

    #endregion
}
