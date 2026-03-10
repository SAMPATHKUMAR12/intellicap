using UnityEngine;

public class StripePatternRenderer : MonoBehaviour
{
    [SerializeField] private Material stripeMaterial;
    [SerializeField] private int width = 1024;
    [SerializeField] private int height = 1024;

    public RenderTexture StripeRT { get; private set; }

    private void OnEnable()
    {
        CreateRT();
        RenderStripes();
    }

    private void CreateRT()
    {
        if (StripeRT != null) return;

        StripeRT = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        StripeRT.wrapMode = TextureWrapMode.Repeat;
        StripeRT.filterMode = FilterMode.Bilinear;
        StripeRT.Create();
    }

    [ContextMenu("Render Stripes")]
    public void RenderStripes()
    {
        if (StripeRT == null) CreateRT();
        Graphics.Blit(null, StripeRT, stripeMaterial);
    }
}