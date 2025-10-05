using SDL2;

namespace DotnetGBC.SDL2.Input;

public class InputBinding
{
    public SDLInputBindingDeviceType DeviceType { get; set; }
    public int Code { get; set; }
    
    public InputBinding() {}

    public InputBinding(SDL.SDL_Keycode keycode)
    {
        DeviceType = SDLInputBindingDeviceType.Keyboard;
        Code = (int)keycode;
    }

    public InputBinding(SDL.SDL_GameControllerButton buttonCode)
    {
        DeviceType = SDLInputBindingDeviceType.Gamepad;
        Code = (int)buttonCode;
    }

    public bool MatchesEvent(SDL.SDL_KeyboardEvent keyboardEvent)
    {
        return DeviceType == SDLInputBindingDeviceType.Keyboard &&
               Code == (int)keyboardEvent.keysym.sym;
    }

    public bool MatchesEvent(SDL.SDL_ControllerButtonEvent buttonEvent)
    {
        return DeviceType == SDLInputBindingDeviceType.Gamepad &&
               Code == buttonEvent.button;
    }
}

