using UnityEngine;

public class StripeOverlayController : MonoBehaviour
{
    public OVROverlay overlay;
    public StripePatternRenderer stripeRenderer;

    void Start()
    {
        if (overlay == null)
            overlay = GetComponent<OVROverlay>();
    }

    void LateUpdate()
    {
        if (overlay == null || stripeRenderer == null)
            return;

        overlay.textures[0] = stripeRenderer.StripeRT;
    }
}