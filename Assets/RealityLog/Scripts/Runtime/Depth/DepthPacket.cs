using System;

[Serializable]
public class DepthPacket
{
    public string type = "depth_frame";
    public string deviceId;
    public string camera; // "left" or "right"

    public long timestampMs;

    public float[] position;   // [x, y, z]
    public float[] rotation;   // [x, y, z, w]

    public float fovLeftTangent;
    public float fovRightTangent;
    public float fovTopTangent;
    public float fovDownTangent;

    public float nearZ;
    public float farZ;

    public int width;
    public int height;

    public float[] depth;
}