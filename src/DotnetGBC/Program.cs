using DotnetGBC.SDL2.Input;
using DotnetGBC.Threading;
using SDL2;

namespace DotnetGBC;

static class Program
{
    const int SCREEN_HEIGHT = 144;
    const int SCREEN_WIDTH = 160;
    const int SCALE_FACTOR = 5;

    private static IntPtr _sdlWindow;
    private static IntPtr _sdlRenderer;
    private static EmulationThread? _emulationThread;

    private static bool _debug = false;
    private static string _romFilenameOverwrite = "";

    private static bool _running = false;
    
    static void Main(string[] args)
    {
        if (SDL.SDL_Init(SDL.SDL_INIT_VIDEO | SDL.SDL_INIT_AUDIO | SDL.SDL_INIT_GAMECONTROLLER) != 0)
        {
            string error = SDL.SDL_GetError();
            Console.WriteLine($"Failed to initialize SDL: {error}");
            return;
        }

        _sdlWindow = SDL.SDL_CreateWindow(
            "dotnetGBC", 
            SDL.SDL_WINDOWPOS_CENTERED, 
            SDL.SDL_WINDOWPOS_CENTERED, 
            SCREEN_WIDTH * SCALE_FACTOR, 
            SCREEN_HEIGHT * SCALE_FACTOR,
            SDL.SDL_WindowFlags.SDL_WINDOW_SHOWN);

        if (_sdlWindow == IntPtr.Zero)
        {
            string error = SDL.SDL_GetError();
            Console.WriteLine($"Failed to create window: {error}");
            SDL.SDL_Quit();
            return;
        }

        _sdlRenderer = SDL.SDL_CreateRenderer(
            _sdlWindow,
            -1,
            SDL.SDL_RendererFlags.SDL_RENDERER_ACCELERATED |
            SDL.SDL_RendererFlags.SDL_RENDERER_PRESENTVSYNC);

        if (_sdlRenderer == IntPtr.Zero)
        {
            string error = SDL.SDL_GetError();
            Console.WriteLine($"Failed to create renderer: {error}");
            SDL.SDL_DestroyWindow(_sdlWindow);
            SDL.SDL_Quit();
            return;
        }
        
        // Set renderer scaling to match window size
        SDL.SDL_RenderSetLogicalSize(_sdlRenderer, SCREEN_WIDTH, SCREEN_HEIGHT);

        if (_debug && _romFilenameOverwrite.Length > 0)
        {
            args[0] = _romFilenameOverwrite;
        }
        
        if (args.Length > 0 && File.Exists(args[0]))
        {
            Console.WriteLine("Loading file: " + args[0]);
            _emulationThread = new EmulationThread(args[0], _sdlRenderer);
            SDLEventPublisher.AttachEmulationThread(_emulationThread);
            _emulationThread.Start();
            
            // Enter the main event loop
            _running = true;
            RunMainLoop();
        }
        else
        {
            Console.WriteLine("No ROM file specified or file not found.");
            Console.WriteLine("Usage: dotnetGBC <path-to-rom>");
        }
        
        Quit();
    }

    public static void Quit()
    {
        // Clean up resources
        _emulationThread?.Dispose();
        
        if (_sdlRenderer != IntPtr.Zero)
            SDL.SDL_DestroyRenderer(_sdlRenderer);
        
        if (_sdlWindow != IntPtr.Zero)
            SDL.SDL_DestroyWindow(_sdlWindow);
        
        SDL.SDL_Quit();
        
        _running = false;
    }

    private static void RunMainLoop()
    {
        while (_running)
        {
            // Process all SDL events through the publisher
            SDLEventPublisher.ProcessEvents();
            
            // Check if a QUIT event was received
            SDL.SDL_PumpEvents();
            if (SDL.SDL_HasEvent(SDL.SDL_EventType.SDL_QUIT) == SDL.SDL_bool.SDL_TRUE)
            {
                _running = false;
            }
            
            // Update the frame if necessary
            _emulationThread?.UpdateFrame();
            
            // Small delay to prevent hogging the CPU
            SDL.SDL_Delay(1);
        }
    }

    public static void UpdateTitle(string title)
    {
        if (_sdlWindow != IntPtr.Zero)
        {
            SDL.SDL_SetWindowTitle(_sdlWindow, "dotnetGBC - " + title);
        }
    }
}

