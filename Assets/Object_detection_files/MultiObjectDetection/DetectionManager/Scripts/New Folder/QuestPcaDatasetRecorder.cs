using System;
using System.Globalization;
using System.IO;
using System.Diagnostics;            // NEW
using Unity.Collections;
using UnityEngine;
using Meta.XR; // PassthroughCameraAccess
using Debug = UnityEngine.Debug;

public class QuestPcaDatasetRecorder : MonoBehaviour
{
    [Header("Inputs")]
    public PassthroughCameraAccess pca;

    [Header("Recording Control")]
    public OVRInput.RawButton recordToggleButton = OVRInput.RawButton.A;
    public bool startRecordingOnPlay = false;

    [Header("Capture Rate")]
    [Tooltip("Time-based capture. 0.25 = 4 FPS. Set <=0 to disable and use motion thresholds only.")]
    public float captureIntervalSec = 0.25f;

    [Tooltip("Capture only if moved more than this distance since last saved frame (meters). Set <=0 to disable.")]
    public float minMoveMeters = 0.06f; // 6 cm

    [Tooltip("Capture only if rotated more than this angle since last saved frame (degrees). Set <=0 to disable.")]
    public float minRotateDeg = 6f;

    [Header("Image Encoding")]
    [Range(1, 100)] public int jpgQuality = 85;

    [Header("Debug")]
    public bool logToConsole = true;

    public RecordingHUD hud;

    // =========================
    // NEW: Performance metrics
    // =========================
    [Header("Recording Metrics")]
    [Tooltip("Assumed display refresh rate for stutter detection. Quest is often 72/80/90/120 depending on your project/device mode.")]
    public float assumedDisplayHz = 72f;

    [Tooltip("Count a stutter if dt > (stutterMultiplier / Hz). 2.0 means >2 frames worth of time.")]
    public float stutterMultiplier = 2f;

    private float _stutterThresholdSec;
    private int _unityFramesDuringRecording;
    private int _stutterFrames;
    private float _worstDeltaTimeSec;

    private int _savedFrames;
    private double _sumSaveMs;
    private double _worstSaveMs;

    private float _recordStartRealtime;
    private bool _metricsActive;

    // Session paths
    private string _sessionDir;
    private string _imagesDir;
    private string _posesCsvPath;
    private string _intrinsicsJsonPath;

    // Runtime state
    private bool _isRecording;
    private int _frameIndex;
    private float _nextAllowedTime;
    private Pose _lastSavedPose;
    private bool _hasLastSavedPose;

    // Reused encoding texture
    private Texture2D _encodeTex;

    void Start()
    {
        if (!pca)
        {
            Debug.LogError("QuestPcaDatasetRecorder: PassthroughCameraAccess reference is missing.");
            enabled = false;
            return;
        }

        CreateNewSession();

        if (startRecordingOnPlay)
        {
            SetRecording(true);
        }
    }

    void Update()
    {
        // Toggle recording
        if (OVRInput.GetDown(recordToggleButton))
        {
            SetRecording(!_isRecording);
        }

        // NEW: Measure runtime stutter while recording
        if (_isRecording && _metricsActive)
        {
            float dt = Time.deltaTime;
            _unityFramesDuringRecording++;
            if (dt > _worstDeltaTimeSec) _worstDeltaTimeSec = dt;
            if (dt > _stutterThresholdSec) _stutterFrames++;
        }

        if (!_isRecording)
            return;

        if (!pca.IsPlaying)
            return;

        // Only attempt capture when a new camera frame arrived this Unity frame
        if (!pca.IsUpdatedThisFrame)
            return;

        // Gate by time interval
        if (captureIntervalSec > 0f && Time.time < _nextAllowedTime)
            return;

        // Gate by motion thresholds (based on camera pose at that frame)
        Pose camPose = pca.GetCameraPose();
        if (_hasLastSavedPose)
        {
            float d = Vector3.Distance(camPose.position, _lastSavedPose.position);
            float a = Quaternion.Angle(camPose.rotation, _lastSavedPose.rotation);

            // NOTE: Your logic requires BOTH thresholds to be satisfied to skip (AND).
            // That’s okay: it means if either motion OR rotation is large enough you capture.
            if (minMoveMeters > 0f && d < minMoveMeters &&
                minRotateDeg > 0f && a < minRotateDeg)
            {
                return;
            }
        }

        // Passed gates -> capture
        CaptureFrame(camPose);

        _lastSavedPose = camPose;
        _hasLastSavedPose = true;

        if (captureIntervalSec > 0f)
            _nextAllowedTime = Time.time + captureIntervalSec;
    }

    private void SetRecording(bool on)
    {
        if (_isRecording == on) return;

        _isRecording = on;

        if (hud) hud.SetRecording(on);

        if (on)
        {
            // NEW: Reset metrics for this run
            _recordStartRealtime = Time.realtimeSinceStartup;
            _unityFramesDuringRecording = 0;
            _stutterFrames = 0;
            _worstDeltaTimeSec = 0f;

            _savedFrames = 0;
            _sumSaveMs = 0;
            _worstSaveMs = 0;

            _stutterThresholdSec = stutterMultiplier / Mathf.Max(1f, assumedDisplayHz);
            _metricsActive = true;

            if (logToConsole)
                Debug.Log($"[Recorder] Recording ON → {_sessionDir} (stutter threshold > {_stutterThresholdSec * 1000f:F1} ms)");

            // When recording starts, write intrinsics once (or overwrite)
            WriteIntrinsicsJson();
        }
        else
        {
            // NEW: Print metrics summary
            PrintMetricsSummary();

            _metricsActive = false;

            if (logToConsole)
                Debug.Log("[Recorder] Recording OFF");
        }
    }

    private void PrintMetricsSummary()
    {
        float elapsed = Time.realtimeSinceStartup - _recordStartRealtime;

        double expected = 0;
        if (captureIntervalSec > 0f)
            expected = elapsed / captureIntervalSec;
        else
            expected = _savedFrames;

        double dropPct = 0;
        if (captureIntervalSec > 0f && expected > 1e-6)
            dropPct = (1.0 - (_savedFrames / expected)) * 100.0;

        double avgSaveMs = (_savedFrames > 0) ? (_sumSaveMs / _savedFrames) : 0.0;

        string msg =
            "[REC METRICS]\n" +
            $"Elapsed: {elapsed:F2}s\n" +
            $"Unity frames: {_unityFramesDuringRecording}, stutter frames: {_stutterFrames} " +
            $"(threshold>{_stutterThresholdSec * 1000.0:F1}ms), worst dt: {_worstDeltaTimeSec * 1000.0:F1}ms\n" +
            $"Saved frames: {_savedFrames}, expected≈{expected:F1}, drop≈{dropPct:F1}% (interval={captureIntervalSec:F2}s)\n" +
            $"Save time: avg={avgSaveMs:F1}ms, worst={_worstSaveMs:F1}ms\n" +
            $"Output: {_sessionDir}";

        Debug.Log(msg);

        
    }

    private void CreateNewSession()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _sessionDir = Path.Combine(Application.persistentDataPath, "QuestCapture_" + stamp);
        _imagesDir = Path.Combine(_sessionDir, "images");
        Directory.CreateDirectory(_imagesDir);

        _posesCsvPath = Path.Combine(_sessionDir, "poses.csv");
        _intrinsicsJsonPath = Path.Combine(_sessionDir, "intrinsics.json");

        File.WriteAllText(_posesCsvPath, "frame_id,timestamp_iso,pos_x,pos_y,pos_z,rot_x,rot_y,rot_z,rot_w\n");

        if (logToConsole)
            Debug.Log($"[Recorder] Session created: {_sessionDir}");
    }

    private void EnsureEncodeTexture(int w, int h)
    {
        if (_encodeTex != null && _encodeTex.width == w && _encodeTex.height == h)
            return;

        if (_encodeTex != null)
            Destroy(_encodeTex);

        _encodeTex = new Texture2D(w, h, TextureFormat.RGBA32, false);
    }

    private void CaptureFrame(Pose camPose)
    {
        // 1) Get CPU colors for latest camera image (do not cache this NativeArray!)
        NativeArray<Color32> colors = pca.GetColors();
        if (!colors.IsCreated || colors.Length == 0)
            return;

        int w = pca.CurrentResolution.x;
        int h = pca.CurrentResolution.y;
        EnsureEncodeTexture(w, h);

        // Copy into our own Texture2D then encode
        _encodeTex.SetPixelData(colors, 0);
        _encodeTex.Apply(false, false);

        string fileName = $"frame_{_frameIndex:D6}.jpg";
        string imgPath = Path.Combine(_imagesDir, fileName);

        // NEW: Measure encode+write time
        var sw = Stopwatch.StartNew();
        byte[] jpg = _encodeTex.EncodeToJPG(jpgQuality);
        File.WriteAllBytes(imgPath, jpg);
        sw.Stop();

        double saveMs = sw.Elapsed.TotalMilliseconds;
        _sumSaveMs += saveMs;
        if (saveMs > _worstSaveMs) _worstSaveMs = saveMs;

        // 2) Log pose (pose corresponds to pca.Timestamp)
        AppendPoseCsv(_frameIndex, pca.Timestamp, camPose);

        _frameIndex++;
        _savedFrames++;

        if (logToConsole && (_frameIndex % 30 == 0))
            Debug.Log($"[Recorder] Saved {_frameIndex} frames... (last save {saveMs:F1} ms)");
    }

    private void AppendPoseCsv(int frameId, DateTime timestamp, Pose pose)
    {
        string ts = timestamp.ToString("O", CultureInfo.InvariantCulture);

        Vector3 p = pose.position;
        Quaternion q = pose.rotation;

        string line = string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2:F6},{3:F6},{4:F6},{5:F6},{6:F6},{7:F6},{8:F6}\n",
            frameId, ts, p.x, p.y, p.z, q.x, q.y, q.z, q.w);

        File.AppendAllText(_posesCsvPath, line);
    }

    private void WriteIntrinsicsJson()
    {
        if (!pca.IsPlaying)
        {
            if (logToConsole) Debug.LogWarning("[Recorder] PCA not playing yet; intrinsics will be written after it starts.");
            return;
        }

        var intr = pca.Intrinsics;
        Vector2Int img = pca.CurrentResolution;

        Vector2 sensorRes = intr.SensorResolution;
        Vector2 currentRes = new Vector2(img.x, img.y);

        Vector2 scaleFactor = currentRes / sensorRes;
        scaleFactor /= Mathf.Max(scaleFactor.x, scaleFactor.y);

        float cropX = sensorRes.x * (1f - scaleFactor.x) * 0.5f;
        float cropY = sensorRes.y * (1f - scaleFactor.y) * 0.5f;
        float cropW = sensorRes.x * scaleFactor.x;
        float cropH = sensorRes.y * scaleFactor.y;

        float fx = intr.FocalLength.x * img.x / cropW;
        float fy = intr.FocalLength.y * img.y / cropH;
        float cx = (intr.PrincipalPoint.x - cropX) * img.x / cropW;
        float cy = (intr.PrincipalPoint.y - cropY) * img.y / cropH;

        string json =
            "{\n" +
            $"  \"width\": {img.x},\n" +
            $"  \"height\": {img.y},\n" +
            $"  \"fx\": {fx.ToString("F6", CultureInfo.InvariantCulture)},\n" +
            $"  \"fy\": {fy.ToString("F6", CultureInfo.InvariantCulture)},\n" +
            $"  \"cx\": {cx.ToString("F6", CultureInfo.InvariantCulture)},\n" +
            $"  \"cy\": {cy.ToString("F6", CultureInfo.InvariantCulture)},\n" +
            "  \"model\": \"PINHOLE\"\n" +
            "}\n";

        File.WriteAllText(_intrinsicsJsonPath, json);

        if (logToConsole)
            Debug.Log($"[Recorder] Intrinsics written: {_intrinsicsJsonPath}\nfx={fx:F3} fy={fy:F3} cx={cx:F3} cy={cy:F3}");
    }
}
