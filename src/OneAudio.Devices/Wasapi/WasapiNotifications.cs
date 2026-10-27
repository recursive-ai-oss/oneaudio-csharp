using OneAudio.Devices.Interop;

namespace OneAudio.Devices;

/// <summary>
/// Maps endpoint notifications from the operating system onto the engines of the open streams: a default change reaches the engines of that
/// direction, a change in the set of devices is raised once.
/// </summary>
internal sealed class WasapiNotifications : IEndpointEvents
{
    private readonly Lock _gate = new();
    private (DeviceDirection Direction, IDeviceEngine Engine)[] _engines = [];

    /// <summary>Starts telling an engine about default changes of its direction.</summary>
    public void Add(DeviceDirection direction, IDeviceEngine engine)
    {
        lock (_gate)
        {
            _engines = [.. _engines, (direction, engine)];
        }
    }

    /// <summary>Stops telling an engine.</summary>
    public void Remove(IDeviceEngine engine)
    {
        lock (_gate)
        {
            _engines = Array.FindAll(_engines, e => !ReferenceEquals(e.Engine, engine));
        }
    }

    /// <inheritdoc/>
    public void DefaultDeviceChanged(int flow, int role)
    {
        // Windows reports each role separately; the stream follows the console role, which is the one it opened.
        if (role != WasapiLogic.RoleConsole || WasapiLogic.DirectionOf(flow) is not { } direction)
        {
            return;
        }

        foreach (var (d, engine) in Volatile.Read(ref _engines))
        {
            if (d == direction)
            {
                engine.NotifyDefaultChanged();
            }
        }
    }

    /// <inheritdoc/>
    public void DevicesChanged()
    {
        var engines = Volatile.Read(ref _engines);
        if (engines.Length > 0)
        {
            engines[0].Engine.NotifyDevicesChanged();
        }
        else
        {
            AudioDevices.RaiseDevicesChanged();
        }
    }
}
