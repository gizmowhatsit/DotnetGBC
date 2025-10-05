namespace DotnetGBC.Memory;

/// <summary>
/// Interface for components that allow subscribing to memory write events
/// </summary>
public interface IMemoryWriteObservable
{
    /// <summary>
    /// Register a handler for writes to a specific address
    /// </summary>
    void RegisterWriteHandler(ushort address, Action<ushort, byte> handler);
    
    /// <summary>
    /// Unregister a handler for writes to a specific address
    /// </summary>
    void UnregisterWriteHandler(ushort address, Action<ushort, byte> handler);
}

