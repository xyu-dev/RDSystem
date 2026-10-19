using UnityEngine;

namespace RDSystem {

public sealed class RDSystemUpdater : MonoBehaviour
{
    [SerializeField] CustomRenderTexture _texture = null;
    [SerializeField, Range(1, 16)] int _stepsPerFrame = 4;

    void Start()
      => _texture.Initialize();

    void Update()
      => _texture.Update(_stepsPerFrame);

    /// <summary>
    /// Steps taken per frame. RDRecession lowers this as the room empties, so the pattern
    /// visibly stops evolving rather than carrying on without anyone there.
    /// </summary>
    public int StepsPerFrame
    {
      get => _stepsPerFrame;
      set => _stepsPerFrame = Mathf.Clamp(value, 1, 16);
    }

    /// <summary>
    /// Wipes the simulation back to its seed state. Hook this to
    /// RDPresenceTracker.onSessionEnd only if you want each visitor to meet an unmarked surface;
    /// leaving it unhooked means the pattern a visitor grew stays in the room after they go.
    /// </summary>
    public void ResetField()
      => _texture.Initialize();
}

} // namespace RDSystem
