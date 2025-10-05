using SDL2;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotnetGBC.SDL2.Input;

public class InputBindingManager
{

    public const string BINDING_FILENAME = "bindings.json";
    public Dictionary<SDLInputBindAction, List<InputBinding>> Bindings;
    private string _configFileAbsolutePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, BINDING_FILENAME);

    public InputBindingManager()
    {
        Bindings = new Dictionary<SDLInputBindAction, List<InputBinding>>();
        PopulateEmptyInputBindings();
    }

    private void PopulateEmptyInputBindings()
    {
        foreach (SDLInputBindAction input in Enum.GetValues<SDLInputBindAction>())
        {
            Bindings.Add(input, []);
        }
    }
    
    private void SetDefaultBindings()
    {
        Bindings = new Dictionary<SDLInputBindAction, List<InputBinding>>();
        PopulateEmptyInputBindings();
        
        // Add default gamepad bindings
        // Assuming XInput, so A and B are inverted.
        AddBinding(SDLInputBindAction.BUTTON_A, new InputBinding(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_B));
        AddBinding(SDLInputBindAction.BUTTON_B, new InputBinding(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_A));
        AddBinding(SDLInputBindAction.BUTTON_START, new InputBinding(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_START));
        AddBinding(SDLInputBindAction.BUTTON_SELECT, new InputBinding(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_BACK));
        AddBinding(SDLInputBindAction.BUTTON_UP, new InputBinding(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_UP));
        AddBinding(SDLInputBindAction.BUTTON_DOWN, new InputBinding(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_DOWN));
        AddBinding(SDLInputBindAction.BUTTON_LEFT, new InputBinding(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_LEFT));
        AddBinding(SDLInputBindAction.BUTTON_RIGHT, new InputBinding(SDL.SDL_GameControllerButton.SDL_CONTROLLER_BUTTON_DPAD_RIGHT));
        
        // Add default keyboard bindings
        AddBinding(SDLInputBindAction.BUTTON_A, new InputBinding(SDL.SDL_Keycode.SDLK_z));
        AddBinding(SDLInputBindAction.BUTTON_B, new InputBinding(SDL.SDL_Keycode.SDLK_x));
        AddBinding(SDLInputBindAction.BUTTON_START, new InputBinding(SDL.SDL_Keycode.SDLK_RETURN));
        AddBinding(SDLInputBindAction.BUTTON_SELECT, new InputBinding(SDL.SDL_Keycode.SDLK_BACKSPACE));
        AddBinding(SDLInputBindAction.BUTTON_UP, new InputBinding(SDL.SDL_Keycode.SDLK_UP));
        AddBinding(SDLInputBindAction.BUTTON_DOWN, new InputBinding(SDL.SDL_Keycode.SDLK_DOWN));
        AddBinding(SDLInputBindAction.BUTTON_LEFT, new InputBinding(SDL.SDL_Keycode.SDLK_LEFT));
        AddBinding(SDLInputBindAction.BUTTON_RIGHT, new InputBinding(SDL.SDL_Keycode.SDLK_RIGHT));
        
        AddBinding(SDLInputBindAction.HOTKEY_FAST_FORWARD, new InputBinding(SDL.SDL_Keycode.SDLK_COMMA));
        AddBinding(SDLInputBindAction.HOTKEY_PAUSE, new InputBinding(SDL.SDL_Keycode.SDLK_SPACE));
        AddBinding(SDLInputBindAction.HOTKEY_REWIND, new InputBinding(SDL.SDL_Keycode.SDLK_PERIOD));
    }

    private void AddBinding(SDLInputBindAction sdlInputBind, InputBinding binding)
    {
        Bindings[sdlInputBind].Add(binding);
    }
    
    private void RemoveBinding(SDLInputBindAction sdlInputBind, InputBinding binding)
    {
        Bindings[sdlInputBind].Remove(binding);
    }

    private void ClearBindings(SDLInputBindAction sdlInputBind)
    {
        Bindings[sdlInputBind].Clear();
    }

    public void ClearAllBindings()
    {
        foreach (var kvp in Bindings)
        {
            // Foreach entry in the dictionary, clear the bindings list.
            kvp.Value.Clear();
        }
    }
    
    public void LoadBindingsFromDisk()
    {
        if (!File.Exists(_configFileAbsolutePath))
        {
            SetDefaultBindings();
            return;
        }

        JsonSerializerOptions options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        
        try
        {
            string jsonString = File.ReadAllText(_configFileAbsolutePath);
            Bindings = JsonSerializer.Deserialize<Dictionary<SDLInputBindAction, List<InputBinding>>>(jsonString, options)!;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            Console.WriteLine($"Error loading bindings, falling back to defaults: {ex.Message}");
            SetDefaultBindings();
            return;
        }

        if (Bindings == null || Bindings.Count == 0)
        {
            SetDefaultBindings();
        }
    }

    public void SaveBindingsToDisk()
    {
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };
        
        string jsonString = JsonSerializer.Serialize(Bindings, options);
        File.WriteAllText(_configFileAbsolutePath, jsonString);
    }
}

