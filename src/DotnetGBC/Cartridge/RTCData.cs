namespace DotnetGBC.Cartridge;

/// <summary>
/// Represents the state of the Real Time Clock in MBC3 cartridges.
/// </summary>
public struct RTCData
{
    /// <summary>
    /// Seconds counter (0-59)
    /// </summary>
    public byte Seconds;

    /// <summary>
    /// Minutes counter (0-59)
    /// </summary>
    public byte Minutes;

    /// <summary>
    /// Hours counter (0-23)
    /// </summary>
    public byte Hours;

    /// <summary>
    /// Days counter low byte (0-255)
    /// </summary>
    public byte DaysLow;

    /// <summary>
    /// Days counter high bit and control flags
    /// Bit 0: Day counter bit 8
    /// Bit 6: Halt flag
    /// Bit 7: Day counter carry bit
    /// </summary>
    public byte DaysHigh;

    /// <summary>
    /// Unix timestamp when the RTC was last updated
    /// </summary>
    public long LastUpdate;

    /// <summary>
    /// Creates a new RTCData structure initialized to zero.
    /// </summary>
    public static RTCData Zero => new RTCData
    {
        Seconds = 0,
        Minutes = 0,
        Hours = 0,
        DaysLow = 0,
        DaysHigh = 0,
        LastUpdate = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
    };

    /// <summary>
    /// Gets the total days counter (0-511).
    /// </summary>
    public int Days => DaysLow | ((DaysHigh & 0x01) << 8);

    /// <summary>
    /// Gets or sets whether the RTC is halted.
    /// </summary>
    public bool Halted
    {
        get => (DaysHigh & 0x40) != 0;
        set => DaysHigh = (byte)((DaysHigh & ~0x40) | (value ? 0x40 : 0));
    }

    /// <summary>
    /// Gets or sets the day counter carry flag.
    /// </summary>
    public bool DayCounterCarry
    {
        get => (DaysHigh & 0x80) != 0;
        set => DaysHigh = (byte)((DaysHigh & ~0x80) | (value ? 0x80 : 0));
    }

    /// <summary>
    /// Updates the RTC based on elapsed time since LastUpdate.
    /// </summary>
    public void Update()
    {
        if (Halted)
            return;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long elapsed = now - LastUpdate;
            
        if (elapsed <= 0)
            return;

        LastUpdate = now;

        // Add elapsed seconds
        long totalSeconds = Seconds + elapsed;
        Seconds = (byte)(totalSeconds % 60);
        long totalMinutes = Minutes + (totalSeconds / 60);
        Minutes = (byte)(totalMinutes % 60);
        long totalHours = Hours + (totalMinutes / 60);
        Hours = (byte)(totalHours % 24);
        long totalDays = Days + (totalHours / 24);

        if (totalDays > 511)
        {
            // Overflow occurred
            DayCounterCarry = true;
            totalDays %= 512;
        }

        DaysLow = (byte)(totalDays & 0xFF);
        DaysHigh = (byte)((uint)(DaysHigh & 0xFE) | ((totalDays >> 8) & 0x01));
    }
}

