using UnityEngine;
using UnityEngine.EventSystems;

public static class RDInteractionUtils
{
    public static bool TryGetPointerHit(PointerEventData eventData, GameObject target, out RaycastHit targetHit)
    {
        targetHit = default;

        if (eventData == null || target == null)
            return false;

        Camera cam = eventData.pressEventCamera ?? Camera.main;
        if (cam == null) return false;

        Ray ray = cam.ScreenPointToRay(eventData.position);
        RaycastHit[] hits = Physics.RaycastAll(ray);

        foreach (var hit in hits)
        {
            if (hit.collider != null &&
                hit.collider.gameObject == target &&
                hit.collider is MeshCollider)
            {
                targetHit = hit;
                return true;
            }
        }

        return false;
    }

    public static Vector2 GetUVFromPointer(PointerEventData eventData, GameObject target)
    {
        if (TryGetPointerHit(eventData, target, out RaycastHit hit))
            return hit.textureCoord;

        return Vector2.zero;
    }

    public static Vector3 GetLocalDirectionFromPointer(PointerEventData eventData, GameObject target)
    {
        if (target == null)
            return Vector3.forward;

        if (TryGetPointerHit(eventData, target, out RaycastHit hit))
        {
            Vector3 localPoint = target.transform.InverseTransformPoint(hit.point);
            Vector3 localDir = localPoint.normalized;

            if (localDir.sqrMagnitude < 0.000001f)
                return Vector3.forward;

            return localDir;
        }

        return Vector3.forward;
    }

    public static Vector3 MirrorLocalDirection(Vector3 localDir, bool flipX, bool flipY)
    {
        if (localDir.sqrMagnitude < 0.000001f)
            localDir = Vector3.forward;

        if (flipX) localDir.x *= -1f;
        if (flipY) localDir.y *= -1f;

        return localDir.normalized;
    }
}