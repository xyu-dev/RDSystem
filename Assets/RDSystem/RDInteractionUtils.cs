//using Diagnostics = System.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Static utility library for RD interaction calculations.
/// No MonoBehaviour, no state — pure functions only.
/// </summary>
public static class RDInteractionUtils
{
    /// <summary>
    /// Casts a ray from the pointer and returns the UV on the target
    /// GameObject's MeshCollider. Returns Vector2.zero if nothing is hit.
    /// </summary>
    public static Vector2 GetUVFromPointer(PointerEventData eventData, GameObject target)
    {
        Camera cam = eventData.pressEventCamera ?? Camera.main;
        if (cam == null) return Vector2.zero;

        Ray ray = cam.ScreenPointToRay(eventData.position);
        RaycastHit[] hits = Physics.RaycastAll(ray);

        foreach (var hit in hits)
        {
            if (hit.collider.gameObject == target && hit.collider is MeshCollider)
            {   //Debug.Log($"Hit {target.name} at UV {hit.textureCoord}");
                return hit.textureCoord;}
        }

        return Vector2.zero;
    }

    /// <summary>
    /// Advances radius and feed by a random delta each call.
    /// grow=true → increase toward max; grow=false → decrease toward base.
    /// Returns the clamped (radius, feed) tuple.
    /// </summary>
    public static (float radius, float feed) StepParameters(
        float radius,        float feed,
        float minRadSpeed,   float maxRadSpeed,
        float minFeedSpeed,  float maxFeedSpeed,
        float baseRadius,    float maxRadius,
        float baseFeed,      float maxFeed,
        bool  grow)
    {
        float sign = grow ? 1f : -1f;

        radius += sign * Random.Range(minRadSpeed,  maxRadSpeed)  * Time.deltaTime;
        feed   += sign * Random.Range(minFeedSpeed, maxFeedSpeed) * Time.deltaTime;

        radius = Mathf.Clamp(radius, baseRadius, maxRadius);
        feed   = Mathf.Clamp(feed,   baseFeed,   maxFeed);

        return (radius, feed);
    }

    /// <summary>
    /// Maps an interaction duration to a normalized [0,1] intensity.
    /// Useful for driving visual effects based on how long the user has been gazing.
    /// </summary>
    public static float DurationToIntensity(float duration, float fullIntensityAfter = 2f)
    {
        return Mathf.Clamp01(duration / fullIntensityAfter);
    }
}
