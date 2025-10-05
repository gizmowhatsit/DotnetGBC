namespace DotnetGBC.SDL2.Input;

public enum SDLInputSubscriptionCategory
{
    GameInputs,
    SystemInputs,
    AllInputs
}

public enum SDLInputBindAction
{
    BUTTON_A = 0,
    BUTTON_B = 1,
    BUTTON_START = 2,
    BUTTON_SELECT = 3,
    BUTTON_LEFT = 4,
    BUTTON_RIGHT = 5,
    BUTTON_UP = 6,
    BUTTON_DOWN = 7,
    HOTKEY_PAUSE = 8,
    HOTKEY_REWIND = 9,
    HOTKEY_FAST_FORWARD = 10
}

public enum SDLInputBindingDeviceType
{
    Keyboard,
    Gamepad
}

