using Anaglyph.XRTemplate.DepthKit;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace Anaglyph.XRTemplate
{
    public class EnvironmentMapper : MonoBehaviour
    {
        public static EnvironmentMapper Instance { get; private set; }

        [Header("Compute")]
        [SerializeField] private ComputeShader shader = null;

        [Header("Volume")]
        [SerializeField] private float metersPerVoxel = 0.1f;
        [SerializeField] private float dispatchesPerSecond = 5f;

        [SerializeField] private RenderTexture volume;
        [SerializeField] private RenderTexture coverageVolume;

        private int vWidth => volume != null ? volume.width : 0;
        private int vHeight => volume != null ? volume.height : 0;
        private int vDepth => volume != null ? volume.volumeDepth : 0;

        [Header("Scan Range")]
        [SerializeField] private float maxEyeDist = 7f;
        public float MaxEyeDist => maxEyeDist;

        [SerializeField] private float minEyeDist = 1f;
        public float MinEyeDist => minEyeDist;

        [Header("Coverage Painting")]
        [SerializeField, Range(0.01f, 1f)] private float coverageIncrement = 0.20f;
        [SerializeField, Range(0.01f, 1f)] private float coverageThreshold = 0.75f;
        [SerializeField, Range(0.25f, 3f)] private float coverageSurfaceBandVoxels = 1.2f;
        [SerializeField, Range(0, 3)] private int coverageBrushRadius = 1;

        private ComputeKernel clearKernel;
        private ComputeKernel integrateKernel;
        private ComputeKernel raymarchKernel;

        private int viewID => DepthKitDriver.agDepthView_ID;
        private int projID => DepthKitDriver.agDepthProj_ID;
        private int viewInvID => DepthKitDriver.agDepthViewInv_ID;
        private int projInvID => DepthKitDriver.agDepthProjInv_ID;
        private int depthTexID => DepthKitDriver.agDepthTex_ID;
        private int normTexID => DepthKitDriver.agDepthNormTex_ID;

        private readonly int numPlayersID = Shader.PropertyToID("numPlayers");
        private readonly int playerHeadsWorldID = Shader.PropertyToID("playerHeadsWorld");

        private readonly int numRaymarchRequestsID = Shader.PropertyToID("numRaymarchRequests");
        private readonly int raymarchRequestsID = Shader.PropertyToID("raymarchRequests");
        private readonly int raymarchResultsID = Shader.PropertyToID("raymarchResults");

        private readonly int coverageIncrementID = Shader.PropertyToID("coverageIncrement");
        private readonly int coverageThresholdID = Shader.PropertyToID("coverageThreshold");
        private readonly int coverageSurfaceBandVoxelsID = Shader.PropertyToID("coverageSurfaceBandVoxels");
        private readonly int coverageBrushRadiusID = Shader.PropertyToID("coverageBrushRadius");
        private readonly int volumeCenterWSID = Shader.PropertyToID("volumeCenterWS");

        private static readonly int EnvCoverageVolumeID = Shader.PropertyToID("_EnvCoverageVolume");
        private static readonly int EnvVolumeSizeID = Shader.PropertyToID("_EnvVolumeSize");
        private static readonly int EnvMetersPerVoxelID = Shader.PropertyToID("_EnvMetersPerVoxel");
        private static readonly int EnvVolumeCenterWSID = Shader.PropertyToID("_EnvVolumeCenterWS");
        private static readonly int EnvCoverageThresholdID = Shader.PropertyToID("_EnvCoverageThreshold");

        private const int MaxPlayers = 512;

        // Cached points within the view-space depth frustum
        private ComputeBuffer frustumVolume;

        public List<Transform> PlayerHeads = new();
        private readonly Vector4[] headPositions = new Vector4[MaxPlayers];

        private bool hasStarted = false;

        // -------- Public getters needed by the stereo mask pipeline --------
        public float MetersPerVoxel => metersPerVoxel;
        public RenderTexture VolumeTexture => volume;
        public RenderTexture CoverageTexture => coverageVolume;
        public Vector3Int VolumeResolution => new Vector3Int(vWidth, vHeight, vDepth);
        public Vector3 VolumeCenterWS => transform.position;

        public Bounds VolumeBoundsWS =>
            new Bounds(
                transform.position,
                new Vector3(vWidth, vHeight, vDepth) * metersPerVoxel
            );

        public Vector3 VolumeWorldSize =>
            volume == null
                ? Vector3.one
                : new Vector3(
                    volume.width * metersPerVoxel,
                    volume.height * metersPerVoxel,
                    volume.volumeDepth * metersPerVoxel
                );

        public Vector3 VoxelToWorldCenter(int x, int y, int z)
        {
            Vector3 p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f);
            p -= new Vector3(vWidth, vHeight, vDepth) * 0.5f;
            p *= metersPerVoxel;
            p += transform.position;
            return p;
        }
        // -----------------------------------------------------------------

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            Debug.Log("[EnvironmentMapper] Start() – mapper is alive");

            if (shader == null)
            {
                Debug.LogError("[EnvironmentMapper] Compute shader is not assigned.");
                enabled = false;
                return;
            }

            ValidateTextures();

            if (volume == null || coverageVolume == null)
            {
                enabled = false;
                return;
            }

            clearKernel = new ComputeKernel(shader, "Clear");
            clearKernel.Set(nameof(volume), volume);
            clearKernel.Set("coverageVolume", coverageVolume);

            integrateKernel = new ComputeKernel(shader, "Integrate");
            integrateKernel.Set(nameof(volume), volume);
            integrateKernel.Set("coverageVolume", coverageVolume);

            raymarchKernel = new ComputeKernel(shader, "Raymarch");
            raymarchKernel.Set("raymarchVolume", volume);

            shader.SetInts("volumeSize", vWidth, vHeight, vDepth);
            shader.SetFloat(nameof(metersPerVoxel), metersPerVoxel);

            PushCoverageParamsToShader();
            PushCoverageGlobals();

            Clear();
            ScanLoop();

            hasStarted = true;
        }

        private void ValidateTextures()
        {
            if (volume == null)
            {
                Debug.LogError("[EnvironmentMapper] volume is null.");
                return;
            }

            if (coverageVolume == null)
            {
                Debug.LogError("[EnvironmentMapper] coverageVolume is null.");
                return;
            }

            if (volume.dimension != UnityEngine.Rendering.TextureDimension.Tex3D)
                Debug.LogError("[EnvironmentMapper] volume must be a 3D RenderTexture.");

            if (coverageVolume.dimension != UnityEngine.Rendering.TextureDimension.Tex3D)
                Debug.LogError("[EnvironmentMapper] coverageVolume must be a 3D RenderTexture.");

            if (!volume.enableRandomWrite)
                Debug.LogWarning("[EnvironmentMapper] volume should have Random Write enabled.");

            if (!coverageVolume.enableRandomWrite)
                Debug.LogWarning("[EnvironmentMapper] coverageVolume should have Random Write enabled.");
        }

        private void PushCoverageParamsToShader()
        {
            shader.SetFloat(coverageIncrementID, coverageIncrement);
            shader.SetFloat(coverageThresholdID, coverageThreshold);
            shader.SetFloat(coverageSurfaceBandVoxelsID, coverageSurfaceBandVoxels);
            shader.SetInt(coverageBrushRadiusID, coverageBrushRadius);
            shader.SetVector(volumeCenterWSID, transform.position);
        }

        private void PushCoverageGlobals()
        {
            Shader.SetGlobalTexture(EnvCoverageVolumeID, coverageVolume);
            Shader.SetGlobalVector(EnvVolumeSizeID, new Vector4(vWidth, vHeight, vDepth, 0));
            Shader.SetGlobalFloat(EnvMetersPerVoxelID, metersPerVoxel);
            Shader.SetGlobalVector(EnvVolumeCenterWSID, transform.position);
            Shader.SetGlobalFloat(EnvCoverageThresholdID, coverageThreshold);
        }

        public void Clear()
        {
            if (volume == null || coverageVolume == null)
                return;

            PushCoverageParamsToShader();
            PushCoverageGlobals();
            clearKernel.DispatchGroups(volume);
        }

        private void Update()
        {
            if (volume == null || coverageVolume == null)
                return;

            PushCoverageParamsToShader();
            PushCoverageGlobals();
        }

        private void OnEnable()
        {
            if (!hasStarted)
                ScanLoop();
        }

        private async void ScanLoop()
        {
            while (enabled)
            {
                await Awaitable.WaitForSecondsAsync(1f / Mathf.Max(0.01f, dispatchesPerSecond));

                if (!DepthKitDriver.DepthAvailable)
                    continue;

                var depthTex = Shader.GetGlobalTexture(depthTexID);
                var normTex = Shader.GetGlobalTexture(normTexID);

                if (depthTex == null || normTex == null)
                    continue;

                if (frustumVolume == null)
                    Setup();

                if (frustumVolume == null)
                    continue;

                var views = Shader.GetGlobalMatrixArray(viewID);
                var projs = Shader.GetGlobalMatrixArray(projID);

                if (views == null || views.Length == 0 || projs == null || projs.Length == 0)
                    continue;

                // Scan/integration uses the available depth view.
                Matrix4x4 view = views[0];
                Matrix4x4 proj = projs[0];

                ApplyScan(depthTex, normTex, view, proj);
            }
        }

        public void ApplyScan(Texture depthTex, Texture normTex, Matrix4x4 view, Matrix4x4 proj)
        {
            if (shader == null || frustumVolume == null)
                return;
              

            shader.SetMatrixArray(viewID, new[] { view, Matrix4x4.zero });
            shader.SetMatrixArray(projID, new[] { proj, Matrix4x4.zero });

            shader.SetMatrixArray(viewInvID, new[] { view.inverse, Matrix4x4.zero });
            shader.SetMatrixArray(projInvID, new[] { proj.inverse, Matrix4x4.zero });

            int playerCount = Mathf.Min(PlayerHeads.Count, MaxPlayers);

            for (int i = 0; i < playerCount; i++)
            {
                Vector3 playerHead = PlayerHeads[i] != null ? PlayerHeads[i].position : Vector3.zero;
                headPositions[i] = new Vector4(playerHead.x, playerHead.y, playerHead.z, 1f);
            }

            shader.SetInt(numPlayersID, playerCount);
            shader.SetVectorArray(playerHeadsWorldID, headPositions);

            shader.SetFloat(nameof(metersPerVoxel), metersPerVoxel);
            shader.SetVector(volumeCenterWSID, transform.position);
            shader.SetFloat(coverageIncrementID, coverageIncrement);
            shader.SetFloat(coverageThresholdID, coverageThreshold);
            shader.SetFloat(coverageSurfaceBandVoxelsID, coverageSurfaceBandVoxels);
            shader.SetInt(coverageBrushRadiusID, coverageBrushRadius);

            integrateKernel.Set(depthTexID, depthTex);
            integrateKernel.Set(normTexID, normTex);
            shader.SetInt("numFrustumSamples", frustumVolume.count);


            integrateKernel.DispatchGroups(frustumVolume.count, 1, 1);

            PushCoverageGlobals();
        }

        private void Setup()
        {
            var depthProjArray = Shader.GetGlobalMatrixArray(DepthKitDriver.agDepthProj_ID);
            if (depthProjArray == null || depthProjArray.Length == 0)
            {
                Debug.LogWarning("[EnvironmentMapper] Setup() called but no depth projection available yet.");
                return;
            }

            var depthProj = depthProjArray[0];
            FrustumPlanes frustum = depthProj.decomposeProjection;
            frustum.zFar = maxEyeDist;

            List<Vector3> positions = new(200000);

            float ls = frustum.left / frustum.zNear;
            float rs = frustum.right / frustum.zNear;
            float ts = frustum.top / frustum.zNear;
            float bs = frustum.bottom / frustum.zNear;

            for (float z = frustum.zNear; z < frustum.zFar; z += metersPerVoxel)
            {
                float xMin = ls * z + metersPerVoxel;
                float xMax = rs * z - metersPerVoxel;

                float yMin = bs * z + metersPerVoxel;
                float yMax = ts * z - metersPerVoxel;

                for (float x = xMin; x < xMax; x += metersPerVoxel)
                {
                    for (float y = yMin; y < yMax; y += metersPerVoxel)
                    {
                        Vector3 v = new Vector3(x, y, -z);

                        if (v.magnitude > minEyeDist && v.magnitude < maxEyeDist)
                            positions.Add(v);
                    }
                }
            }

            frustumVolume?.Release();
            frustumVolume = new ComputeBuffer(positions.Count, sizeof(float) * 3);
            frustumVolume.SetData(positions);

            integrateKernel.Set(nameof(frustumVolume), frustumVolume);

            Debug.Log($"[EnvironmentMapper] Setup() created frustumVolume with {positions.Count} samples.");
        }

        private void OnDestroy()
        {
            frustumVolume?.Release();
            frustumVolume = null;

            if (Instance == this)
                Instance = null;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RaymarchRequest
        {
            public RaymarchRequest(Ray ray, float maxDistance)
            {
                origin = ray.origin;
                direction = ray.direction;
                this.maxDistance = maxDistance;
            }

            public Vector4 origin;
            public Vector3 direction;
            public float maxDistance;
        }

        private readonly int requestStride = Marshal.SizeOf<RaymarchRequest>();
        private readonly List<RaymarchRequest> pendingRequests = new();

        public struct RaymarchResult
        {
            public Ray ray;
            public Vector3 point;
            public float distance;
            public bool didHit;
            public Vector3 normal;
            public float confidence;

            public RaymarchResult(Ray ray, float distance, Vector3 normal, float confidence)
            {
                this.ray = ray;
                this.distance = distance;
                this.point = ray.origin + ray.direction * distance;
                this.normal = normal;
                this.confidence = confidence;
                this.didHit = distance >= 0.0f;
            }
        }

        private Task<float[]> currentRaymarchBatch = null;

        public async Task<RaymarchResult> RaymarchAsync(Ray ray, float maxDistance)
        {
            RaymarchRequest request = new RaymarchRequest(ray, maxDistance);
            int index = pendingRequests.Count;
            pendingRequests.Add(request);

            if (currentRaymarchBatch == null)
                currentRaymarchBatch = DispatchRaymarches();

            float[] data = await currentRaymarchBatch;

            int baseIndex = index * 5;
            float dist = data[baseIndex + 0];
            Vector3 normal = new Vector3(
                data[baseIndex + 1],
                data[baseIndex + 2],
                data[baseIndex + 3]
            );
            float confidence = data[baseIndex + 4];

            return new RaymarchResult(ray, dist, normal, confidence);
        }

        private async Task<float[]> DispatchRaymarches()
        {
            await Awaitable.EndOfFrameAsync();

            int count = pendingRequests.Count;
            if (count == 0)
            {
                currentRaymarchBatch = null;
                return new float[0];
            }

            ComputeBuffer requestsBuffer = new ComputeBuffer(count, requestStride);
            requestsBuffer.SetData(pendingRequests);

            const int floatsPerRay = 5;
            ComputeBuffer resultBuffer = new ComputeBuffer(count * floatsPerRay, sizeof(float));

            pendingRequests.Clear();
            currentRaymarchBatch = null;

            shader.SetInt(numRaymarchRequestsID, count);

            // This project’s ComputeKernel wrapper uses Set(...), not SetBuffer(...)
            raymarchKernel.Set(raymarchRequestsID, requestsBuffer);
            raymarchKernel.Set(raymarchResultsID, resultBuffer);

            raymarchKernel.DispatchGroups(count, 1, 1);

            float[] results = new float[count * floatsPerRay];
            resultBuffer.GetData(results);

            requestsBuffer.Dispose();
            resultBuffer.Dispose();

            return results;
        }
    }
}