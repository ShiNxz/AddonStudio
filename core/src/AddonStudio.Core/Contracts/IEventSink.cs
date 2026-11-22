namespace AddonStudio.Core.Contracts;

/// <summary>
/// Where a long request streams its events. The host gives each request a sink bound to its id, so services call
/// Emit(EventNames.X, new XEvent(...)) without knowing the request. Emit must be cheap and thread-safe, never throws.
/// </summary>
public interface IEventSink
{
    void Emit(string eventName, object data);
}

/// <summary>Drops every event.</summary>
public sealed class NullEventSink : IEventSink
{
    public static readonly NullEventSink Instance = new();

    private NullEventSink()
    {
    }

    public void Emit(string eventName, object data)
    {
    }
}

/// <summary>Calls a delegate for every event (CLI progress, tests).</summary>
public sealed class DelegateEventSink(Action<string, object> emit) : IEventSink
{
    public void Emit(string eventName, object data)
    {
        try
        {
            emit(eventName, data);
        }
        catch
        {
            // a sink never throws into the service that emits
        }
    }
}
