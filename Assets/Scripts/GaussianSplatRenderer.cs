using System;
using System.IO;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

public class GaussianSplatRenderer : MonoBehaviour
{
    const string kPointCloudPly = "point_cloud/iteration_7000/point_cloud.ply";
    const string kCamerasJson = "cameras.json";

    [FolderPicker(kPointCloudPly)]
    public string m_PointCloudFolder;
    [Range(1,30)]
    public int m_ScaleDown = 10;
    public Material m_Material;
    public ComputeShader m_CSSplatUtilities;
    public ComputeShader m_CSGpuSort;

    // input file splat data is expected to be in this format
    public struct InputSplat
    {
        public Vector3 pos;
        public Vector3 nor;
        public Vector3 dc0;
        public Vector3 sh0, sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, sh10, sh11, sh12, sh13, sh14;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }

    public struct CameraData
    {
        public Vector3 pos;
        public Vector3 axisX, axisY, axisZ;
        public float fov;
    }

    int m_SplatCount;
    Bounds m_Bounds;
    NativeArray<InputSplat> m_SplatData;
    CameraData[] m_Cameras;

    GraphicsBuffer m_GpuData;
    GraphicsBuffer m_GpuPositions;
    GraphicsBuffer m_GpuSortDistances;
    GraphicsBuffer m_GpuSortKeys;

    IslandGPUSort m_Sorter;
    IslandGPUSort.Args m_SorterArgs;

    public string pointCloudFolder => m_PointCloudFolder;
    public int splatCount => m_SplatCount;
    public Bounds bounds => m_Bounds;
    public NativeArray<InputSplat> splatData => m_SplatData;
    public GraphicsBuffer gpuSplatData => m_GpuData;
    public CameraData[] cameras => m_Cameras;

    public static NativeArray<InputSplat> LoadPLYSplatFile(string folder)
    {
        NativeArray<InputSplat> data = default;
        string plyPath = $"{folder}/{kPointCloudPly}";
        if (!File.Exists(plyPath))
            return data;
        int splatCount = 0;
        PLYFileReader.ReadFile(plyPath, out splatCount, out int vertexStride, out var plyAttrNames, out var verticesRawData);
        if (UnsafeUtility.SizeOf<InputSplat>() != vertexStride)
            throw new Exception($"InputVertex size mismatch, we expect {UnsafeUtility.SizeOf<InputSplat>()} file has {vertexStride}");
        return verticesRawData.Reinterpret<InputSplat>(1);
    }

    static CameraData[] LoadJsonCamerasFile(string folder)
    {
        string path = $"{folder}/{kCamerasJson}";
        if (!File.Exists(path))
            return null;

        string json = File.ReadAllText(path);
        // JsonUtility does not support 2D arrays, so mogrify that into something else, argh
        string num = "([\\-\\d\\.]+)";
        string vec = $"\\[{num},\\s*{num},\\s*{num}\\]";
        json = System.Text.RegularExpressions.Regex.Replace(json,
            $"\"rotation\": \\[{vec},\\s*{vec},\\s*{vec}\\]",
            "\"rotx\":[$1,$2,$3], \"roty\":[$4,$5,$6], \"rotz\":[$7,$8,$9]"
        );
        json = $"{{ \"cameras\": {json} }}";
        var jsonCameras = JsonUtility.FromJson<JsonCameras>(json);
        var result = new CameraData[jsonCameras.cameras.Length];
        for (var camIndex = 0; camIndex < jsonCameras.cameras.Length; camIndex++)
        {
            var jsonCam = jsonCameras.cameras[camIndex];
            var pos = new Vector3(jsonCam.position[0], jsonCam.position[1], jsonCam.position[2]);
            // the matrix is a "view matrix", not "camera matrix" lol
            var axisx = new Vector3(jsonCam.rotx[0], jsonCam.roty[0], jsonCam.rotz[0]);
            var axisy = new Vector3(jsonCam.rotx[1], jsonCam.roty[1], jsonCam.rotz[1]);
            var axisz = new Vector3(jsonCam.rotx[2], jsonCam.roty[2], jsonCam.rotz[2]);

            pos.z *= -1;
            axisy *= -1;
            axisx.z *= -1;
            axisy.z *= -1;
            axisz.z *= -1;

            var cam = new CameraData
            {
                pos = pos,
                axisX = axisx,
                axisY = axisy,
                axisZ = axisz,
                fov = 25 //@TODO
            };
            result[camIndex] = cam;
        }

        return result;
    }

    public void OnEnable()
    {
        Camera.onPreCull += OnPreCullCamera;

        m_Cameras = null;
        if (m_Material == null || m_CSSplatUtilities == null || m_CSGpuSort == null)
            return;
        m_Cameras = LoadJsonCamerasFile(m_PointCloudFolder);
        m_SplatData = LoadPLYSplatFile(m_PointCloudFolder);
        m_SplatCount = m_SplatData.Length / m_ScaleDown;

        m_Bounds = new Bounds(m_SplatData[0].pos, Vector3.zero);
        NativeArray<Vector3> inputPositions = new NativeArray<Vector3>(m_SplatCount, Allocator.Temp);
        for (var i = 0; i < m_SplatCount; ++i)
        {
            var pos = m_SplatData[i].pos;
            inputPositions[i] = pos;
            m_Bounds.Encapsulate(pos);
        }

        m_GpuPositions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 12);
        m_GpuPositions.SetData(inputPositions);
        inputPositions.Dispose();

        m_GpuData = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, UnsafeUtility.SizeOf<InputSplat>());
        m_GpuData.SetData(m_SplatData, 0, 0, m_SplatCount);

        m_GpuSortDistances = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 4);
        m_GpuSortKeys = new GraphicsBuffer(GraphicsBuffer.Target.Structured, m_SplatCount, 4);

        m_Material.SetBuffer("_DataBuffer", m_GpuData);
        m_Material.SetBuffer("_OrderBuffer", m_GpuSortKeys);

        m_Sorter = new IslandGPUSort(m_CSGpuSort);
        m_SorterArgs.inputKeys = m_GpuSortDistances;
        m_SorterArgs.inputValues = m_GpuSortKeys;
        m_SorterArgs.count = (uint)m_SplatCount;
        m_SorterArgs.resources = IslandGPUSort.SupportResources.Load(m_SplatCount);
        m_Material.SetBuffer("_OrderBuffer", m_SorterArgs.resources.sortBufferValues);
    }

    void OnPreCullCamera(Camera cam)
    {
        if (m_GpuData == null)
            return;

        SortPoints(cam);
        Graphics.DrawProcedural(m_Material, m_Bounds, MeshTopology.Triangles, 36, m_SplatCount, cam);
    }

    public void OnDisable()
    {
        Camera.onPreCull -= OnPreCullCamera;
        m_SplatData.Dispose();
        m_GpuData?.Dispose();
        m_GpuPositions?.Dispose();
        m_GpuSortDistances?.Dispose();
        m_GpuSortKeys?.Dispose();
        m_SorterArgs.resources.Dispose();
    }

    void SortPoints(Camera cam)
    {
        // calculate distance to the camera for each splat
        m_CSSplatUtilities.SetBuffer(0, "_InputPositions", m_GpuPositions);
        m_CSSplatUtilities.SetBuffer(0, "_SplatSortDistances", m_GpuSortDistances);
        m_CSSplatUtilities.SetBuffer(0, "_SplatSortKeys", m_GpuSortKeys);
        m_CSSplatUtilities.SetMatrix("_WorldToCameraMatrix", cam.worldToCameraMatrix);
        m_CSSplatUtilities.SetInt("_SplatCount", m_SplatCount);
        m_CSSplatUtilities.GetKernelThreadGroupSizes(0, out uint gsX, out uint gsY, out uint gsZ);
        m_CSSplatUtilities.Dispatch(0, (m_SplatCount + (int)gsX - 1)/(int)gsX, 1, 1);

        // sort the splats
        CommandBuffer cmd = new CommandBuffer {name = "GPUSort"};
        m_Sorter.Dispatch(cmd, m_SorterArgs);
        Graphics.ExecuteCommandBuffer(cmd);
    }

    [Serializable]
    public class JsonCamera
    {
        public int id;
        public string img_name;
        public int width;
        public int height;
        public float[] position;
        public float[] rotx;
        public float[] roty;
        public float[] rotz;
        public float fx;
        public float fy;
    }

    [Serializable]
    public class JsonCameras
    {
        public JsonCamera[] cameras;
    }
}
