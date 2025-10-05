using SDL2;

using System.Runtime.InteropServices;
using DotnetGBC.Graphics;

namespace DotnetGBC.SDL2.Rendering;

public class GBCSDLRenderer : IDisposable
{
    private readonly PPU _ppu;
    private readonly IntPtr _renderer;
    private IntPtr _texture;
    private bool _disposed;
    private bool _debugMode = false;
    private bool _debugOutput = false;
    private int _frameCount = 0;
    private bool _overlayMode = false; // Enable grid overlay on PPU output

    public GBCSDLRenderer(PPU ppu, IntPtr renderer)
    {
        _ppu = ppu ?? throw new ArgumentNullException(nameof(ppu));
        _renderer = renderer;

        Console.WriteLine($"[GBCSDLRenderer] Creating texture with dimensions: {_ppu.Width}x{_ppu.Height}");
        Console.WriteLine($"[GBCSDLRenderer] PPU Buffer Size: {_ppu.BufferSize} bytes");
        Console.WriteLine($"[GBCSDLRenderer] PPU Buffer Pointer: {_ppu.BufferPointer}");

        // Create the SDL texture with the correct format
        _texture = SDL.SDL_CreateTexture(
            _renderer,
            SDL.SDL_PIXELFORMAT_ABGR8888, // ABGR8888 is correct here, don't worry about it.
            (int)SDL.SDL_TextureAccess.SDL_TEXTUREACCESS_STREAMING,
            _ppu.Width,
            _ppu.Height);

        if (_texture == IntPtr.Zero)
        {
            string error = SDL.SDL_GetError();
            throw new Exception($"Failed to create SDL texture: {error}");
        }
    }

    public void Render()
    {
        if (_disposed) return;

        // Lock the texture to get direct access to the memory
        IntPtr pixelsPtr;
        int pitch;
        int result = SDL.SDL_LockTexture(_texture, IntPtr.Zero, out pixelsPtr, out pitch);
        
        if (result < 0)
        {
            string error = SDL.SDL_GetError();
            Console.WriteLine($"Failed to lock texture: {error}");
            return;
        }

        _frameCount++;

        if (_debugMode)
        {
            // Generate a test pattern to verify colors
            GenerateTestPattern(pixelsPtr, pitch, _ppu.Width, _ppu.Height);
            
            // Disable debug mode after 180 frames (about 3 seconds)
            if (_frameCount >= 180)
            {
                Console.WriteLine("Switching from test pattern to PPU output");
                _debugMode = false;
                
                // Analyze PPU buffer before switching
                AnalyzePPUBuffer();
            }
        }
        else
        {
            // Copy the PPU frame buffer to the texture
            unsafe
            {
                if (_ppu.BufferPointer == IntPtr.Zero)
                {
                    Console.WriteLine("ERROR: PPU Buffer Pointer is null!");
                    FillSolidColor(pixelsPtr, _ppu.Width, _ppu.Height, 255, 0, 0, 255); // Red screen for error
                }
                else
                {
                    byte* srcPtr = (byte*)_ppu.BufferPointer.ToPointer();
                    byte* dstPtr = (byte*)pixelsPtr.ToPointer();
                    
                    // Copy the buffer byte by byte
                    for (int i = 0; i < _ppu.BufferSize; i++)
                    {
                        dstPtr[i] = srcPtr[i];
                    }
                    
                    // If overlay mode is enabled, add a grid pattern on top of the PPU output
                    if (_overlayMode)
                    {
                        AddGridOverlay(dstPtr, _ppu.Width, _ppu.Height);
                    }
                    
                    // Every 60 frames, sample and log some pixels
                    if (_debugOutput && _frameCount % 60 == 0)
                    {
                        SamplePPUPixels(srcPtr);
                    }
                }
            }
        }

        // Unlock the texture
        SDL.SDL_UnlockTexture(_texture);

        // Clear the renderer
        SDL.SDL_RenderClear(_renderer);

        // Render the texture to the full window
        SDL.SDL_RenderCopy(_renderer, _texture, IntPtr.Zero, IntPtr.Zero);

        // Present the renderer (show the frame)
        SDL.SDL_RenderPresent(_renderer);
    }

    private unsafe void SamplePPUPixels(byte* buffer)
    {
        Console.WriteLine("Sampling PPU pixels:");
        
        // Sample pixels from different screen positions
        int[] samplePositions = { 0, 16, 32, 64, 128, _ppu.Width * _ppu.Height - 1 };
        
        foreach (int pos in samplePositions)
        {
            int pixelOffset = pos * 4;
            if (pixelOffset < _ppu.BufferSize)
            {
                Console.WriteLine($"Pixel at {pos}: R={buffer[pixelOffset]}, G={buffer[pixelOffset+1]}, B={buffer[pixelOffset+2]}, A={buffer[pixelOffset+3]}");
            }
        }
    }

    private void AnalyzePPUBuffer()
    {
        Console.WriteLine("Analyzing PPU Buffer before switching:");
        
        if (_ppu.BufferPointer == IntPtr.Zero)
        {
            Console.WriteLine("ERROR: PPU Buffer Pointer is null!");
            return;
        }
        
        // Check if buffer is all zeros (black)
        bool allZero = true;
        bool allSame = true;
        byte firstByte = 0;
        int sampleSize = Math.Min(_ppu.BufferSize, 1000); // Sample up to 1000 bytes
        
        byte[] sample = new byte[sampleSize];
        Marshal.Copy(_ppu.BufferPointer, sample, 0, sampleSize);
        
        if (sample.Length > 0)
        {
            firstByte = sample[0];
            
            for (int i = 0; i < sample.Length; i++)
            {
                if (sample[i] != 0)
                {
                    allZero = false;
                }
                
                if (sample[i] != firstByte)
                {
                    allSame = false;
                }
                
                if (!allZero && !allSame)
                {
                    break;
                }
            }
        }
        
        if (allZero)
        {
            Console.WriteLine("WARNING: PPU Buffer appears to be all zeros (black screen)");
        }
        else if (allSame)
        {
            Console.WriteLine($"WARNING: PPU Buffer appears to contain all the same byte value: {firstByte}");
        }
        else
        {
            Console.WriteLine("PPU Buffer contains varying data");
        }
        
        // Sample some pixels
        if (sample.Length >= 16)
        {
            Console.WriteLine("First 4 pixels (RGBA): ");
            for (int i = 0; i < 4; i++)
            {
                if (i * 4 + 3 < sample.Length)
                {
                    Console.WriteLine($"Pixel {i}: R={sample[i*4]}, G={sample[i*4+1]}, B={sample[i*4+2]}, A={sample[i*4+3]}");
                }
            }
        }
    }

    private unsafe void AddGridOverlay(byte* buffer, int width, int height)
    {
        // Add a grid overlay to help see if PPU data is showing through
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Draw a grid line every 16 pixels
                if (x % 16 == 0 || y % 16 == 0)
                {
                    int index = (y * width + x) * 4;
                    
                    // Make the pixel brighter (add some white)
                    buffer[index] = (byte)Math.Min(255, buffer[index] + 64);     // R
                    buffer[index + 1] = (byte)Math.Min(255, buffer[index + 1] + 64); // G
                    buffer[index + 2] = (byte)Math.Min(255, buffer[index + 2] + 64); // B
                    // Don't modify alpha
                }
            }
        }
    }

    private void GenerateTestPattern(IntPtr pixelsPtr, int pitch, int width, int height)
    {
        // Create a test pattern with different colors
        byte[] buffer = new byte[width * height * 4];
        
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                
                int patternType = ((x / 16) + (y / 16)) % 4;
                
                switch (patternType)
                {
                    case 0: // Red
                        buffer[index] = 255;   // R
                        buffer[index + 1] = 0; // G
                        buffer[index + 2] = 0; // B
                        buffer[index + 3] = 255; // A
                        break;
                    case 1: // Green
                        buffer[index] = 0;     // R
                        buffer[index + 1] = 255; // G
                        buffer[index + 2] = 0; // B
                        buffer[index + 3] = 255; // A
                        break;
                    case 2: // Blue
                        buffer[index] = 0;     // R
                        buffer[index + 1] = 0; // G
                        buffer[index + 2] = 255; // B
                        buffer[index + 3] = 255; // A
                        break;
                    case 3: // White
                        buffer[index] = 255;   // R
                        buffer[index + 1] = 255; // G
                        buffer[index + 2] = 255; // B
                        buffer[index + 3] = 255; // A
                        break;
                }
            }
        }
        
        // Copy the buffer to the texture
        Marshal.Copy(buffer, 0, pixelsPtr, buffer.Length);
    }

    private void FillSolidColor(IntPtr pixelsPtr, int width, int height, byte r, byte g, byte b, byte a)
    {
        byte[] buffer = new byte[width * height * 4];
        
        for (int i = 0; i < width * height; i++)
        {
            int index = i * 4;
            buffer[index] = r;
            buffer[index + 1] = g;
            buffer[index + 2] = b;
            buffer[index + 3] = a;
        }
        
        Marshal.Copy(buffer, 0, pixelsPtr, buffer.Length);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            if (_texture != IntPtr.Zero)
            {
                SDL.SDL_DestroyTexture(_texture);
                _texture = IntPtr.Zero;
            }
            _disposed = true;
        }
    }
}

