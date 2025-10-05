namespace DotnetGBC.SDL2.Input;

public struct SDLInputEvent
{
    public SDLInputBindingDeviceType DeviceType;
    public SDLInputBindAction? InputBinding;
    public int KeyButtonCode;
}

