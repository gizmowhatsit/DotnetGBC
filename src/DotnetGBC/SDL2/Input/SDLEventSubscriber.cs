namespace DotnetGBC.SDL2.Input;

public interface SDLEventSubscriber
{
    public void Process(SDLInputEvent sdlInputEvent, bool isPressed);
}

