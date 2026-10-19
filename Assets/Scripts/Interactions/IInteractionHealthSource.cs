/// <summary>
/// Lets the exhibition layer know whether an input source is actually delivering data,
/// so the piece can fall back or ask for help instead of sitting silently dead in a gallery.
/// </summary>
public interface IInteractionHealthSource
{
    /// <summary>True when this source is producing usable interaction data right now.</summary>
    bool IsTracking { get; }

    /// <summary>Short human-readable reason, shown on the operator overlay. Empty when healthy.</summary>
    string TrackingStatus { get; }
}
