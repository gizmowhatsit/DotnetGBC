using DotnetGBC.Memory;
using DotnetGBC.Threading;
using SDL2;

namespace DotnetGBC.Audio;

/// <summary>
/// Audio Processing Unit for Game Boy/Game Boy Color emulation.
/// Handles sound generation and playback via SDL2.
/// </summary>
public class APU : IDisposable
{
    // Game Boy has 4 sound channels
    private const int CHANNEL_COUNT = 4;

    // Audio output settings
    private int _currentSampleRate = 44100;
    private const int BUFFER_SIZE = 2048; // Samples per channel, so total buffer is BUFFER_SIZE * 2 for stereo

    private const double CPU_CYCLES_PER_SECOND_NORMAL = EmulationThread.CYCLES_PER_FRAME * EmulationThread.FRAME_RATE;
    private double _currentCyclesPerSample;

    // Frame Sequencer runs at 512 Hz, stepping through 8 phases.
    // Each phase is 1/512th of a second.
    // CPU Cycles per Frame Sequencer step (normal speed) = 4194304 / 512 = 8192
    // This will be adjusted for double speed if necessary when used.
    private const int FRAME_SEQUENCER_CLOCK_CYCLES_NORMAL = (int)(CPU_CYCLES_PER_SECOND_NORMAL / 512.0);


    // SDL Audio device
    private uint _audioDeviceId;
    private bool _audioEnabled;

    // Reference to MMU for register access
    private readonly MMU _mmu;

    // Audio buffer for sample generation
    private readonly short[] _audioBuffer;
    private int _bufferPosition;
    
    // New timing mechanism
    private long _totalCpuCyclesElapsed = 0;
    private long _totalSamplesGenerated = 0;

    private int _frameSequencerCounter;
    private int _frameSequencerStep;


    // Channel states - used by NR52 status and GenerateSample
    // These should be updated by each channel when its status changes (e.g. after length counter expires or DAC disabled)
    private bool[] _channelIsActive = new bool[CHANNEL_COUNT];

    // Register addresses
    private const ushort NR10_REG = 0xFF10; // Channel 1 Sweep
    private const ushort NR11_REG = 0xFF11; // Channel 1 Length/Wave Duty
    private const ushort NR12_REG = 0xFF12; // Channel 1 Volume Envelope
    private const ushort NR13_REG = 0xFF13; // Channel 1 Frequency (Low)
    private const ushort NR14_REG = 0xFF14; // Channel 1 Frequency (High)/Control

    private const ushort NR21_REG = 0xFF16; // Channel 2 Length/Wave Duty
    private const ushort NR22_REG = 0xFF17; // Channel 2 Volume Envelope
    private const ushort NR23_REG = 0xFF18; // Channel 2 Frequency (Low)
    private const ushort NR24_REG = 0xFF19; // Channel 2 Frequency (High)/Control

    private const ushort NR30_REG = 0xFF1A; // Channel 3 Enable
    private const ushort NR31_REG = 0xFF1B; // Channel 3 Length
    private const ushort NR32_REG = 0xFF1C; // Channel 3 Output Level
    private const ushort NR33_REG = 0xFF1D; // Channel 3 Frequency (Low)
    private const ushort NR34_REG = 0xFF1E; // Channel 3 Frequency (High)/Control
    private const ushort WAVE_RAM_START = 0xFF30; // Channel 3 Wave Pattern RAM
    private const ushort WAVE_RAM_END = 0xFF3F;

    private const ushort NR41_REG = 0xFF20; // Channel 4 Length
    private const ushort NR42_REG = 0xFF21; // Channel 4 Volume Envelope
    private const ushort NR43_REG = 0xFF22; // Channel 4 Polynomial Counter
    private const ushort NR44_REG = 0xFF23; // Channel 4 Control

    private const ushort NR50_REG = 0xFF24; // Master Volume/VIN Panning
    private const ushort NR51_REG = 0xFF25; // Sound Panning
    private const ushort NR52_REG = 0xFF26; // Sound Control

    // Channel implementations
    private PulseChannel _channel1; // Square wave with sweep
    private PulseChannel _channel2; // Square wave
    private WaveChannel _channel3;  // Wave pattern
    private NoiseChannel _channel4; // Noise

    // Master volume
    private int _masterVolumeLeft = 7;
    private int _masterVolumeRight = 7;

    // Panning
    private byte _panningNR51 = 0xF3; // Default power-up

    // Master sound enable
    private bool _masterSoundOn = true;


    public APU(MMU mmu)
    {
        _mmu = mmu;
        UpdateCyclesPerSample(); // Initialize _currentCyclesPerSample
        _audioBuffer = new short[BUFFER_SIZE * 2]; // Stereo, so double the samples

        // Initialize channels
        _channel1 = new PulseChannel(true, this, 0); // With sweep
        _channel2 = new PulseChannel(false, this, 1); // Without sweep
        _channel3 = new WaveChannel(this, 2);
        _channel4 = new NoiseChannel(this, 3);

        // Register handlers for register writes
        RegisterHandlers();

        // Initialize SDL Audio
        InitializeAudio();
        // Call reset after handlers are registered to init MMU registers
        Reset();
    }
    
    private void UpdateCyclesPerSample()
    {
        double effectiveCpuCyclesPerSecond = _mmu.IsDoubleSpeed() ? CPU_CYCLES_PER_SECOND_NORMAL * 2.0 : CPU_CYCLES_PER_SECOND_NORMAL;
        _currentCyclesPerSample = effectiveCpuCyclesPerSecond / _currentSampleRate;
    }

    public void Reset()
    {
        // Reset all channel state
        _channel1.Reset();
        _channel2.Reset();
        _channel3.Reset();
        _channel4.Reset();

        // Reset buffer state
        Array.Clear(_audioBuffer, 0, _audioBuffer.Length);
        _bufferPosition = 0;
        
        _totalCpuCyclesElapsed = 0;
        _totalSamplesGenerated = 0;
        
        _frameSequencerCounter = 0;
        _frameSequencerStep = 0;

        // Reset register state by writing default values through MMU
        // This ensures handlers are called and APU state is consistent
        _mmu.WriteByte(NR10_REG, 0x80);
        _mmu.WriteByte(NR11_REG, 0xBF); 
        _mmu.WriteByte(NR12_REG, 0xF3);
        _mmu.WriteByte(NR14_REG, 0xBF); 

        _mmu.WriteByte(NR21_REG, 0x3F); 
        _mmu.WriteByte(NR22_REG, 0x00);
        _mmu.WriteByte(NR24_REG, 0xBF); 

        _mmu.WriteByte(NR30_REG, 0x7F); 
        _mmu.WriteByte(NR31_REG, 0xFF); 
        _mmu.WriteByte(NR32_REG, 0x9F); 
        _mmu.WriteByte(NR34_REG, 0xBF); 

        _mmu.WriteByte(NR41_REG, 0xFF); 
        _mmu.WriteByte(NR42_REG, 0x00);
        _mmu.WriteByte(NR43_REG, 0x00);
        _mmu.WriteByte(NR44_REG, 0xBF); 

        _mmu.WriteByte(NR50_REG, 0x77);
        _mmu.WriteByte(NR51_REG, 0xF3);
        
        // todo: Come back and validate these values
        // Use 0xF2 for CGB, 0xF3 for DMG?  Works for DMG so far.
        _mmu.WriteByte(NR52_REG, _mmu.IsGbcMode ? (byte)0xF2 : (byte)0xF3);

        for (ushort addr = WAVE_RAM_START; addr <= WAVE_RAM_END; addr++)
        {
            _mmu.WriteByte(addr, 0x00); 
        }

        for (int i = 0; i < CHANNEL_COUNT; i++)
        {
            _channelIsActive[i] = false;
        }
        _masterSoundOn = (_mmu.ReadByte(NR52_REG) & 0x80) != 0; 
        UpdateNR52Status();
    }


    private void InitializeAudio()
    {
        if (SDL.SDL_InitSubSystem(SDL.SDL_INIT_AUDIO) < 0)
        {
            Console.WriteLine($"Failed to initialize SDL Audio: {SDL.SDL_GetError()}");
            _audioEnabled = false;
            return;
        }

        SDL.SDL_AudioSpec desiredSpec = new SDL.SDL_AudioSpec
        {
            freq = _currentSampleRate,
            format = SDL.AUDIO_S16LSB, 
            channels = 2, 
            samples = BUFFER_SIZE, 
            callback = null 
        };

        _audioDeviceId = SDL.SDL_OpenAudioDevice(null, 0, ref desiredSpec, out var obtainedSpec, (int)SDL.SDL_AUDIO_ALLOW_FREQUENCY_CHANGE);

        if (_audioDeviceId == 0)
        {
            string error = SDL.SDL_GetError();
            Console.WriteLine($"Failed to open audio device: {error}");
            _audioEnabled = false;
            return;
        }

        if (obtainedSpec.freq != _currentSampleRate)
        {
            _currentSampleRate = obtainedSpec.freq;
            UpdateCyclesPerSample(); // Recalculate based on new sample rate and current speed mode
        }
        
        _audioEnabled = true;
        SDL.SDL_PauseAudioDevice(_audioDeviceId, 0); 
    }

    private void RegisterHandlers()
    {
        _mmu.RegisterWriteHandler(NR10_REG, HandleNR10Change);
        _mmu.RegisterWriteHandler(NR11_REG, HandleNR11Change);
        _mmu.RegisterWriteHandler(NR12_REG, HandleNR12Change);
        _mmu.RegisterWriteHandler(NR13_REG, HandleNR13Change);
        _mmu.RegisterWriteHandler(NR14_REG, HandleNR14Change);

        _mmu.RegisterWriteHandler(NR21_REG, HandleNR21Change);
        _mmu.RegisterWriteHandler(NR22_REG, HandleNR22Change);
        _mmu.RegisterWriteHandler(NR23_REG, HandleNR23Change);
        _mmu.RegisterWriteHandler(NR24_REG, HandleNR24Change);

        _mmu.RegisterWriteHandler(NR30_REG, HandleNR30Change);
        _mmu.RegisterWriteHandler(NR31_REG, HandleNR31Change);
        _mmu.RegisterWriteHandler(NR32_REG, HandleNR32Change);
        _mmu.RegisterWriteHandler(NR33_REG, HandleNR33Change);
        _mmu.RegisterWriteHandler(NR34_REG, HandleNR34Change);

        _mmu.RegisterWriteHandler(NR41_REG, HandleNR41Change);
        _mmu.RegisterWriteHandler(NR42_REG, HandleNR42Change);
        _mmu.RegisterWriteHandler(NR43_REG, HandleNR43Change);
        _mmu.RegisterWriteHandler(NR44_REG, HandleNR44Change);

        _mmu.RegisterWriteHandler(NR50_REG, HandleNR50Change);
        _mmu.RegisterWriteHandler(NR51_REG, HandleNR51Change);
        _mmu.RegisterWriteHandler(NR52_REG, HandleNR52Change);

        for (ushort addr = WAVE_RAM_START; addr <= WAVE_RAM_END; addr++)
        {
            _mmu.RegisterWriteHandler(addr, HandleWaveRAMChange);
        }
    }

    private void HandleNR10Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel1.WriteNRX0(value);
    }

    private void HandleNR11Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel1.WriteNRX1(value);
    }

    private void HandleNR12Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel1.WriteNRX2(value);
    }

    private void HandleNR13Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel1.WriteNRX3(value);
    }

    private void HandleNR14Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel1.WriteNRX4(value);
    }

    private void HandleNR21Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel2.WriteNRX1(value);
    }

    private void HandleNR22Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel2.WriteNRX2(value);
    }

    private void HandleNR23Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel2.WriteNRX3(value);
    }

    private void HandleNR24Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel2.WriteNRX4(value);
    }

    private void HandleNR30Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel3.WriteNR30(value);
    }

    private void HandleNR31Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel3.WriteNRX1(value);
    }

    private void HandleNR32Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel3.WriteNR32(value);
    }

    private void HandleNR33Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel3.WriteNRX3(value);
    }

    private void HandleNR34Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel3.WriteNRX4(value);
    } 
    private void HandleWaveRAMChange(ushort address, byte value)
    {
        _channel3.UpdateWaveTable((ushort)(address - WAVE_RAM_START), value);
    }

    private void HandleNR41Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel4.WriteNRX1(value);
    }

    private void HandleNR42Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel4.WriteNRX2(value);
    }

    private void HandleNR43Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel4.WriteNR43(value);
    }

    private void HandleNR44Change(ushort address, byte value)
    {
        if (!_masterSoundOn && address != NR52_REG) return;
        _channel4.WriteNR44(value);
    }

    private void HandleNR50Change(ushort address, byte value)
    {
        _masterVolumeLeft = (value & 0x07);
        _masterVolumeRight = ((value >> 4) & 0x07);
    }

    private void HandleNR51Change(ushort address, byte value)
    {
        _panningNR51 = value;
    }

    private void HandleNR52Change(ushort address, byte value)
    {
        bool prevMasterSoundOn = _masterSoundOn;
        _masterSoundOn = (value & 0x80) != 0;

        if (prevMasterSoundOn && !_masterSoundOn) // Turning master sound OFF
        {
            // Clear most registers except NR52, length counters, and wave RAM
            for (ushort regAddr = NR10_REG; regAddr <= NR44_REG; regAddr++)
            {
                 // Skip non-existent registers or specific cases like wave ram handled by channels
                if (regAddr == 0xFF15 || regAddr == 0xFF1F || (regAddr >= 0xFF27 && regAddr <= 0xFF2F)) continue;
                if (regAddr >= WAVE_RAM_START && regAddr <= WAVE_RAM_END) continue; // Wave RAM persists

                // Only zero out registers for channels 1-4. NR50, NR51 are not zeroed.
                 bool isChannelSpecificReg = (regAddr >= NR10_REG && regAddr <= NR14_REG) ||
                                           (regAddr >= NR21_REG && regAddr <= NR24_REG) || // NR20 does not exist (0xFF15)
                                           (regAddr >= NR30_REG && regAddr <= NR34_REG) ||
                                           (regAddr >= NR41_REG && regAddr <= NR44_REG); // NR40 does not exist (0xFF1F)

                if (isChannelSpecificReg)
                {
                    _mmu.WriteDirect(regAddr, 0x00);
                }
            }
            // Reset channel internal states (except length counters, which are not cleared by APU off)
            _channel1.ResetOnApuOff();
            _channel2.ResetOnApuOff();
            _channel3.ResetOnApuOff();
            _channel4.ResetOnApuOff();

            for (int i = 0; i < CHANNEL_COUNT; i++) _channelIsActive[i] = false;
            _frameSequencerCounter = 0; // Reset frame sequencer as per Pandocs
            _frameSequencerStep = 0;
        }
        else if (!_masterSoundOn && (value & 0x80) != 0) // Turning master sound ON
        {
            _frameSequencerCounter = 0; // Reset frame sequencer as per Pandocs behavior on APU re-enable
            _frameSequencerStep = 0;    // This effectively means it starts at step 0 on next APU tick.
                                        // Some emulators might start it at 7 to immediately clock envelopes.
                                        // Pandocs: "Frame Sequencer's clock is NOT reset when sound is turned off [...]
                                        // but IS reset when it's turned back on."
        }
        // NR52 status bits 0-3 are updated by UpdateNR52Status
        UpdateNR52Status(); // This will use the new _masterSoundOn state
    }

    public void UpdateChannelStatus(int channelIndex, bool isActive)
    {
        if (channelIndex < 0 || channelIndex >= CHANNEL_COUNT) return;
        _channelIsActive[channelIndex] = isActive;
        UpdateNR52Status();
    }

    private void UpdateNR52Status()
    {
        byte currentNR52 = _mmu.ReadByte(NR52_REG); // Read without triggering handlers
        byte newStatus = (byte)(_masterSoundOn ? 0x80 : 0x00);
        if (_channelIsActive[0]) newStatus |= 0x01;
        if (_channelIsActive[1]) newStatus |= 0x02;
        if (_channelIsActive[2]) newStatus |= 0x04;
        if (_channelIsActive[3]) newStatus |= 0x08;
        newStatus |= (byte)(currentNR52 & 0x70); // Preserve read-only bits 4-6

        if ((currentNR52 & 0x8F) != (newStatus & 0x8F)) // Only write if relevant bits changed
        {
             _mmu.WriteDirect(NR52_REG, newStatus); 
        }
    }

    public void Step(int cycles)
    {
        if (!_audioEnabled) return;

        // Update cycles per sample if speed mode changed
        // This is important if IsDoubleSpeed can change mid-frame or between APU steps.
        // Assuming MMU's IsDoubleSpeed() is the source of truth.
        double effectiveCpuCyclesPerSecond = _mmu.IsDoubleSpeed() ? CPU_CYCLES_PER_SECOND_NORMAL * 2.0 : CPU_CYCLES_PER_SECOND_NORMAL;
        _currentCyclesPerSample = effectiveCpuCyclesPerSecond / _currentSampleRate;
        int currentFrameSequencerClockCycles = _mmu.IsDoubleSpeed() ? FRAME_SEQUENCER_CLOCK_CYCLES_NORMAL * 2 : FRAME_SEQUENCER_CLOCK_CYCLES_NORMAL;


        if (_masterSoundOn)
        {
            _frameSequencerCounter += cycles;
            while (_frameSequencerCounter >= currentFrameSequencerClockCycles)
            {
                _frameSequencerCounter -= currentFrameSequencerClockCycles;
                _frameSequencerStep = (_frameSequencerStep + 1) % 8;

                if (_frameSequencerStep == 0 || _frameSequencerStep == 2 || _frameSequencerStep == 4 || _frameSequencerStep == 6)
                {
                    _channel1.ClockLengthCounter();
                    _channel2.ClockLengthCounter();
                    _channel3.ClockLengthCounter();
                    _channel4.ClockLengthCounter();
                }
                if (_frameSequencerStep == 2 || _frameSequencerStep == 6) // Sweep clocked at 128Hz
                {
                     _channel1.ClockSweepUnit();
                }
                if (_frameSequencerStep == 7) // Volume envelopes clocked at 64Hz
                {
                    _channel1.ClockVolumeEnvelope();
                    _channel2.ClockVolumeEnvelope();
                    _channel4.ClockVolumeEnvelope();
                }
            }

            // Step channel frequency timers with actual CPU cycles
            _channel1.StepFrequencyTimer(cycles);
            _channel2.StepFrequencyTimer(cycles);
            _channel3.StepFrequencyTimer(cycles);
            _channel4.StepFrequencyTimer(cycles);
        }

        _totalCpuCyclesElapsed += cycles;
        long targetSamples = (long)(_totalCpuCyclesElapsed / _currentCyclesPerSample);

        while (_totalSamplesGenerated < targetSamples)
        {
            if (_masterSoundOn)
            {
                GenerateSample(); // Generates one stereo sample pair
            }
            else
            {
                // Fill buffer with silence if APU is off
                if (_bufferPosition < _audioBuffer.Length -1) {
                    _audioBuffer[_bufferPosition++] = 0;
                    _audioBuffer[_bufferPosition++] = 0;
                }
            }
            _totalSamplesGenerated++;

            if (_bufferPosition >= _audioBuffer.Length)
            {
                QueueAudio();
                _bufferPosition = 0;
            }
        }
        // If buffer has content and needs flushing due to APU turning off, or end of processing.
        // This needs careful consideration: if APU turns off, remaining samples should be played.
        // The current logic fills the _audioBuffer directly. If _totalSamplesGenerated catches up
        // but buffer is not full, it will be sent on next fill.
    }


    private void GenerateSample() // Called when _masterSoundOn is true
    {
        float mixedLeft = 0;
        float mixedRight = 0;

        if (_channelIsActive[0])
        {
            float ch1Sample = _channel1.GetSample();
            if ((_panningNR51 & 0x10) != 0) mixedLeft += ch1Sample; // SO2 output ch1 to left
            if ((_panningNR51 & 0x01) != 0) mixedRight += ch1Sample; // SO1 output ch1 to right
        }
        if (_channelIsActive[1])
        {
            float ch2Sample = _channel2.GetSample();
            if ((_panningNR51 & 0x20) != 0) mixedLeft += ch2Sample; // SO2 output ch2 to left
            if ((_panningNR51 & 0x02) != 0) mixedRight += ch2Sample; // SO1 output ch2 to right
        }
        if (_channelIsActive[2])
        {
            float ch3Sample = _channel3.GetSample();
            if ((_panningNR51 & 0x40) != 0) mixedLeft += ch3Sample; // SO2 output ch3 to left
            if ((_panningNR51 & 0x04) != 0) mixedRight += ch3Sample; // SO1 output ch3 to right
        }
        if (_channelIsActive[3])
        {
            float ch4Sample = _channel4.GetSample();
            if ((_panningNR51 & 0x80) != 0) mixedLeft += ch4Sample; // SO2 output ch4 to left
            if ((_panningNR51 & 0x08) != 0) mixedRight += ch4Sample; // SO1 output ch4 to right
        }

        // Apply master volume. Game Boy volumes are 0-7.
        // Scale samples from approx -1.0 to 1.0 to 16-bit range.
        // Max output per channel is ~1.0. Max mixed is ~4.0.
        // Scaled by master vol (0-7) then to short range.
        // Amplitude scale should be chosen carefully. If 4 channels max out, mixed could be 4.0.
        // (MasterVol/7.0f) scales this. Max output is then 4.0 * (7/7.0) = 4.0.
        // Need to scale this 4.0 down to a suitable level for short.MaxValue (32767).
        // So, 32767 / 4.0 = ~8191. Let's use a lower value to prevent clipping
        const float AMPLITUDE_SCALE = 4000.0f; 

        // Normalize master volume from 0-7 to 0.0-1.0
        float volLeftNorm = (_masterVolumeLeft) / 7.0f;
        float volRightNorm = (_masterVolumeRight) / 7.0f;

        short finalLeft = (short)(Math.Clamp(mixedLeft * volLeftNorm * AMPLITUDE_SCALE, short.MinValue, short.MaxValue));
        short finalRight = (short)(Math.Clamp(mixedRight * volRightNorm * AMPLITUDE_SCALE, short.MinValue, short.MaxValue));

        if (_bufferPosition < _audioBuffer.Length -1 ) {
            _audioBuffer[_bufferPosition++] = finalLeft;
            _audioBuffer[_bufferPosition++] = finalRight;
        }
    }

    private void QueueAudio()
    {
        if (!_audioEnabled || _bufferPosition == 0) return; // Don't queue if nothing to queue
        unsafe
        {
            fixed (short* bufferPtr = _audioBuffer)
            {
                // Queue only the samples that have been filled
                SDL.SDL_QueueAudio(_audioDeviceId, (IntPtr)bufferPtr, (uint)(_bufferPosition * sizeof(short)));
            }
        }
        _bufferPosition = 0; // Reset buffer position after queueing
    }

    public void Dispose()
    {
        if (_audioEnabled)
        {
            SDL.SDL_PauseAudioDevice(_audioDeviceId, 1);
            SDL.SDL_CloseAudioDevice(_audioDeviceId);
            _audioEnabled = false;
            SDL.SDL_QuitSubSystem(SDL.SDL_INIT_AUDIO);
        }
    }

    private abstract class SoundChannel
    {
        protected APU _apu;
        protected int _channelIndex;
        public bool DacEnabled { get; protected set; }

        protected int _initialVolume;
        protected int _currentVolume;
        protected bool _envelopeIncrease;
        protected int _envelopePeriod; // Ticks of 64Hz clock
        protected int _envelopeCounter; // Down-counter for envelope period

        protected int _lengthData;    // Initial value for length counter (NRx1 lower 6 bits for ch1/2/4, NRx1 all 8 for ch3)
        protected int _lengthCounter; // Down-counter for length
        protected bool _lengthEnabled; // Bit 6 of NRx4

        protected int _frequencyValue; // 11-bit frequency value from NRx3 and NRx4
        protected int _frequencyTimer; // Down-counter for channel's specific frequency period

        public SoundChannel(APU apu, int channelIndex)
        {
            _apu = apu;
            _channelIndex = channelIndex;
        }

        public virtual void Reset()
        {
            DacEnabled = false;
            _initialVolume = 0;
            _currentVolume = 0;
            _envelopeIncrease = false;
            _envelopePeriod = 0;
            _envelopeCounter = 0;
            _lengthData = 0;
            _lengthCounter = 0;
            _lengthEnabled = false;
            _frequencyValue = 0;
            _frequencyTimer = 0;
            _apu.UpdateChannelStatus(_channelIndex, false);
        }
        
        // Called when APU master switch is turned off
        public virtual void ResetOnApuOff()
        {
            // Most state is cleared, but length counters are not.
            // Frequency, volume, envelope, duty, sweep are effectively reset.
            DacEnabled = false; // DAC is off
            _initialVolume = 0;
            _currentVolume = 0;
            _envelopeIncrease = false;
            _envelopePeriod = 0;
            _envelopeCounter = 0;
            // _lengthData and _lengthCounter persist
            // _lengthEnabled persists
            _frequencyValue = 0; // Or at least its effect is gone with DAC off.
            _frequencyTimer = 0; // Becomes irrelevant with DAC off.
            // Wave RAM for CH3 is not cleared by APU off.
            _apu.UpdateChannelStatus(_channelIndex, false);
        }


        protected virtual void Trigger()
        {
            // Common trigger logic: enable channel in NR52, reload length, envelope, freq timer
            // DacEnabled is checked by individual channels based on their NRx2
            _apu.UpdateChannelStatus(_channelIndex, DacEnabled); // Update based on current DAC status
            _currentVolume = _initialVolume;
            _envelopeCounter = _envelopePeriod; // Reload envelope timer

            if (_lengthCounter == 0 && _lengthEnabled) // Only reload if it hit zero AND length is enabled
            {
                _lengthCounter = GetMaxLegth();
                if (_lengthCounter == 0 && _channelIndex == 2) _lengthCounter = 256; // CH3 max length is 256
                else if (_lengthCounter == 0) _lengthCounter = 64; // CH1/2/4 max length is 64
            }
            // If length counter was not zero but a trigger happens, it continues counting down.
            // If length becomes enabled by this trigger, and counter is 0, it's reloaded.
        }

        public virtual void ClockLengthCounter() // Called by Frame Sequencer (256Hz)
        {
            if (_lengthEnabled && _lengthCounter > 0)
            {
                _lengthCounter--;
                if (_lengthCounter == 0)
                {
                    DacEnabled = false; // Channel turns off
                    _apu.UpdateChannelStatus(_channelIndex, false);
                }
            }
        }

        public virtual void ClockVolumeEnvelope() // Called by Frame Sequencer (64Hz)
        {
            if (!DacEnabled || _envelopePeriod == 0) return; // No envelope update if period is 0
            
            _envelopeCounter--;
            if (_envelopeCounter <= 0)
            {
                _envelopeCounter = _envelopePeriod; // Reload period
                if (_envelopeIncrease)
                {
                    if (_currentVolume < 15) _currentVolume++;
                }
                else
                {
                    if (_currentVolume > 0) _currentVolume--;
                }
            }
        }

        // 'cycles' are raw CPU T-cycles
        public abstract void StepFrequencyTimer(int cycles); 
        public abstract float GetSample();
        protected abstract int GetMaxLegth(); // Max value for length counter (64 for ch1/2/4, 256 for ch3)

        // NRx1: Wave Duty (Pulse) / Sound Length (All)
        public virtual void WriteNRX1(byte value) // Bits 0-5 for Ch1/2/4 length, 0-7 for Ch3 length
        {
            // Length data is specific to channel type for max length calculation
            // Max length for Pulse/Noise is 64, for Wave is 256.
            // NRx1 sets the initial length counter value L = MaxLength - write_value
            _lengthData = value & (GetMaxLegth() == 256 ? 0xFF : 0x3F); // Mask accordingly
            _lengthCounter = GetMaxLegth() - _lengthData;
        }

        // NRx2: Volume Envelope
        public virtual void WriteNRX2(byte value)
        {
            _initialVolume = (value >> 4) & 0x0F;
            _envelopeIncrease = (value & 0x08) != 0;
            _envelopePeriod = value & 0x07; // Envelope step time (n * 1/64 s)
            
            // DAC Power: Channel's DAC is only powered if bits 3-7 (initial vol or envelope dir) are non-zero.
            DacEnabled = (value & 0xF8) != 0; 
            if (!DacEnabled) _apu.UpdateChannelStatus(_channelIndex, false);
            // If DAC is turned off via NRx2, volume is forced to 0.
            // Current volume is not immediately zeroed here, but GetSample should return 0 if !DacEnabled.
        }

        // NRx3: Frequency LSB
        public virtual void WriteNRX3(byte value)
        {
            _frequencyValue = (_frequencyValue & 0x0700) | value;
        }

        // NRx4: Frequency MSB / Control (Trigger, Length Enable)
        public virtual void WriteNRX4(byte value)
        {
            _frequencyValue = (_frequencyValue & 0x00FF) | ((value & 0x07) << 8);
            bool oldLengthEnabled = _lengthEnabled;
            _lengthEnabled = (value & 0x40) != 0;

            // "Extra length clocking" behavior if length enable changes from 0 to 1 on certain frame sequencer steps
            // This is complex, for now, simple enable/disable.
            // If length is disabled then enabled again while counter is 0, it does not reload on its own.
            // Trigger reloads it.
            // todo: Follow up on extra length clocking behavior

            if ((value & 0x80) != 0) // Trigger bit
            {
                Trigger();
            }
        }
    }

    private class PulseChannel : SoundChannel
    {
        private readonly bool _hasSweep;
        // Sweep Unit (Channel 1 only)
        private int _sweepPeriod;    // Sweep step time (n * 1/128 s)
        private bool _sweepDecrease; // false=Addition, true=Subtraction
        private int _sweepShift;     // Number of frequency shifts
        private int _sweepTimer;     // Down-counter for sweep period
        private int _shadowFrequency;
        private bool _sweepEnabledThisTrigger; // Tracks if sweep was active for the current trigger
        private bool _sweepCalculationOverflow; // If sweep calc went > 2047

        private int _waveDuty; // Bits 6-7 of NRx1
        private static readonly byte[] DUTY_PATTERNS = new byte[]
        {
            0b00000001, // 12.5%
            0b10000001, // 25%
            0b10000111, // 50%
            0b01111110  // 75%
        };

        private int _wavePosition; // 0-7, steps through the duty pattern

        public PulseChannel(bool hasSweep, APU apu, int channelIndex) : base(apu, channelIndex)
        {
            _hasSweep = hasSweep;
        }

        public override void Reset()
        {
            base.Reset();
            _waveDuty = 0;
            _wavePosition = 0;
            if (_hasSweep)
            {
                _sweepPeriod = 0;
                _sweepDecrease = false;
                _sweepShift = 0;
                _sweepTimer = 0;
                _shadowFrequency = 0;
                _sweepEnabledThisTrigger = false;
                _sweepCalculationOverflow = false;
            }
        }
        
        public override void ResetOnApuOff()
        {
            base.ResetOnApuOff();
            _waveDuty = 0; // Duty is part of NRx1, which is cleared.
            _wavePosition = 0;
             if (_hasSweep)
            {
                _sweepPeriod = 0; // NRx0 is cleared
                _sweepDecrease = false;
                _sweepShift = 0;
                _sweepTimer = 0;
                _shadowFrequency = 0;
                _sweepEnabledThisTrigger = false;
                 _sweepCalculationOverflow = false;
            }
        }


        protected override int GetMaxLegth() => 64;

        //NR10 / NRx0 for Channel 1 (Sweep)
        public void WriteNRX0(byte value) 
        {
            if (!_hasSweep) return;
            _sweepPeriod = (value >> 4) & 0x07;
            _sweepDecrease = (value & 0x08) != 0;
            _sweepShift = value & 0x07;

            // Writing to NR10 can disable channel if sweep calculation after write results in overflow with negate mode
            if (_sweepDecrease && _sweepCalculationOverflow) { // If it was previously calculated to overflow
                 // And now negate mode is active again, this can disable the channel.
                 // This specific interaction is complex, often related to "zombie mode" or "sweep bug".
                 // For now, assume this doesn't immediately disable; disabling happens on sweep clock.
            }
        }

        public override void WriteNRX1(byte value) // Duty + Length
        {
            _waveDuty = (value >> 6) & 0x03;
            base.WriteNRX1(value); // Handles length
        }
        
        public override void WriteNRX2(byte value) // Envelope
        {
            base.WriteNRX2(value);
            // If DAC is turned off by writing to NRx2 (initial vol=0 and env dir=decrease),
            // channel becomes inactive. This is handled in base.WriteNRX2.
            if (!DacEnabled) _apu.UpdateChannelStatus(_channelIndex, false);
        }


        protected override void Trigger()
        {
            if (!DacEnabled && _initialVolume == 0 && !_envelopeIncrease) // Check DAC power from NRx2
            {
                 _apu.UpdateChannelStatus(_channelIndex, false);
                 return;
            }
            DacEnabled = true; // Explicitly enable if not already (e.g. NRx2 write made it true)
            
            _frequencyTimer = (2048 - _frequencyValue) * 4; // Reload frequency timer
            _wavePosition = 0; // Reset wave position

            if (_hasSweep)
            {
                _shadowFrequency = _frequencyValue;
                _sweepTimer = _sweepPeriod == 0 ? 8 : _sweepPeriod; // If period 0, sweep is off (effectively timer never reaches 0 unless period is set later)
                                                                    // Official docs say period 0 means 8.
                _sweepEnabledThisTrigger = (_sweepPeriod > 0 || _sweepShift > 0);
                _sweepCalculationOverflow = false;

                if (_sweepShift > 0) // If shift is non-zero, initial calculation happens
                {
                    CalculateNewSweepFrequency(true); // isTrigger = true
                }
            }
            base.Trigger(); // Handles length, envelope, and NR52 status
        }

        // Called by Frame Sequencer (128Hz)
        public void ClockSweepUnit() 
        {
            if (!_hasSweep || !_sweepEnabledThisTrigger || _sweepPeriod == 0) return;
            
            _sweepTimer--;
            if (_sweepTimer <= 0)
            {
                _sweepTimer = _sweepPeriod == 0 ? 8 : _sweepPeriod; // Reload sweep timer
                CalculateNewSweepFrequency(false); // isTrigger = false
            }
        }

        private void CalculateNewSweepFrequency(bool isTriggerContext)
        {
            if (!_sweepEnabledThisTrigger && !isTriggerContext) return; // Sweep might have been disabled mid-trigger
            if (_sweepCalculationOverflow) return; // Already overflowed, channel is off

            int shiftedFreq = _shadowFrequency >> _sweepShift;
            int newFreq;
            if (_sweepDecrease) 
            {
                newFreq = _shadowFrequency - shiftedFreq;
            }
            else 
            {
                newFreq = _shadowFrequency + shiftedFreq;
            }

            if (newFreq > 2047)
            {
                DacEnabled = false; // Channel disabled by sweep overflow
                _apu.UpdateChannelStatus(_channelIndex, false);
                _sweepCalculationOverflow = true; // Mark overflow
                _sweepEnabledThisTrigger = false; // Stop further sweep calcs for this trigger
            }
            // else if (newFreq < 0) // Should not happen with typical positive frequencies
            // {
                 // Treat as disabled or clamp to 0, though hardware behavior might be more nuanced.
                 // For simplicity, let's assume it doesn't go < 0 if shadowFrequency starts positive.
                 // If it does, it would effectively stop the sound.
            // }
            else if (_sweepShift > 0) // Only update if shift is > 0 (Pandocs)
            {
                // Update actual channel frequency and shadow register
                _frequencyValue = newFreq;
                _shadowFrequency = newFreq;
                // Reload frequency timer with new value
                _frequencyTimer = (2048 - _frequencyValue) * 4;

                // Second overflow check with the new _frequencyValue (if not already overflowed by newFreq > 2047)
                // This is for the case where newFreq itself isn't >2047, but applying it to _frequencyValue (if different) would cause issues.
                // However, _frequencyValue is directly set to newFreq here.
                // One more check if _shadowFrequency (which IS newFreq) >> _sweepShift again would cause problems (for next step)
                // but the immediate check is newFreq > 2047.
            }
        }


        public override void StepFrequencyTimer(int cycles) // cycles are CPU T-cycles
        {
            if (!DacEnabled) return;
            _frequencyTimer -= cycles;
            while (_frequencyTimer <= 0)
            {
                _wavePosition = (_wavePosition + 1) % 8;
                int period = (2048 - _frequencyValue) * 4; // Period in T-cycles for pulse channels
                if (period == 0) period = 8192 * 4; // Effectively silent or max period if freq is 2048
                _frequencyTimer += period;
            }
        }

        public override float GetSample()
        {
            if (!DacEnabled || _currentVolume == 0) return 0;
            // Duty pattern gives 1 for high, 0 for low part of wave
            bool outputHigh = ((DUTY_PATTERNS[_waveDuty] >> (7 - _wavePosition)) & 1) != 0;
            // Game Boy sound output is often signed around a midpoint.
            // A common way is (outputHigh ? Volume : -Volume) then normalized.
            return outputHigh ? _currentVolume / 15.0f : -_currentVolume / 15.0f;
        }
    }

    private class WaveChannel : SoundChannel
    {
        private byte[] _waveTable = new byte[16]; // 32 4-bit samples
        private int _waveRamReadPosition; // 0-31, current 4-bit sample index
        private int _outputLevelShift; //0: mute, 1: 100%, 2: 50% (shift right 1), 3: 25% (shift right 2)

        public WaveChannel(APU apu, int channelIndex) : base(apu, channelIndex)
        {
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            Array.Clear(_waveTable, 0, _waveTable.Length);
            _waveRamReadPosition = 0;
            _outputLevelShift = 0; // Muted by default
            DacEnabled = false; // NR30 bit 7 controls DAC
        }
        
        public override void ResetOnApuOff()
        {
            base.ResetOnApuOff();
            // Wave RAM (_waveTable) is NOT cleared when APU is turned off.
            _waveRamReadPosition = 0;
            // _outputLevelShift (NR32) is not cleared.
            // DacEnabled (NR30) is cleared.
        }


        protected override int GetMaxLegth() => 256;

        // NR30: DAC Enable
        public void WriteNR30(byte value) 
        {
            DacEnabled = (value & 0x80) != 0;
            if (!DacEnabled) _apu.UpdateChannelStatus(_channelIndex, false);
        }

        // NR31: Length Data
        public override void WriteNRX1(byte value) // Overrides base for full 8-bit length
        {
            _lengthData = value; // Uses all 8 bits
            _lengthCounter = GetMaxLegth() - _lengthData;
        }
        
        // NR32: Output Level
        public void WriteNR32(byte value) 
        {
            int volCode = (value >> 5) & 0x03;
            switch (volCode)
            {
                case 0: _outputLevelShift = -1; break; // Mute (special value for GetSample)
                case 1: _outputLevelShift = 0; break;  // 100%
                case 2: _outputLevelShift = 1; break;  // 50% (>>1)
                case 3: _outputLevelShift = 2; break;  // 25% (>>2)
            }
        }
        
        // NRx3 and NRx4 are handled by base for frequency.

        protected override void Trigger()
        {
            if (!DacEnabled) // Check NR30 DAC power
            {
                _apu.UpdateChannelStatus(_channelIndex, false);
                return;
            }
            // Unlike pulse/noise, Wave channel has no NRx2 to check for DAC power. NR30 is king.
            
            _apu.UpdateChannelStatus(_channelIndex, true);
            _waveRamReadPosition = 0; // Reset sample position
            _frequencyTimer = (2048 - _frequencyValue) * 2; // Reload frequency timer (Wave period is (2048-freq)*2 T-cycles)
            
            // Length counter logic for Wave (from base.Trigger() but adapted)
            if (_lengthCounter == 0 && _lengthEnabled)
            {
                _lengthCounter = GetMaxLegth(); // Max length 256
            }
            // Wave channel does not have volume envelope, so no envelope reset here.
            // base.Trigger() not called to avoid its envelope logic.
        }

        public void UpdateWaveTable(ushort indexInWaveRamByte, byte value) // index is 0-15
        {
            if (indexInWaveRamByte < _waveTable.Length) _waveTable[indexInWaveRamByte] = value;
        }

        public override void StepFrequencyTimer(int cycles) // cycles are CPU T-cycles
        {
            if (!DacEnabled) return;
            _frequencyTimer -= cycles;
            while (_frequencyTimer <= 0)
            {
                _waveRamReadPosition = (_waveRamReadPosition + 1) % 32; // 32 4-bit samples
                int period = (2048 - _frequencyValue) * 2; // Period in T-cycles for wave channel
                if (period == 0) period = 8192 * 2; // Max period if freq is 2048
                _frequencyTimer += period;
            }
        }

        public override float GetSample()
        {
            if (!DacEnabled || _outputLevelShift == -1) return 0; // Muted if DAC off or level is 0
            
            byte twoSamples = _waveTable[_waveRamReadPosition / 2]; // Get byte containing two 4-bit samples
            int sample4bit = ((_waveRamReadPosition % 2) == 0) ? (twoSamples >> 4) & 0x0F : twoSamples & 0x0F;
            
            if (_outputLevelShift > 0) sample4bit >>= _outputLevelShift;
            
            // Convert 4-bit (0-15) to a float range (e.g. -1.0 to 1.0)
            // Pandocs says samples are "as is", so 0-15.
            // Output is often (sample - 7.5) / 7.5 to map to ~[-1, 1]
            // Or, if it's direct DAC values, it's simpler: (sample / 15.0f) if mapping to [0,1]
            // For mixing, a signed output is typical.
            return (sample4bit - 7.5f) / 7.5f; // Maps 0..15 to -1.0 .. 1.0 (approx)
        }
    }

    private class NoiseChannel : SoundChannel
    {
        private int _lfsr; // Linear Feedback Shift Register (15-bit)
        private bool _lfsrWidthMode; // false=15-bit, true=7-bit (NR43 bit 3)
        private int _clockShift;    // NR43 bits 4-7
        private int _divisorCode;   // NR43 bits 0-2
        // Divisor r: 0->8, 1->16, ..., 7->112
        private static readonly int[] DIVISOR_LOOKUP = { 8, 16, 32, 48, 64, 80, 96, 112 };

        public NoiseChannel(APU apu, int channelIndex) : base(apu, channelIndex)
        {
             Reset();
        }

        public override void Reset()
        {
            base.Reset();
            _lfsr = 0x7FFF; // Initial LFSR value (all 1s)
            _lfsrWidthMode = false;
            _clockShift = 0;
            _divisorCode = 0;
        }
        
        public override void ResetOnApuOff()
        {
            base.ResetOnApuOff();
            // LFSR state is not specified to be cleared by APU off, but its clocking stops.
            // NRx2 (envelope), NRx3 (poly counter), NRx4 (trigger/length enable) are cleared or reset effect.
            _lfsr = 0x7FFF; // Let's reset LFSR for consistency on APU off, though it might persist.
            _lfsrWidthMode = false; // from NR43
            _clockShift = 0;    // from NR43
            _divisorCode = 0;   // from NR43
        }


        protected override int GetMaxLegth() => 64;

        //NR43: Polynomial Counter (Clock shift, LFSR width, Divisor code)
        public void WriteNR43(byte value) 
        {
            _clockShift = (value >> 4) & 0x0F;
            _lfsrWidthMode = (value & 0x08) != 0;
            _divisorCode = value & 0x07;
        }

        //NR44: Control (Trigger, Length Enable) - NRx3 for freq LSB is not used by noise
        // Base WriteNRX4 handles Freq MSB (not used here) and Length Enable + Trigger
        // So we only need to implement the trigger part specific to Noise.
        public void WriteNR44(byte value) // This is NRx4
        {
            base.WriteNRX4(value); // Handles length enable and calls Trigger if bit 7 is set
        }
        
        public override void WriteNRX3(byte value) { /* Noise channel does not use NRx3 */ }


        protected override void Trigger()
        {
            if (!DacEnabled && _initialVolume == 0 && !_envelopeIncrease) // Check DAC power from NRx2
            {
                 _apu.UpdateChannelStatus(_channelIndex, false);
                 return;
            }
            DacEnabled = true;

            _lfsr = 0x7FFF; // Reset LFSR on trigger
            
            // Calculate initial frequency timer value based on NR43
            int divisor = DIVISOR_LOOKUP[_divisorCode];
            // Clock shift s should be < 14. If s >= 14, timer effectively never clocks (or very rarely).
            int actualShift = Math.Min(_clockShift, 13); // Clamp shift to prevent excessive periods
            _frequencyTimer = divisor << actualShift; // Period in T-cycles for noise channel
            
            base.Trigger(); // Handles length, envelope, and NR52 status
        }

        public override void StepFrequencyTimer(int cycles) // cycles are CPU T-cycles
        {
            if (!DacEnabled) return;
            _frequencyTimer -= cycles;
            while (_frequencyTimer <= 0)
            {
                // Clock the LFSR
                int bit0 = _lfsr & 1;
                int bit1 = (_lfsr >> 1) & 1;
                int xorResult = bit0 ^ bit1;
                
                _lfsr >>= 1; // Shift LFSR
                _lfsr |= (xorResult << 14); // Put XOR result into bit 14 (15-bit LFSR)
                
                if (_lfsrWidthMode) // If 7-bit mode
                {
                    // Bit 6 is also set to XOR result
                    _lfsr &= ~(1 << 6); // Clear bit 6
                    _lfsr |= (xorResult << 6); // Set bit 6 to XOR result
                }
                
                // Reload frequency timer
                int divisor = DIVISOR_LOOKUP[_divisorCode];
                int actualShift = Math.Min(_clockShift, 13);
                int period = divisor << actualShift;
                if (period == 0) period = 8; // Minimum divisor is 8 if code=0, shift=0
                _frequencyTimer += period;
            }
        }

        public override float GetSample()
        {
            if (!DacEnabled || _currentVolume == 0) return 0;
            // Output is based on bit 0 of LFSR. If 0, output is 1 * vol. If 1, output is 0 * vol (or -1 * vol for signed).
            // Most docs say "output is low bit of LFSR inverted". So if lfsr_bit0=0, output=1. If lfsr_bit0=1, output=0.
            bool outputHigh = (_lfsr & 1) == 0; // Output is 1 if bit 0 is 0.
            return outputHigh ? (float)_currentVolume / 15.0f : -(float)_currentVolume / 15.0f;
        }
    }
}

