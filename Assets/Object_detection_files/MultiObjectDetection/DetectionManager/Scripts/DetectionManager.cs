// Copyright (c) Meta Platforms, Inc. and affiliates.

using System.Collections;
using System.Collections.Generic;
using Meta.XR;
using Meta.XR.Samples;
using UnityEngine;
using UnityEngine.Events;

namespace PassthroughCameraSamples.MultiObjectDetection
{
    [MetaCodeSample("PassthroughCameraApiSamples-MultiObjectDetection")]
    public class DetectionManager : MonoBehaviour
    {
        [SerializeField] private PassthroughCameraAccess m_cameraAccess;

        [Header("Controls configuration")]
        [SerializeField] private OVRInput.RawButton m_actionButton = OVRInput.RawButton.A;

        [Header("UI references")]
        [SerializeField] private DetectionUiMenuManager m_uiMenuManager;

        [Header("Placement configuration")]
        [SerializeField] private float m_spawnDistance = 0.25f;
        [SerializeField] private AudioSource m_placeSound;

        [Header("Sentis inference refs")]
        [SerializeField] private SentisInferenceRunManager m_runInference;
        [SerializeField] private SentisInferenceUiManager m_uiInference;
        [Space(10)]
        public UnityEvent<int> OnObjectsIdentified;

        private bool m_isPaused = true;
        private readonly List<GameObject> m_spawnedEntities = new();
        private bool m_isStarted = false;
        private bool m_isSentisReady = false;
        private float m_delayPauseBackTime = 0f;

        [Header("Spawn Distance Limits")]
        [SerializeField] private float m_minSpawnDistanceFromCamera = 0.5f;
        [SerializeField] private float m_maxSpawnDistanceFromCamera = 5.0f;

        [Header("Complexity -> Sphere spawning")]
        [SerializeField] private ComplexityScoreDB m_scoreDB;
        [SerializeField] private GameObject m_spherePrefab;
        [SerializeField] private float m_complexityThreshold = 50f;
        [SerializeField] private float m_defaultSphereRadius = 0.25f;

        [Header("Sphere sizing from 2D bbox")]
        [SerializeField] private float m_spherePadding = 1.25f;
        [SerializeField] private float m_minSphereRadius = 0.10f;
        [SerializeField] private float m_maxSphereRadius = 2.00f;

        [Header("Auto spawning")]
        [SerializeField] private bool m_autoSpawnEnabled = true;
        [SerializeField] private float m_autoSpawnInterval = 0.5f;
        private float m_autoSpawnTimer = 0f;

        [Header("Sphere Merging")]
        [SerializeField] private bool m_mergeOverlaps = true;
        [SerializeField] private float m_mergePadding = 1.0f;
        [SerializeField] private float m_overlapEpsilon = 0.01f;

        private readonly List<GameObject> m_spawnedSpheres = new();

        #region Unity Functions

        private void Awake()
        {
            if (OVRManager.display != null)
                OVRManager.display.RecenteredPose += CleanSpawnedObjectsCallback;
        }

        private void OnDestroy()
        {
            if (OVRManager.display != null)
                OVRManager.display.RecenteredPose -= CleanSpawnedObjectsCallback;
        }

        private IEnumerator Start()
        {
            var sentisInference = m_runInference != null
                ? m_runInference
                : FindAnyObjectByType<SentisInferenceRunManager>();

            if (sentisInference == null)
            {
                Debug.LogError("[DetectionManager] SentisInferenceRunManager not found.");
                yield break;
            }

            while (!sentisInference.IsModelLoaded)
                yield return null;

            m_isSentisReady = true;
            m_isPaused = false;
            m_isStarted = true;
        }

        private void Update()
        {
            if (!m_isStarted)
            {
                if (m_cameraAccess != null && m_cameraAccess.IsPlaying && m_isSentisReady)
                {
                    m_isStarted = true;
                    m_isPaused = false;
                }
            }
            else
            {
                if (m_autoSpawnEnabled)
                {
                    m_autoSpawnTimer -= Time.deltaTime;
                    if (m_autoSpawnTimer <= 0f)
                    {
                        SpawnCurrentDetectedObjects();
                        m_autoSpawnTimer = m_autoSpawnInterval;
                    }
                }

                m_delayPauseBackTime -= Time.deltaTime;
                if (m_delayPauseBackTime < 0f)
                    m_delayPauseBackTime = 0f;
            }

            if (m_isPaused || m_cameraAccess == null || !m_cameraAccess.IsPlaying)
            {
                if (m_isPaused)
                    m_delayPauseBackTime = 0.1f;

                return;
            }

            if (m_runInference != null && !m_runInference.IsRunning())
                m_runInference.RunInference(m_cameraAccess);
        }

        #endregion

        #region Sphere Helpers

        private static void MidpointEnclosingSphere(Vector3 c1, float r1, Vector3 c2, float r2, out Vector3 c, out float r)
        {
            c = (c1 + c2) * 0.5f;
            float need1 = Vector3.Distance(c, c1) + r1;
            float need2 = Vector3.Distance(c, c2) + r2;
            r = Mathf.Max(need1, need2);
        }

        private static bool ContainsSphere(Vector3 cBig, float rBig, Vector3 cSmall, float rSmall, float eps = 0.001f)
        {
            float d = Vector3.Distance(cBig, cSmall);
            return (d + rSmall) <= (rBig - eps);
        }

        private static bool Overlaps(Vector3 c1, float r1, Vector3 c2, float r2, float eps)
        {
            float d = Vector3.Distance(c1, c2);
            return d < (r1 + r2 - eps);
        }

        private void SpawnOrMergeSphere(Vector3 newCenter, float newRadius)
        {
            foreach (var s in m_spawnedSpheres)
            {
                if (!s) continue;

                var info = s.GetComponent<CoverageSphereInfo>();
                if (info == null) continue;

                if (ContainsSphere(s.transform.position, info.Radius, newCenter, newRadius, 0.01f))
                    return;
            }

            int mergeIndex = -1;
            for (int i = 0; i < m_spawnedSpheres.Count; i++)
            {
                var s = m_spawnedSpheres[i];
                if (!s) continue;

                var info = s.GetComponent<CoverageSphereInfo>();
                if (info == null) continue;

                if (Overlaps(s.transform.position, info.Radius, newCenter, newRadius, m_overlapEpsilon))
                {
                    mergeIndex = i;
                    break;
                }
            }

            if (mergeIndex == -1)
            {
                SpawnSphere(newCenter, newRadius);
                return;
            }

            var baseSphere = m_spawnedSpheres[mergeIndex];
            if (baseSphere == null)
            {
                SpawnSphere(newCenter, newRadius);
                return;
            }

            var baseInfo = baseSphere.GetComponent<CoverageSphereInfo>();
            if (baseInfo == null)
            {
                baseInfo = baseSphere.AddComponent<CoverageSphereInfo>();
                baseInfo.Radius = newRadius;
            }

            Vector3 mergedCenter = baseSphere.transform.position;
            float mergedRadius = baseInfo.Radius;

            MidpointEnclosingSphere(mergedCenter, mergedRadius, newCenter, newRadius, out mergedCenter, out mergedRadius);

            for (int i = m_spawnedSpheres.Count - 1; i >= 0; i--)
            {
                if (i == mergeIndex) continue;

                var s = m_spawnedSpheres[i];
                if (!s)
                {
                    m_spawnedSpheres.RemoveAt(i);
                    continue;
                }

                var info = s.GetComponent<CoverageSphereInfo>();
                if (info == null) continue;

                if (Overlaps(mergedCenter, mergedRadius, s.transform.position, info.Radius, m_overlapEpsilon))
                {
                    MidpointEnclosingSphere(
                        mergedCenter,
                        mergedRadius,
                        s.transform.position,
                        info.Radius,
                        out mergedCenter,
                        out mergedRadius);

                    m_spawnedSpheres.RemoveAt(i);
                    m_spawnedEntities.Remove(s);
                    Destroy(s);
                }
            }

            mergedRadius *= m_mergePadding;
            mergedRadius = Mathf.Min(mergedRadius, m_maxSphereRadius);

            baseSphere.transform.SetPositionAndRotation(mergedCenter, Quaternion.identity);
            baseSphere.transform.localScale = Vector3.one * mergedRadius;
            baseInfo.Radius = mergedRadius;
        }

        private GameObject SpawnSphere(Vector3 pos, float radius)
        {
            if (m_spherePrefab == null)
            {
                Debug.LogWarning("[DetectionManager] Sphere prefab is missing.");
                return null;
            }

            var go = Instantiate(m_spherePrefab);
            go.transform.SetPositionAndRotation(pos, Quaternion.identity);
            go.transform.localScale = Vector3.one * radius;

            var info = go.GetComponent<CoverageSphereInfo>() ?? go.AddComponent<CoverageSphereInfo>();
            info.Radius = radius;

            m_spawnedSpheres.Add(go);
            m_spawnedEntities.Add(go);

            return go;
        }

        #endregion

        #region Detection Placement

        private void CleanSpawnedObjectsCallback()
        {
            foreach (var e in m_spawnedEntities)
            {
                if (e != null)
                    Destroy(e, 0.1f);
            }

            m_spawnedEntities.Clear();
            m_spawnedSpheres.Clear();
            OnObjectsIdentified?.Invoke(-1);
        }

        private void SpawnCurrentDetectedObjects()
        {
            if (m_uiInference == null || m_uiInference.BoxDrawn == null)
                return;

            int count = 0;
            foreach (var box in m_uiInference.BoxDrawn)
            {
                if (PlaceDetectionSphere(box))
                    count++;
            }

            if (count > 0 && m_placeSound != null)
                m_placeSound.Play();

            OnObjectsIdentified?.Invoke(count);
        }

        private bool PlaceDetectionSphere(SentisInferenceUiManager.BoundingBox box)
        {
            if (!box.WorldPos.HasValue)
                return false;

            Vector3 position = box.WorldPos.Value;
            var cam = Camera.main;

            if (cam != null)
            {
                float distanceToCamera = Vector3.Distance(cam.transform.position, position);

                if (distanceToCamera < m_minSpawnDistanceFromCamera)
                    return false;

                if (distanceToCamera > m_maxSpawnDistanceFromCamera)
                    return false;
            }

            string className = box.ClassName;

            foreach (var e in m_spawnedEntities)
            {
                if (!e) continue;

                if (Vector3.Distance(e.transform.position, position) < m_spawnDistance)
                    return false;
            }

            bool spawnSphere = false;

            if (m_scoreDB != null && m_scoreDB.IsReady && m_scoreDB.TryGetScore(className, out float score))
            {
                spawnSphere = score >= m_complexityThreshold;
                Debug.Log($"[Spawn] class={className} score={score:F1} spawnSphere={spawnSphere}");
            }

            if (!spawnSphere || m_spherePrefab == null)
                return false;

            float radius = m_defaultSphereRadius;

            if (cam != null && m_uiInference != null)
                radius = EstimateSphereRadiusFromUiBBox(cam, m_uiInference, box, position);

            if (m_mergeOverlaps)
                SpawnOrMergeSphere(position, radius);
            else
                SpawnSphere(position, radius);

            return true;
        }

        #endregion

        #region Public Functions

        public void OnPause(bool pause)
        {
            m_isPaused = pause;
        }

        #endregion

        #region Sphere Radius Estimation

        private float EstimateSphereRadiusFromUiBBox(
            Camera cam,
            SentisInferenceUiManager ui,
            SentisInferenceUiManager.BoundingBox box,
            Vector3 worldCenter)
        {
            float depth = Vector3.Distance(cam.transform.position, worldCenter);
            depth = Mathf.Max(depth, 0.05f);

            float bboxUiPx = Mathf.Max(box.Width, box.Height);

            Vector2 displayUiSize = ui.GetDisplaySize();
            float uiToScreen = cam.pixelHeight / Mathf.Max(1f, displayUiSize.y);
            float bboxScreenPx = bboxUiPx * uiToScreen;

            float fovYRad = cam.fieldOfView * Mathf.Deg2Rad;
            float focalPx = (0.5f * cam.pixelHeight) / Mathf.Tan(0.5f * fovYRad);

            float radiusPx = 0.5f * bboxScreenPx;
            float r = (radiusPx * depth) / focalPx;

            r *= m_spherePadding;
            r = Mathf.Clamp(r, m_minSphereRadius, m_maxSphereRadius);
            return r;
        }

        #endregion
    }
}