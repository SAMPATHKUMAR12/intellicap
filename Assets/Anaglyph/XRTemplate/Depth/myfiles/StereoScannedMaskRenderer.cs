using UnityEngine;
using UnityEngine.XR;
using Anaglyph.XRTemplate;

public class StereoScannedMaskRenderer : MonoBehaviour
{
    [SerializeField] private ComputeShader maskCompute;
    [SerializeField] private EnvironmentMapper mapper;
    [SerializeField] private Camera xrCamera;

    [SerializeField] private int maskWidth = 256;
    [SerializeField] private int maskHeight = 256;
    [SerializeField] private float maxDistance = 7f;
    [SerializeField] private float updatesPerSecond = 2f;
    [SerializeField] private float coverageThreshold = 0.2f;

    public RenderTexture MaskLeftRT { get; private set; }
    public RenderTexture MaskRightRT { get; private set; }

    private int kernel;
    private float nextUpdate;

    private void Start()
    {
        if (mapper == null)
            mapper = EnvironmentMapper.Instance;

        if (xrCamera == null)
            xrCamera = Camera.main;

        if (maskCompute == null || mapper == null || xrCamera == null)
        {
            Debug.LogError("[StereoScannedMaskRenderer] Missing references.");
            enabled = false;
            return;
        }

        kernel = maskCompute.FindKernel("RenderMask");
        MaskLeftRT = CreateRT("MaskLeftRT");
        MaskRightRT = CreateRT("MaskRightRT");
    }

    private RenderTexture CreateRT(string rtName)
    {
        var rt = new RenderTexture(maskWidth, maskHeight, 0, RenderTextureFormat.RFloat);
        rt.name = rtName;
        rt.enableRandomWrite = true;
        rt.filterMode = FilterMode.Bilinear;
        rt.wrapMode = TextureWrapMode.Clamp;
        rt.Create();
        return rt;
    }

    private void Update()
    {
        if (Time.time < nextUpdate)
            return;

        nextUpdate = Time.time + 1f / Mathf.Max(0.1f, updatesPerSecond);

        if (mapper == null || mapper.CoverageTexture == null || xrCamera == null)
            return;

        Matrix4x4 leftView = xrCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Left);
        Matrix4x4 rightView = xrCamera.GetStereoViewMatrix(Camera.StereoscopicEye.Right);

        Matrix4x4 leftProj = GL.GetGPUProjectionMatrix(
            xrCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left), false);
        Matrix4x4 rightProj = GL.GetGPUProjectionMatrix(
            xrCamera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right), false);

        RenderEyeMask(leftView.inverse, leftProj.inverse, MaskLeftRT);
        RenderEyeMask(rightView.inverse, rightProj.inverse, MaskRightRT);
    }

    private void RenderEyeMask(Matrix4x4 viewInv, Matrix4x4 projInv, RenderTexture target)
    {
        maskCompute.SetTexture(kernel, "coverageVolume", mapper.CoverageTexture);
        maskCompute.SetTexture(kernel, "maskOut", target);

        Vector3Int res = mapper.VolumeResolution;
        maskCompute.SetInts("volumeSize", res.x, res.y, res.z);
        maskCompute.SetFloat("metersPerVoxel", mapper.MetersPerVoxel);
        maskCompute.SetFloat("maxDistance", maxDistance);
        maskCompute.SetFloat("coverageThreshold", coverageThreshold);
        maskCompute.SetInt("maskWidth", maskWidth);
        maskCompute.SetInt("maskHeight", maskHeight);
        maskCompute.SetMatrix("eyeViewInv", viewInv);
        maskCompute.SetMatrix("eyeProjInv", projInv);
        maskCompute.SetVector("volumeCenterWS", mapper.transform.position);

        int gx = Mathf.CeilToInt(maskWidth / 8f);
        int gy = Mathf.CeilToInt(maskHeight / 8f);
        maskCompute.Dispatch(kernel, gx, gy, 1);
    }
}