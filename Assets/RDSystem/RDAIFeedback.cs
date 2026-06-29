using UnityEngine;

/// <summary>
/// AI-controlled variant of RDFeedback.
/// Suppresses parent Feed/Kill writes so RDAIController has full ownership.
/// All interaction logic (ClickState, ClickPos, ClickRadius) still works normally.
/// </summary>
public class RDAIFeedback : RDFeedback
{
    protected override void WriteFeed(float feed)
    {
        // Intentionally empty: Feed is owned by RDAIController
    }

    protected override void WriteKill(float kill)
    {
        // Intentionally empty: Kill is owned by RDAIController
    }
}