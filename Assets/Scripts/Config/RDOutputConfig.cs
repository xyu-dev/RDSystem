using UnityEngine;

[CreateAssetMenu(menuName = "RD/Output Config", fileName = "RDOutputConfig")]
public class RDOutputConfig : ScriptableObject
{
    public bool writeClickPos = true;
    public bool writeClickState = true;
    public bool writeClickRadius = true;
    public bool writeFeed = true;
    public bool writeKill = true;
    public bool writeResetBlend = true;
    public bool writeClickDir = true;
}