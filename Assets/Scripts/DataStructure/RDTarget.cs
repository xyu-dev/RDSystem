using UnityEngine;

[System.Serializable]
public struct RDTarget
{
    public float feed;
    public float kill;

    public float clickState;
    public float clickRadius;
    public float resetBlend;

    public Vector2 clickPos;
    public Vector3 clickDir;
}