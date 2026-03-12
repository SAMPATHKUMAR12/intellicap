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
        [SerializeField] private float m_spawnDistance = 0.35f;
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

        [Header("Radius estimation")]
        [SerializeField] private float m_defaultSphereRadius = 0.25f;
        [SerializeField] private float m_radiusPadding = 1.15f;
        [SerializeField] private float m_minSphereRadius = 0.08f;
        [SerializeField] private float m_maxSphereRadius = 1.5f;

        [Header("Auto spawning")]
        [SerializeField] private bool m_autoSpawnEnabled = true;
        [SerializeField] private float m_autoSpawnInterval = 1.0f;
        private float m_autoSpawnTimer = 0f;

        [Header("Spawn confirmation")]
        [SerializeField] private int m_requiredConfirmations = 2;
        [SerializeField] private float m_candidateMatchDistance = 0.25f;
        [SerializeField] private float m_candidateLifetime = 0.8f;

        [Header("Startup stabilization")]
        [SerializeField] private float m_spawnWarmupSeconds = 1.5f;
        [SerializeField] private int m_requiredDetectionCyclesBeforeSpawn = 2;

        [Header("Sphere merging")]
        [SerializeField] private bool m_mergeOverlappingSpheres = true;
        [SerializeField] private float m_mergeOverlapFactor = 1.0f;
        [SerializeField] private float m_mergePadding = 1.05f;

        private float m_spawnEnableTime = 0f;
        private int m_validDetectionCycles = 0;

        private readonly List<GameObject> m_spawnedSpheres = new();
        private readonly List<PendingCandidate> m_pendingCandidates = new();

        private class PendingCandidate
        {
            public string ClassName;
            public Vector3 Position;
            public float EstimatedRadius;
            public int Confirmations;
            public float LastSeenTime;
        }

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

            m_spawnEnableTime = Time.time + m_spawnWarmupSeconds;
            m_validDetectionCycles = 0;
        }

        private void Update()
        {
            if (!m_isStarted)
            {
                if (m_cameraAccess != null && m_cameraAccess.IsPlaying && m_isSentisReady)
                {
                    m_isStarted = true;
                    m_isPaused = false;
                    m_spawnEnableTime = Time.time + m_spawnWarmupSeconds;
                    m_validDetectionCycles = 0;
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

            CleanupExpiredCandidates();

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

        private static bool SpheresOverlap(Vector3 c1, float r1, Vector3 c2, float r2, float factor = 1.0f)
        {
            float d = Vector3.Distance(c1, c2);
            return d < (r1 + r2) * factor;
        }

        private static void EncloseTwoSpheres(Vector3 c1, float r1, Vector3 c2, float r2, out Vector3 c, out float r)
        {
            float d = Vector3.Distance(c1, c2);

            // One sphere completely contains the other
            if (d <= Mathf.Abs(r2 - r1))
            {
                if (r2 >= r1)
                {
                    c = c2;
                    r = r2;
                }
                else
                {
                    c = c1;
                    r = r1;
                }
                return;
            }

            // General case
            r = (d + r1 + r2) * 0.5f;
            Vector3 dir = d > 0.0001f ? (c2 - c1) / d : Vector3.right;
            c = c1 + dir * (r - r1);
        }

        private bool IsDuplicateSphere(Vector3 newPosition, float newRadius)
        {
            foreach (var s in m_spawnedSpheres)
            {
                if (!s) continue;

                var info = s.GetComponent<CoverageSphereInfo>();
                if (info == null) continue;

                float existingRadius = info.Radius;
                float d = Vector3.Distance(s.transform.position, newPosition);

                float duplicateThreshold = Mathf.Min(
                    m_spawnDistance,
                    Mathf.Min(existingRadius, newRadius) * 0.5f);

                if (d < duplicateThreshold)
                    return true;
            }

            return false;
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

            Debug.Log($"[DetectionManager] Spawned sphere at {pos} radius={radius:F3}");
            return go;
        }

        private void SpawnOrMergeSphere(Vector3 newPos, float newRadius)
        {
            if (!m_mergeOverlappingSpheres)
            {
                SpawnSphere(newPos, newRadius);
                return;
            }

            List<GameObject> overlapping = new();

            for (int i = 0; i < m_spawnedSpheres.Count; i++)
            {
                var sphere = m_spawnedSpheres[i];
                if (!sphere) continue;

                var info = sphere.GetComponent<CoverageSphereInfo>();
                if (info == null) continue;

                if (SpheresOverlap(
                        sphere.transform.position,
                        info.Radius,
                        newPos,
                        newRadius,
                        m_mergeOverlapFactor))
                {
                    overlapping.Add(sphere);
                }
            }

            if (overlapping.Count == 0)
            {
                SpawnSphere(newPos, newRadius);
                return;
            }

            Vector3 mergedCenter = newPos;
            float mergedRadius = newRadius;

            for (int i = 0; i < overlapping.Count; i++)
            {
                var sphere = overlapping[i];
                if (!sphere) continue;

                var info = sphere.GetComponent<CoverageSphereInfo>();
                if (info == null) continue;

                EncloseTwoSpheres(
                    mergedCenter,
                    mergedRadius,
                    sphere.transform.position,
                    info.Radius,
                    out mergedCenter,
                    out mergedRadius);
            }

            mergedRadius *= m_mergePadding;

            // Option 2: if merged sphere would exceed max size, cancel merge
            if (mergedRadius > m_maxSphereRadius)
            {
                Debug.Log($"[SphereMerge] Cancelled merge: mergedRadius={mergedRadius:F3}, max={m_maxSphereRadius:F3}");
                SpawnSphere(newPos, newRadius);
                return;
            }

            mergedRadius = Mathf.Clamp(mergedRadius, m_minSphereRadius, m_maxSphereRadius);

            for (int i = 0; i < overlapping.Count; i++)
            {
                var sphere = overlapping[i];
                if (sphere == null) continue;

                m_spawnedSpheres.Remove(sphere);
                m_spawnedEntities.Remove(sphere);
                Destroy(sphere);
            }

            SpawnSphere(mergedCenter, mergedRadius);
        }

        private float EstimateSphereRadiusFromDetection(SentisInferenceUiManager.BoundingBox box, Vector3 worldPos)
        {
            var cam = Camera.main;
            if (cam == null || m_uiInference == null)
                return m_defaultSphereRadius;

            float depth = Vector3.Distance(cam.transform.position, worldPos);
            depth = Mathf.Max(depth, 0.05f);

            float bboxUiPx = Mathf.Max(box.Width, box.Height);

            Vector2 displayUiSize = m_uiInference.GetDisplaySize();
            float uiToScreen = cam.pixelHeight / Mathf.Max(1f, displayUiSize.y);
            float bboxScreenPx = bboxUiPx * uiToScreen;

            float fovYRad = cam.fieldOfView * Mathf.Deg2Rad;
            float focalPx = (0.5f * cam.pixelHeight) / Mathf.Tan(0.5f * fovYRad);

            float radiusPx = 0.5f * bboxScreenPx;
            float radiusMeters = (radiusPx * depth) / focalPx;

            radiusMeters *= m_radiusPadding;
            return Mathf.Clamp(radiusMeters, m_minSphereRadius, m_maxSphereRadius);
        }

        #endregion

        #region Candidate Confirmation

        private void CleanupExpiredCandidates()
        {
            for (int i = m_pendingCandidates.Count - 1; i >= 0; i--)
            {
                if (Time.time - m_pendingCandidates[i].LastSeenTime > m_candidateLifetime)
                    m_pendingCandidates.RemoveAt(i);
            }
        }

        private bool ConfirmAndMaybeSpawn(string className, Vector3 position, SentisInferenceUiManager.BoundingBox box)
        {
            float observedRadius = EstimateSphereRadiusFromDetection(box, position);

            PendingCandidate best = null;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < m_pendingCandidates.Count; i++)
            {
                var candidate = m_pendingCandidates[i];
                if (candidate.ClassName != className)
                    continue;

                float d = Vector3.Distance(candidate.Position, position);
                if (d < m_candidateMatchDistance && d < bestDistance)
                {
                    bestDistance = d;
                    best = candidate;
                }
            }

            if (best == null)
            {
                m_pendingCandidates.Add(new PendingCandidate
                {
                    ClassName = className,
                    Position = position,
                    EstimatedRadius = observedRadius,
                    Confirmations = 1,
                    LastSeenTime = Time.time
                });
                return false;
            }

            best.Position = Vector3.Lerp(best.Position, position, 0.4f);
            best.EstimatedRadius = Mathf.Lerp(best.EstimatedRadius, observedRadius, 0.4f);
            best.Confirmations++;
            best.LastSeenTime = Time.time;

            if (best.Confirmations < m_requiredConfirmations)
                return false;

            float finalRadius = best.EstimatedRadius;

            if (IsDuplicateSphere(best.Position, finalRadius))
            {
                m_pendingCandidates.Remove(best);
                return false;
            }

            SpawnOrMergeSphere(best.Position, finalRadius);
            m_pendingCandidates.Remove(best);
            return true;
        }

        #endregion

        #region Detection Placement

        private void CleanSpawnedObjectsCallback()
        {
            foreach (var entity in m_spawnedEntities)
            {
                if (entity != null)
                    Destroy(entity, 0.1f);
            }

            m_spawnedEntities.Clear();
            m_spawnedSpheres.Clear();
            m_pendingCandidates.Clear();
            m_validDetectionCycles = 0;
            m_spawnEnableTime = Time.time + m_spawnWarmupSeconds;
            OnObjectsIdentified?.Invoke(-1);
        }

        private void SpawnCurrentDetectedObjects()
        {
            if (m_uiInference == null || m_uiInference.BoxDrawn == null)
                return;

            if (Time.time < m_spawnEnableTime)
                return;

            int validBoxesThisCycle = 0;
            foreach (var box in m_uiInference.BoxDrawn)
            {
                if (box.WorldPos.HasValue)
                    validBoxesThisCycle++;
            }

            if (validBoxesThisCycle > 0)
                m_validDetectionCycles++;
            else
                m_validDetectionCycles = 0;

            if (m_validDetectionCycles < m_requiredDetectionCyclesBeforeSpawn)
                return;

            int count = 0;
            foreach (var box in m_uiInference.BoxDrawn)
            {
                if (ProcessDetection(box))
                    count++;
            }

            if (count > 0 && m_placeSound != null)
                m_placeSound.Play();

            OnObjectsIdentified?.Invoke(count);
        }

        private bool ProcessDetection(SentisInferenceUiManager.BoundingBox box)
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

            bool spawnSphere = false;

            if (m_scoreDB != null && m_scoreDB.IsReady && m_scoreDB.TryGetScore(className, out float score))
            {
                spawnSphere = score >= m_complexityThreshold;
                Debug.Log($"[SpawnCandidate] class={className} score={score:F1} allowed={spawnSphere}");
            }

            if (!spawnSphere || m_spherePrefab == null)
                return false;

            return ConfirmAndMaybeSpawn(className, position, box);
        }

        #endregion

        #region Public Functions

        public void OnPause(bool pause)
        {
            m_isPaused = pause;
        }

        #endregion
    }
}