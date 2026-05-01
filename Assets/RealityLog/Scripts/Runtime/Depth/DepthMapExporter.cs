# nullable enable

using System;
using System.IO;
using UnityEngine;
using UnityEngine.Android;
using RealityLog.Common;
using RealityLog.IO;
using System.Collections;

namespace RealityLog.Depth
{
    public class DepthMapExporter : MonoBehaviour
    {
        private static readonly string[] descriptorHeader = new[]
            {
                "timestamp_ms", "ovr_timestamp",
                "create_pose_location_x", "create_pose_location_y", "create_pose_location_z",
                "create_pose_rotation_x", "create_pose_rotation_y", "create_pose_rotation_z", "create_pose_rotation_w",
                "fov_left_angle_tangent", "fov_right_angle_tangent", "fov_top_angle_tangent", "fov_down_angle_tangent",
                "near_z", "far_z",
                "width", "height"
            };

        // My fields for backend export
        [SerializeField] private DepthPacketSender packetSender = default!;
        [SerializeField] private string deviceId = "quest_01";
        [SerializeField] private bool streamLeftDepth = true;
        [SerializeField] private bool streamRightDepth = false;


        [HideInInspector]
        [SerializeField] private ComputeShader copyDepthMapShader = default!;
        [SerializeField] private string directoryName = "";
        [SerializeField] private string leftDepthMapDirectoryName = "left_depth";
        [SerializeField] private string rightDepthMapDirectoryName = "right_depth";
        [SerializeField] private string leftDepthDescFileName = "left_depth_descriptors.csv";
        [SerializeField] private string rightDepthDescFileName = "right_depth_descriptors.csv";
        [Header("Synchronized Capture")]
        [Tooltip("Required: Reference to CaptureTimer for FPS-based capture timing.")]
        [SerializeField] private CaptureTimer captureTimer = default!;

        private DepthDataExtractor? depthDataExtractor;

        private DepthRenderTextureExporter? renderTextureExporter;
        private CsvWriter? leftDepthCsvWriter;
        private CsvWriter? rightDepthCsvWriter;

        private double baseOvrTimeSec;
        private long baseUnixTimeMs;

        private bool hasScenePermission = false;
        private bool depthSystemReady = false;

        public bool IsDepthSystemReady => depthSystemReady;

        public string DirectoryName
        {
            get => directoryName;
            set => directoryName = value;
        }

        // Utility to read raw depth data back as float array for packet sending
        private static float[] ReadDepthRawAsFloatArray(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int count = bytes.Length / sizeof(float);
            float[] result = new float[count];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        public void StartExport()
        {
            leftDepthCsvWriter?.Dispose();
            rightDepthCsvWriter?.Dispose();

            // Reset base times when starting a new recording session
            // This ensures timestamps align with camera/pose data
            baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
            baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            
            Debug.Log($"[{Constants.LOG_TAG}] DepthMapExporter - Reset base times: OVR={baseOvrTimeSec:F3}s, Unix={baseUnixTimeMs}ms");

            leftDepthCsvWriter = new(Path.Join(Application.persistentDataPath, DirectoryName, leftDepthDescFileName), descriptorHeader);
            rightDepthCsvWriter = new(Path.Join(Application.persistentDataPath, DirectoryName, rightDepthDescFileName), descriptorHeader);

            Directory.CreateDirectory(Path.Join(Application.persistentDataPath, DirectoryName, leftDepthMapDirectoryName));
            Directory.CreateDirectory(Path.Join(Application.persistentDataPath, DirectoryName, rightDepthMapDirectoryName));
        }

        public void StopExport()
        {
            // Note: Timer stop is handled by RecordingManager
            // Just cleanup our resources here

            leftDepthCsvWriter?.Dispose();
            leftDepthCsvWriter = null;
            rightDepthCsvWriter?.Dispose();
            rightDepthCsvWriter = null;

            // Note: We keep depth enabled to avoid re-initialization overhead on next recording
        }

        private void Start()
        {
            Debug.Log("[DepthExporter] Start() running.");

            if (packetSender == null)
            {
                Debug.Log("[DepthExporter] packetSender is null, finding/creating sender.");

                packetSender = FindFirstObjectByType<DepthPacketSender>();

                if (packetSender == null)
                {
                    Debug.Log("[DepthExporter] Creating DepthPacketSender.");
                    var senderObject = new GameObject("DepthPacketSender");
                    packetSender = senderObject.AddComponent<DepthPacketSender>();
                }
            }
            baseOvrTimeSec = OVRPlugin.GetTimeInSeconds();
            baseUnixTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            depthDataExtractor = new();
            renderTextureExporter = new(copyDepthMapShader);

            Permission.RequestUserPermission(OVRPermissionsRequester.ScenePermission);

            // Note: We do NOT enable depth here anymore. We wait for permission in Update().
            Application.onBeforeRender += OnBeforeRender;
        }

        private void Update()
        {
            // Try to "prime" the depth system by fetching one frame at startup
            // Once we get a valid frame, mark the system as ready and stop trying
            if (!depthSystemReady && depthDataExtractor != null)
            {
                // Check for permission first
                if (!hasScenePermission)
                {
                    hasScenePermission = Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);
                    if (!hasScenePermission) return; // Wait for permission
                    
                    // Permission granted, enable depth
                    depthDataExtractor.SetDepthEnabled(true);
                    Debug.Log($"[{Constants.LOG_TAG}] DepthMapExporter - Scene permission granted, enabling depth system...");
                }

                if (depthDataExtractor.TryGetUpdatedDepthTexture(out var renderTexture, out var frameDescriptors))
                {
                    if (renderTexture != null && renderTexture.IsCreated())
                    {
                        depthSystemReady = true;
                        Debug.Log($"[{Constants.LOG_TAG}] DepthMapExporter - Depth system warmed up and ready!");
                    }
                }
            }
        }

        private void OnDestroy()
        {
            // Clean up depth system
            depthDataExtractor?.SetDepthEnabled(false);
            
            renderTextureExporter?.Dispose();
            renderTextureExporter = null;

            Application.onBeforeRender -= OnBeforeRender;
        }

        private void OnBeforeRender()
        {
            // Early exit if resources not ready
            if (renderTextureExporter == null || depthDataExtractor == null
                || leftDepthCsvWriter == null || rightDepthCsvWriter == null)
            {
                return;
            }

            // Check if timer says we should capture this frame
            // Timer handles FPS timing internally
            if (!captureTimer.IsCapturing || !captureTimer.ShouldCaptureThisFrame)
            {
                return;
            }
            
            // Debug: Log when we're about to capture
            Debug.Log($"[DepthExporter] Capturing depth at Unity time={Time.unscaledTime:F3}s");

            if (!hasScenePermission)
            {
                hasScenePermission = Permission.HasUserAuthorizedPermission(OVRPermissionsRequester.ScenePermission);

                if (hasScenePermission)
                {
                    depthDataExtractor.SetDepthEnabled(true);
                }
                else
                {
                    return;
                }
            }

            if (depthDataExtractor.TryGetUpdatedDepthTexture(out var renderTexture, out var frameDescriptors))
            {
                // Depth system is ready (we already warmed it up in Update())
                // Just capture the frame data

                const int FRAME_DESC_COUNT = 2;

                if (renderTexture == null || !renderTexture.IsCreated())
                {
                    Debug.LogError("RenderTexture is not created or null.");
                    return;
                }

                if (frameDescriptors.Length != FRAME_DESC_COUNT)
                    {
                        Debug.LogError("Expected exactly two depth frame descriptors (left and right).");
                        return;
                    }

                var width = renderTexture.width;
                var height = renderTexture.height;

                var unixTime = ConvertTimestampNsToUnixTimeMs(frameDescriptors[0].timestampNs);

                var leftDepthFilePath = Path.Join(Application.persistentDataPath, DirectoryName, $"{leftDepthMapDirectoryName}/{unixTime}.raw");
                var rightDepthFilePath = Path.Join(Application.persistentDataPath, DirectoryName, $"{rightDepthMapDirectoryName}/{unixTime}.raw");

                renderTextureExporter.Export(renderTexture, leftDepthFilePath, rightDepthFilePath);
                Debug.Log($"[DepthExporter] Exported raw depth files. left={leftDepthFilePath}");

                Debug.Log("[DepthExporter] About to build/send depth packet.");
                Debug.Log("[DepthExporter] packetSender null? " + (packetSender == null));

                // Send packets to backend
                if (packetSender != null)
                {
                    Debug.Log("[DepthExporter] packetSender exists. Building packet.");
                    Debug.Log("[DepthExporter] streamLeftDepth = " + streamLeftDepth);
                    if (streamLeftDepth)
                    {
                        // try
                        // {
                            var leftFrame = frameDescriptors[0];

                            Debug.Log("[DepthExporter] Checking left file exists: " + File.Exists(leftDepthFilePath));

                            //var leftDepth = ReadDepthRawAsFloatArray(leftDepthFilePath);
                            StartCoroutine(SendDepthWhenFileReady(
                                leftDepthFilePath,
                                "left",
                                ConvertTimestampNsToUnixTimeMs(leftFrame.timestampNs),
                                leftFrame.createPoseLocation,
                                leftFrame.createPoseRotation,
                                leftFrame.fovLeftAngleTangent,
                                leftFrame.fovRightAngleTangent,
                                leftFrame.fovTopAngleTangent,
                                leftFrame.fovDownAngleTangent,
                                leftFrame.nearZ,
                                leftFrame.farZ,
                                width,
                                height
                            ));

                        //     Debug.Log($"[DepthExporter] Read left depth. count={leftDepth.Length}");

                        //     var leftPacket = new DepthPacket
                        //     {
                        //         deviceId = deviceId,
                        //         camera = "left",
                        //         timestampMs = ConvertTimestampNsToUnixTimeMs(leftFrame.timestampNs),
                        //         depth = leftDepth
                        //     };

                        //     Debug.Log("[DepthExporter] Calling packetSender.SendPacket(leftPacket).");
                        //     packetSender.SendPacket(leftPacket);
                        // }
                        // catch (Exception ex)
                        // {
                        //     Debug.LogError("[DepthExporter] LEFT SEND FAILED: " + ex);
                        // }
                    }

                    Debug.Log("[DepthExporter] streamRightDepth = " + streamRightDepth);
                    if (streamRightDepth)
                    {
                        var rightFrame = frameDescriptors[1];
                        var rightDepth = ReadDepthRawAsFloatArray(rightDepthFilePath);

                        Debug.Log($"[DepthExporter] Read right depth. count={rightDepth.Length}");

                        var rightPacket = new DepthPacket
                        {
                            deviceId = deviceId,
                            camera = "right",
                            timestampMs = ConvertTimestampNsToUnixTimeMs(rightFrame.timestampNs),

                            position = new float[]
                            {
                                rightFrame.createPoseLocation.x,
                                rightFrame.createPoseLocation.y,
                                rightFrame.createPoseLocation.z
                            },

                            rotation = new float[]
                            {
                                rightFrame.createPoseRotation.x,
                                rightFrame.createPoseRotation.y,
                                rightFrame.createPoseRotation.z,
                                rightFrame.createPoseRotation.w
                            },

                            fovLeftTangent = rightFrame.fovLeftAngleTangent,
                            fovRightTangent = rightFrame.fovRightAngleTangent,
                            fovTopTangent = rightFrame.fovTopAngleTangent,
                            fovDownTangent = rightFrame.fovDownAngleTangent,

                            nearZ = rightFrame.nearZ,
                            farZ = rightFrame.farZ,

                            width = width,
                            height = height,
                            depth = rightDepth
                        };

                        Debug.Log("[DepthExporter] Calling packetSender.SendPacket(rightPacket).");
                        packetSender.SendPacket(rightPacket);
                    }
                }


                for (var i = 0; i < FRAME_DESC_COUNT; ++i)
                {
                    var frameDesc = frameDescriptors[i];

                    var timestampMs = ConvertTimestampNsToUnixTimeMs(frameDesc.timestampNs);
                    var ovrTimestamp = frameDesc.timestampNs / 1.0e9;

                    var row = new double[]
                    {
                        timestampMs,
                        ovrTimestamp,
                        frameDesc.createPoseLocation.x, frameDesc.createPoseLocation.y, frameDesc.createPoseLocation.z,
                        frameDesc.createPoseRotation.x, frameDesc.createPoseRotation.y, frameDesc.createPoseRotation.z, frameDesc.createPoseRotation.w,
                        frameDesc.fovLeftAngleTangent, frameDesc.fovRightAngleTangent,
                        frameDesc.fovTopAngleTangent, frameDesc.fovDownAngleTangent,
                        frameDesc.nearZ, frameDesc.farZ,
                        width, height
                    };

                    if (i == 0)
                    {
                        leftDepthCsvWriter?.EnqueueRow(row);
                    }
                    else
                    {
                        rightDepthCsvWriter?.EnqueueRow(row);
                    }
                }
            } else {
                Debug.LogError("Failed to get updated depth texture.");
            }
        }

        private long ConvertTimestampNsToUnixTimeMs(long timestampNs)
        {
            var deltaMs = (long) (timestampNs / 1.0e6 - baseOvrTimeSec * 1000.0);
            return baseUnixTimeMs + deltaMs;
        }

       private IEnumerator SendDepthWhenFileReady(
        string path,
        string cameraName,
        long timestampMs,
        Vector3 position,
        Quaternion rotation,
        float fovLeftTangent,
        float fovRightTangent,
        float fovTopTangent,
        float fovDownTangent,
        float nearZ,
        float farZ,
        int width,
        int height)
    {
        float timeout = 1.0f;
        float timer = 0f;

        while (!File.Exists(path) && timer < timeout)
        {
            timer += 0.05f;
            yield return new WaitForSeconds(0.05f);
        }

        if (!File.Exists(path))
        {
            Debug.LogError("[DepthExporter] Timed out waiting for file: " + path);
            yield break;
        }

        Debug.Log($"[DepthExporter] {cameraName} file ready. Reading: {path}");

        var depth = ReadDepthRawAsFloatArray(path);

        Debug.Log($"[DepthExporter] Read {cameraName} depth. count={depth.Length}");

        var packet = new DepthPacket
        {
            deviceId = deviceId,
            camera = cameraName,
            timestampMs = timestampMs,

            position = new float[]
            {
                position.x,
                position.y,
                position.z
            },

            rotation = new float[]
            {
                rotation.x,
                rotation.y,
                rotation.z,
                rotation.w
            },

            fovLeftTangent = fovLeftTangent,
            fovRightTangent = fovRightTangent,
            fovTopTangent = fovTopTangent,
            fovDownTangent = fovDownTangent,

            nearZ = nearZ,
            farZ = farZ,

            width = width,
            height = height,

            depth = depth
        };

        Debug.Log($"[DepthExporter] Calling packetSender.SendPacket({cameraName}).");
        packetSender.SendPacket(packet);
    }

#if UNITY_EDITOR
        private void OnValidate()
        {
            const string COPY_DEPTH_MAP_SHADER_PATH = "Assets/RealityLog/ComputeShaders/CopyDepthMap.compute";

            if (copyDepthMapShader == null)
            {
                var shader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(COPY_DEPTH_MAP_SHADER_PATH);
                if (shader == null)
                {
                    Debug.LogError($"Failed to load ComputeShader at path: {COPY_DEPTH_MAP_SHADER_PATH}");
                }
                else
                {
                    copyDepthMapShader = shader;
                    Debug.Log($"Successfully loaded ComputeShader: {COPY_DEPTH_MAP_SHADER_PATH}");
                }
            }
        }
# endif
    }
}