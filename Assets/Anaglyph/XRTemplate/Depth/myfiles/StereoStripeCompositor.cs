using UnityEngine;

public class StereoStripeCompositor : MonoBehaviour
{
    [SerializeField] private StripePatternRenderer stripeRenderer;
    [SerializeField] private StereoScannedMaskRenderer maskRenderer;
    [SerializeField] private Material stereoCompositeMat;

    private static readonly int StripeTexID = Shader.PropertyToID("_StripeTex");
    private static readonly int MaskTexLeftID = Shader.PropertyToID("_MaskTexLeft");
    private static readonly int MaskTexRightID = Shader.PropertyToID("_MaskTexRight");

    private void Update()
    {
        if (stripeRenderer == null || maskRenderer == null || stereoCompositeMat == null)
            return;

        if (stripeRenderer.StripeRT != null)
            stereoCompositeMat.SetTexture(StripeTexID, stripeRenderer.StripeRT);

        if (maskRenderer.MaskLeftRT != null)
            stereoCompositeMat.SetTexture(MaskTexLeftID, maskRenderer.MaskLeftRT);

        if (maskRenderer.MaskRightRT != null)
            stereoCompositeMat.SetTexture(MaskTexRightID, maskRenderer.MaskRightRT);
    }
}