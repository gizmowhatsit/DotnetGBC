using System.Net.Mime;
using DotnetGBC.Threading;
using Microsoft.VisualBasic;
using SDL2;

namespace DotnetGBC.SDL2.Input;

public static class SDLEventPublisher
{
    
    private static InputBindingManager _inputBindingManager;
    
    private static List<SDLEventSubscriber> _gameInputSubscribers = [];
    private static List<SDLEventSubscriber> _systemInputSubscribers = [];
    private static List<SDLEventSubscriber> _allInputSubscribers = [];
    private static EmulationThread _mainEmulationThread;
    
    // Audio device tracking
    private static HashSet<uint> _audioDevices = [];

    static SDLEventPublisher()
    {
        _inputBindingManager = new InputBindingManager();
        _inputBindingManager.LoadBindingsFromDisk();
        _inputBindingManager.SaveBindingsToDisk();
    }

    public static void Subscribe(SDLEventSubscriber subscriber, SDLInputSubscriptionCategory inputSubscription)
    {
        switch (inputSubscription)
        {
            case SDLInputSubscriptionCategory.GameInputs:
                _gameInputSubscribers.Add(subscriber);
                
                break;
            case SDLInputSubscriptionCategory.SystemInputs:
                _systemInputSubscribers.Add(subscriber);
                
                break;
            case SDLInputSubscriptionCategory.AllInputs:
                _allInputSubscribers.Add(subscriber);
                break;
            
            default:
                throw new ArgumentOutOfRangeException(nameof(inputSubscription), inputSubscription, null);
        }
    }

    public static void AttachEmulationThread(EmulationThread emulationThread)
    {
        _mainEmulationThread = emulationThread;
    }

    public static void Unsubscribe(SDLEventSubscriber subscriber)
    {
        _allInputSubscribers.Remove(subscriber);
        _gameInputSubscribers.Remove(subscriber);
        _systemInputSubscribers.Remove(subscriber);
    }
    
    public static void ProcessEvents()
    { 
        while (SDL.SDL_PollEvent(out SDL.SDL_Event sdlEvent) != 0)
        {
            switch (sdlEvent.type)
            {
                // Keyboard input
                case SDL.SDL_EventType.SDL_KEYDOWN:
                case SDL.SDL_EventType.SDL_KEYUP:
                    ProcessKeyboardEvent(sdlEvent.key,
                        sdlEvent.type == SDL.SDL_EventType.SDL_KEYDOWN);
                    break;
                    
                // Controller input
                case SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN:
                case SDL.SDL_EventType.SDL_CONTROLLERBUTTONUP:
                    ProcessControllerButtonEvent(sdlEvent.cbutton, 
                        (sdlEvent.type == SDL.SDL_EventType.SDL_CONTROLLERBUTTONDOWN));
                    break;
                
                case SDL.SDL_EventType.SDL_CONTROLLERDEVICEADDED:
                    // A controller was connected - open it
                    SDL.SDL_GameControllerOpen(sdlEvent.cdevice.which);
                    break;
                    
                case SDL.SDL_EventType.SDL_CONTROLLERDEVICEREMOVED:
                    // A controller was disconnected - nothing to do here
                    break;
                    
                // Audio device events (for SDL_mixer)
                case SDL.SDL_EventType.SDL_AUDIODEVICEADDED:
                    if (sdlEvent.adevice.iscapture == 0) // 0 = output device
                    {
                        _audioDevices.Add(sdlEvent.adevice.which);
                    }
                    break;
                    
                case SDL.SDL_EventType.SDL_AUDIODEVICEREMOVED:
                    if (sdlEvent.adevice.iscapture == 0)
                    {
                        _audioDevices.Remove(sdlEvent.adevice.which);
                    }
                    break;

                case SDL.SDL_EventType.SDL_QUIT:
                    {
                        _mainEmulationThread.Dispose();
                        Program.Quit();
                    }
                    break;
            }
        }
    }
    
    private static void ProcessKeyboardEvent(SDL.SDL_KeyboardEvent keyEvent, bool isPressed)
    {
        foreach (var input in _inputBindingManager.Bindings.Keys)
        {
            foreach (var binding in _inputBindingManager.Bindings[input])
            {
                if (binding.MatchesEvent(keyEvent))
                {
                    SDLInputEvent sdlInputEvent = new SDLInputEvent
                    {
                        DeviceType = SDLInputBindingDeviceType.Gamepad,
                        InputBinding = input,
                        KeyButtonCode = (int) keyEvent.keysym.sym
                    };

                    foreach (var allInputSubscriber in _allInputSubscribers)
                    {
                        allInputSubscriber.Process(sdlInputEvent, isPressed);
                    }
                    
                    if (input >= SDLInputBindAction.HOTKEY_PAUSE)
                    {
                        foreach (var systemInputSubscriber in _systemInputSubscribers)
                        {
                            systemInputSubscriber.Process(sdlInputEvent, isPressed);
                        }
                    }
                    else
                    {
                        foreach (var gameInputSubscriber in _gameInputSubscribers)
                        {
                            gameInputSubscriber.Process(sdlInputEvent, isPressed);
                        }
                    }
                    break;
                }

                else
                {
                    SDLInputEvent sdlInputEvent = new SDLInputEvent
                    {
                        DeviceType = SDLInputBindingDeviceType.Gamepad,
                        InputBinding = null,
                        KeyButtonCode = (int) keyEvent.keysym.sym
                    };

                    foreach (var allInputSubscriber in _allInputSubscribers)
                    {
                        allInputSubscriber.Process(sdlInputEvent, isPressed);
                    }
                }
            }
        }
    }
    
    private static void ProcessControllerButtonEvent(SDL.SDL_ControllerButtonEvent buttonEvent, bool isPressed)
    {
        foreach (var input in _inputBindingManager.Bindings.Keys)
        {
            foreach (var binding in _inputBindingManager.Bindings[input])
            {
                if (binding.MatchesEvent(buttonEvent))
                {
                    SDLInputEvent sdlInputEvent = new SDLInputEvent
                    {
                        DeviceType = SDLInputBindingDeviceType.Gamepad,
                        InputBinding = input,
                        KeyButtonCode = buttonEvent.which
                    };

                    foreach (var allInputSubscriber in _allInputSubscribers)
                    {
                        allInputSubscriber.Process(sdlInputEvent, isPressed);
                    }
                    
                    if (input >= SDLInputBindAction.HOTKEY_PAUSE)
                    {
                        foreach (var systemInputSubscriber in _systemInputSubscribers)
                        {
                            systemInputSubscriber.Process(sdlInputEvent, isPressed);
                        }
                    }
                    else
                    {
                        foreach (var gameInputSubscriber in _gameInputSubscribers)
                        {
                            gameInputSubscriber.Process(sdlInputEvent, isPressed);
                        }
                    }
                    
                    break;
                }

                else
                {
                    SDLInputEvent sdlInputEvent = new SDLInputEvent
                    {
                        DeviceType = SDLInputBindingDeviceType.Gamepad,
                        InputBinding = null,
                        KeyButtonCode = buttonEvent.which
                    };

                    foreach (var allInputSubscriber in _allInputSubscribers)
                    {
                        allInputSubscriber.Process(sdlInputEvent, isPressed);
                    }
                }
            }
        }
    }
}

