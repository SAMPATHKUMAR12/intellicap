// Copyright (c) Meta Platforms, Inc. and affiliates.

using System;
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

        [Header("Ui references")]
        [SerializeField] private DetectionUiMenuManager m_uiMenuManager;

        [Header("Placement configureation")]
        [SerializeField] private GameObject m_spwanMarker;
        [SerializeField] private EnvironmentRayCastSampleManager m_environmentRaycast;
        [SerializeField] private float m_spawnDistance = 0.25f;
        [SerializeField] private AudioSource m_placeSound;

        [Header("Sentis inference ref")]
        [SerializeField] private SentisInferenceRunManager m_runInference;
        [SerializeField] private SentisInferenceUiManager m_uiInference;
        [Space(10)]
        public UnityEvent<int> OnObjectsIdentified;

        private bool m_isPaused = true;
        private List<GameObject> m_spwanedEntities = new();
        private bool m_isStarted = false;
        private bool m_isSentisReady = false;
        private float m_delayPauseBackTime = 0;

        [Header("Spawn Distance Limits")]
        [SerializeField] private float m_minSpawnDistanceFromCamera = 0.5f;
        [SerializeField] private float m_maxSpawnDistanceFromCamera = 5.0f;



        [Header("Complexity -> Sphere spawning")]
        [SerializeField] private ComplexityScoreDB m_scoreDB;
        [SerializeField] private GameObject m_spherePrefab;
        [SerializeField] private float m_complexityThreshold = 50f;  // adjust
        [SerializeField] private float m_defaultSphereRadius = 0.25f;


        [Header("Sphere sizing from 2D bbox")]
        [SerializeField] private float m_spherePadding = 1.25f; // 1.1–1.5
        [SerializeField] private float m_minSphereRadius = 0.10f;
        [SerializeField] private float m_maxSphereRadius = 2.00f;

        [Header("Auto spawning")]
        [SerializeField] private bool m_autoSpawnEnabled = true;
        [SerializeField] private float m_autoSpawnInterval = 0.5f; // seconds (0.3–1.0 good)
        private float m_autoSpawnTimer = 0f;


        [Header("Sphere Merging")]
        [SerializeField] private bool m_mergeOverlaps = true;
        [SerializeField] private float m_mergePadding = 1.0f;   // slightly bigger sphere after merge
        [SerializeField] private float m_overlapEpsilon = 0.01f; // tolerance

        private readonly List<GameObject> m_spawnedSpheres = new();


        #region Unity Functions
        private void Awake() => OVRManager.display.RecenteredPose += CleanMarkersCallBack;

        private void OnDestroy() => OVRManager.display.RecenteredPose -= CleanMarkersCallBack;

        private IEnumerator Start()
        {
            // Wait until Sentis model is loaded
            var sentisInference = FindAnyObjectByType<SentisInferenceRunManager>();
            while (!sentisInference.IsModelLoaded)
            {
                yield return null;
            }
            m_isSentisReady = true;
            m_isPaused = false;   // auto-start inference
            m_isStarted = true;   // skip initial gating if you want

        }


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

// Minimal sphere enclosing two spheres
private static void EncloseTwoSpheres(Vector3 c1, float r1, Vector3 c2, float r2, out Vector3 c, out float r)
{
    float d = Vector3.Distance(c1, c2);

    // One contains the other
    if (d <= Mathf.Abs(r2 - r1))
    {
        if (r2 >= r1) { c = c2; r = r2; }
        else         { c = c1; r = r1; }
        return;
    }

    // General case
    r = (d + r1 + r2) * 0.5f;
    Vector3 dir = (c2 - c1) / d;
    c = c1 + dir * (r - r1);
}
private void SpawnOrMergeSphere(Vector3 newCenter, float newRadius)
{
    // 1) If it's already covered, ignore
    foreach (var s in m_spawnedSpheres)
    {
        if (!s) continue;
        var info = s.GetComponent<CoverageSphereInfo>();
        if (!info) continue;

        if (ContainsSphere(s.transform.position, info.Radius, newCenter, newRadius, 0.01f))
            return;
    }

    // 2) Find one overlapped sphere to merge into
    int mergeIndex = -1;
    for (int i = 0; i < m_spawnedSpheres.Count; i++)
    {
        var s = m_spawnedSpheres[i];
        if (!s) continue;

        var info = s.GetComponent<CoverageSphereInfo>();
        if (!info) continue;

        if (Overlaps(s.transform.position, info.Radius, newCenter, newRadius, m_overlapEpsilon))
        {
            mergeIndex = i;
            break;
        }
    }

    // 3) If no overlap: just spawn
    if (mergeIndex == -1)
    {
        SpawnSphere(newCenter, newRadius);
        return;
    }

    // 4) Merge new sphere + ALL overlapped into one (stored in mergedCenter/Radius)
    var baseSphere = m_spawnedSpheres[mergeIndex];
    var baseInfo = baseSphere.GetComponent<CoverageSphereInfo>();

    Vector3 mergedCenter = baseSphere.transform.position;
    float mergedRadius = baseInfo.Radius;

    // include new sphere
    MidpointEnclosingSphere(mergedCenter, mergedRadius, newCenter, newRadius, out mergedCenter, out mergedRadius);


    // include others that overlap this merged result
    for (int i = m_spawnedSpheres.Count - 1; i >= 0; i--)
    {
        if (i == mergeIndex) continue;

        var s = m_spawnedSpheres[i];
        if (!s) { m_spawnedSpheres.RemoveAt(i); continue; }

        var info = s.GetComponent<CoverageSphereInfo>();
        if (!info) continue;

        if (Overlaps(mergedCenter, mergedRadius, s.transform.position, info.Radius, m_overlapEpsilon))
        {
            MidpointEnclosingSphere(mergedCenter, mergedRadius, s.transform.position, info.Radius, out mergedCenter, out mergedRadius);

            m_spawnedSpheres.RemoveAt(i);
            m_spwanedEntities.Remove(s);
            Destroy(s);
        }
    }

    // 5) Make merged sphere slightly bigger
    mergedRadius *= m_mergePadding;

    // 6) Clamp AFTER padding (so it never exceeds max)
    mergedRadius = Mathf.Min(mergedRadius, m_maxSphereRadius);

    // 7) Apply final transform UPDATE
    baseSphere.transform.SetPositionAndRotation(mergedCenter, Quaternion.identity);
    baseSphere.transform.localScale = Vector3.one * mergedRadius; // your convention
    baseInfo.Radius = mergedRadius;


}



        private GameObject SpawnSphere(Vector3 pos, float radius)
        {
            var go = Instantiate(m_spherePrefab);
            go.transform.SetPositionAndRotation(pos, Quaternion.identity);

            // IMPORTANT: keep your same convention (no *2)
            go.transform.localScale = Vector3.one * radius;

            var info = go.GetComponent<CoverageSphereInfo>() ?? go.AddComponent<CoverageSphereInfo>();
            info.Radius = radius;

            m_spawnedSpheres.Add(go);
            m_spwanedEntities.Add(go); // optional: if you want CleanMarkersCallBack to destroy spheres too

            return go;
        }


        private void Update()
        {
            if (!m_isStarted)
            {
                // Manage the Initial Ui Menu
                if (m_cameraAccess.IsPlaying && m_isSentisReady)
                {
                    m_isStarted = true;
                    m_isPaused = false; // ✅ auto start inference

                }
            }
            else
            {
                // Press A button to spawn 3d markers
            if (m_autoSpawnEnabled)
            {
                m_autoSpawnTimer -= Time.deltaTime;
                if (m_autoSpawnTimer <= 0f)
                {
                    SpwanCurrentDetectedObjects();
                    m_autoSpawnTimer = m_autoSpawnInterval;
                }
            }

                // Cooldown for the A button after return from the pause menu
                m_delayPauseBackTime -= Time.deltaTime;
                if (m_delayPauseBackTime <= 0)
                {
                    m_delayPauseBackTime = 0;
                }
            }

            // Don't start Sentis inference if the app is paused or we don't have a camera image yet
            if (m_isPaused || !m_cameraAccess.IsPlaying)
            {
                if (m_isPaused)
                {
                    // Set the delay time for the A button to return from the pause menu
                    m_delayPauseBackTime = 0.1f;
                }
                return;
            }

            // Run a new inference when the current inference finishes
            if (!m_runInference.IsRunning())
            {
                m_runInference.RunInference(m_cameraAccess);
            }
        }
        #endregion

        #region Marker Functions
        /// <summary>
        /// Clean 3d markers when the tracking space is re-centered.
        /// </summary>
        private void CleanMarkersCallBack()
        {
            foreach (var e in m_spwanedEntities)
            {
                Destroy(e, 0.1f);
            }
            m_spwanedEntities.Clear();
            OnObjectsIdentified?.Invoke(-1);
        }
        /// <summary>
        /// Spwan 3d markers for the detected objects
        /// </summary>
        private void SpwanCurrentDetectedObjects()
        {
            var count = 0;
            foreach (var box in m_uiInference.BoxDrawn)
            {
                if (PlaceMarkerUsingEnvironmentRaycast(box))
                    count++;
            }

            if (count > 0) m_placeSound.Play();
            OnObjectsIdentified?.Invoke(count);
        }


        /// <summary>
        /// Place a marker using the environment raycast
        /// </summary>
private bool PlaceMarkerUsingEnvironmentRaycast(SentisInferenceUiManager.BoundingBox box)
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

    // --- simple duplicate check (distance-only)
    foreach (var e in m_spwanedEntities)
    {
        if (!e) continue;
        if (Vector3.Distance(e.transform.position, position) < m_spawnDistance)
            return false;
    }

    // Decide marker vs sphere based on CSV complexity
    bool spawnSphere = false;

    if (m_scoreDB != null && m_scoreDB.IsReady && m_scoreDB.TryGetScore(className, out float score))
    {
        spawnSphere = score >= m_complexityThreshold;
        Debug.Log($"[Spawn] class={className} score={score:F1} spawnSphere={spawnSphere}");
    }

    GameObject go;

if (spawnSphere && m_spherePrefab != null)
{
    
    float radius = m_defaultSphereRadius;

    if (cam != null && m_uiInference != null)
        radius = EstimateSphereRadiusFromUiBBox(cam, m_uiInference, box, position);

    if (m_mergeOverlaps)
        SpawnOrMergeSphere(position, radius);
    else
        SpawnSphere(position, radius);

    return true;
}

    else
    {
        go = Instantiate(m_spwanMarker);
        go.transform.SetPositionAndRotation(position, Quaternion.identity);
        go.GetComponent<DetectionSpawnMarkerAnim>().SetYoloClassName(className);
    }

    m_spwanedEntities.Add(go);
    return true;
}

        #endregion

        #region Public Functions
        /// <summary>
        /// Pause the detection logic when the pause menu is active
        /// </summary>
        public void OnPause(bool pause)
        {
            m_isPaused = pause;
        }
        #endregion

        private float EstimateSphereRadiusFromUiBBox(
            Camera cam,
            SentisInferenceUiManager ui,
            SentisInferenceUiManager.BoundingBox box,
            Vector3 worldCenter)
        {
            // 1) distance to object
            float depth = Vector3.Distance(cam.transform.position, worldCenter);
            depth = Mathf.Max(depth, 0.05f);

            // 2) bbox diameter in UI pixels (RawImage space)
            float bboxUiPx = Mathf.Max(box.Width, box.Height);

            // 3) map UI pixels -> screen pixels
            Vector2 displayUiSize = ui.GetDisplaySize();           // e.g. 1000 x 700 (UI)
            float uiToScreen = cam.pixelHeight / Mathf.Max(1f, displayUiSize.y);
            float bboxScreenPx = bboxUiPx * uiToScreen;

            // 4) pinhole conversion: radiusMeters = (radiusPx * depth) / focalPx
            float fovYRad = cam.fieldOfView * Mathf.Deg2Rad;
            float focalPx = (0.5f * cam.pixelHeight) / Mathf.Tan(0.5f * fovYRad);

            float radiusPx = 0.5f * bboxScreenPx;
            float r = (radiusPx * depth) / focalPx;

            // padding + clamp
            r *= m_spherePadding;
            r = Mathf.Clamp(r, m_minSphereRadius, m_maxSphereRadius);
            return r;
        }







    }
}

