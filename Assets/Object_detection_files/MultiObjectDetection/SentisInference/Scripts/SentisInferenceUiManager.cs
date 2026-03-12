// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections.Generic;
using Meta.XR;
using Meta.XR.Samples;
using Unity.InferenceEngine;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class SentisInferenceUiManager : MonoBehaviour
    {
        [Header("Placement configureation")]
        [SerializeField] private EnvironmentRayCastSampleManager m_environmentRaycast;
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("Multi-ray world position sampling")]
        [SerializeField] private bool m_useMultiRaySampling = true;
        [SerializeField] private float m_innerSampleFactor = 0.25f;

        [Header("UI display references")]
        [SerializeField] private SentisObjectDetectedUiManager m_detectionCanvas;
        [SerializeField] private RawImage m_displayImage;
        [SerializeField] private Sprite m_boxTexture;
        [SerializeField] private Color m_boxColor;
        [SerializeField] private Font m_font;
        [SerializeField] private Color m_fontColor;
        [SerializeField] private int m_fontSize = 80;
        [Space(10)]
        public UnityEvent<int> OnObjectsDetected;

        public List<BoundingBox> BoxDrawn = new();

        private string[] m_labels;
        private readonly List<GameObject> m_boxPool = new();
        private Transform m_displayLocation;

        public struct BoundingBox
        {
            public float CenterX;
            public float CenterY;
            public float Width;
            public float Height;
            public string Label;
            public Vector3? WorldPos;
            public string ClassName;
        }

        public Vector2 GetDisplaySize()
        {
            return m_displayImage != null
                ? m_displayImage.rectTransform.rect.size
                : Vector2.one;
        }

        public Texture GetDisplayTexture()
        {
            return m_displayImage != null ? m_displayImage.texture : null;
        }

        #region Unity Functions

        private void Start()
        {
            if (m_displayImage != null)
                m_displayLocation = m_displayImage.transform;
        }

        #endregion

        #region Detection Functions

        public void OnObjectDetectionError()
        {
            ClearAnnotations();
            OnObjectsDetected?.Invoke(0);
        }

        #endregion

        #region BoundingBoxes functions

        public void SetLabels(TextAsset labelsAsset)
        {
            if (labelsAsset == null)
            {
                Debug.LogError("[SentisInferenceUiManager] Labels asset is null.");
                m_labels = new string[0];
                return;
            }

            m_labels = labelsAsset.text.Split('\n');
        }

        public void SetDetectionCapture(Texture image)
        {
            if (m_displayImage != null)
                m_displayImage.texture = image;

            if (m_detectionCanvas != null)
                m_detectionCanvas.CapturePosition();
        }

        public void DrawUIBoxes(Tensor<float> output, Tensor<int> labelIDs, float imageWidth, float imageHeight, Pose cameraPose)
        {
            if (m_detectionCanvas != null)
                m_detectionCanvas.UpdatePosition();

            ClearAnnotations();

            if (m_displayImage == null)
            {
                Debug.LogWarning("[SentisInferenceUiManager] Display image is missing.");
                OnObjectsDetected?.Invoke(0);
                return;
            }

            if (m_cameraAccess == null)
            {
                Debug.LogWarning("[SentisInferenceUiManager] Camera access is missing.");
                OnObjectsDetected?.Invoke(0);
                return;
            }

            if (m_environmentRaycast == null)
            {
                Debug.LogWarning("[SentisInferenceUiManager] Environment raycast manager is missing.");
                OnObjectsDetected?.Invoke(0);
                return;
            }

            float displayWidth = m_displayImage.rectTransform.rect.width;
            float displayHeight = m_displayImage.rectTransform.rect.height;

            int boxesFound = output.shape[0];
            if (boxesFound <= 0)
            {
                OnObjectsDetected?.Invoke(0);
                return;
            }

            int maxBoxes = Mathf.Min(boxesFound, 200);
            OnObjectsDetected?.Invoke(maxBoxes);

            for (int n = 0; n < maxBoxes; n++)
            {
                float normalizedCenterX = output[n, 0] / imageWidth;
                float normalizedCenterY = output[n, 1] / imageHeight;

                float normalizedWidth = output[n, 2] / imageWidth;
                float normalizedHeight = output[n, 3] / imageHeight;

                float centerX = displayWidth * (normalizedCenterX - 0.5f);
                float centerY = displayHeight * (normalizedCenterY - 0.5f);

                string className = GetClassName(labelIDs[n]);

                Vector3? worldPos = m_useMultiRaySampling
                    ? SampleWorldPosFromBBox9(normalizedCenterX, normalizedCenterY, normalizedWidth, normalizedHeight, cameraPose)
                    : SampleSingleCenterWorldPos(normalizedCenterX, normalizedCenterY, cameraPose);

                var box = new BoundingBox
                {
                    CenterX = centerX,
                    CenterY = centerY,
                    ClassName = className,
                    Width = output[n, 2] * (displayWidth / imageWidth),
                    Height = output[n, 3] * (displayHeight / imageHeight),
                    Label = $"Id: {n} Class: {className} Center (px): {(int)centerX},{(int)centerY} Center (%): {normalizedCenterX:0.00},{normalizedCenterY:0.00}",
                    WorldPos = worldPos,
                };

                BoxDrawn.Add(box);

                // No visible 2D boxes
                // DrawBox(box, n);
            }
        }

        private Vector3? SampleSingleCenterWorldPos(float normalizedCenterX, float normalizedCenterY, Pose cameraPose)
        {
            var ray = m_cameraAccess.ViewportPointToRay(
                new Vector2(
                    Mathf.Clamp01(normalizedCenterX),
                    Mathf.Clamp01(1.0f - normalizedCenterY)),
                cameraPose);

            return m_environmentRaycast.Raycast(ray);
        }

        private Vector3? SampleWorldPosFromBBox9(
            float normalizedCenterX,
            float normalizedCenterY,
            float normalizedWidth,
            float normalizedHeight,
            Pose cameraPose)
        {
            float dx = normalizedWidth * m_innerSampleFactor;
            float dy = normalizedHeight * m_innerSampleFactor;

            Vector2[] samplePoints =
            {
                new Vector2(normalizedCenterX, normalizedCenterY),           // center
                new Vector2(normalizedCenterX - dx, normalizedCenterY),      // left
                new Vector2(normalizedCenterX + dx, normalizedCenterY),      // right
                new Vector2(normalizedCenterX, normalizedCenterY - dy),      // top
                new Vector2(normalizedCenterX, normalizedCenterY + dy),      // bottom
                new Vector2(normalizedCenterX - dx, normalizedCenterY - dy), // top-left
                new Vector2(normalizedCenterX + dx, normalizedCenterY - dy), // top-right
                new Vector2(normalizedCenterX - dx, normalizedCenterY + dy), // bottom-left
                new Vector2(normalizedCenterX + dx, normalizedCenterY + dy)  // bottom-right
            };

            Vector3? bestHit = null;
            float bestDistance = float.MaxValue;
            Vector3 cameraPosition = cameraPose.position;

            for (int i = 0; i < samplePoints.Length; i++)
            {
                float vx = Mathf.Clamp01(samplePoints[i].x);
                float vy = Mathf.Clamp01(samplePoints[i].y);

                var ray = m_cameraAccess.ViewportPointToRay(
                    new Vector2(vx, 1.0f - vy),
                    cameraPose);

                Vector3? hit = m_environmentRaycast.Raycast(ray);
                if (!hit.HasValue)
                    continue;

                float d = Vector3.Distance(cameraPosition, hit.Value);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    bestHit = hit.Value;
                }
            }

            return bestHit;
        }

        private string GetClassName(int labelId)
        {
            if (m_labels == null || m_labels.Length == 0)
                return $"class_{labelId}";

            if (labelId < 0 || labelId >= m_labels.Length)
                return $"class_{labelId}";

            return m_labels[labelId].Replace(" ", "_").Trim();
        }

        private void ClearAnnotations()
        {
            foreach (var box in m_boxPool)
            {
                if (box != null)
                    box.SetActive(false);
            }

            BoxDrawn.Clear();
        }

        private void DrawBox(BoundingBox box, int id)
        {
        }

        private GameObject CreateNewBox(Color color)
        {
            if (m_displayLocation == null)
                return null;

            var panel = new GameObject("ObjectBox");
            _ = panel.AddComponent<CanvasRenderer>();

            var img = panel.AddComponent<Image>();
            img.color = color;
            img.sprite = m_boxTexture;
            img.type = Image.Type.Sliced;
            img.fillCenter = false;

            panel.transform.SetParent(m_displayLocation, false);

            var text = new GameObject("ObjectLabel");
            _ = text.AddComponent<CanvasRenderer>();
            text.transform.SetParent(panel.transform, false);

            var txt = text.AddComponent<Text>();
            txt.font = m_font;
            txt.color = m_fontColor;
            txt.fontSize = m_fontSize;
            txt.horizontalOverflow = HorizontalWrapMode.Overflow;

            var rt2 = text.GetComponent<RectTransform>();
            rt2.offsetMin = new Vector2(20, rt2.offsetMin.y);
            rt2.offsetMax = new Vector2(0, rt2.offsetMax.y);
            rt2.offsetMin = new Vector2(rt2.offsetMin.x, 0);
            rt2.offsetMax = new Vector2(rt2.offsetMax.x, 30);
            rt2.anchorMin = new Vector2(0, 0);
            rt2.anchorMax = new Vector2(1, 1);

            panel.SetActive(false);
            m_boxPool.Add(panel);
            return panel;
        }

        #endregion
    }
}