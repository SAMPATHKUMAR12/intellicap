using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

public class ComplexityScoreDB : MonoBehaviour
{
    [Header("CSV Settings")]
    [SerializeField] private string csvFileName = "label.csv";   // in StreamingAssets
    [SerializeField] private bool caseInsensitive = true;

    // className -> score (0..100)
    private readonly Dictionary<string, float> _scoreByClass = new();

    public bool IsReady { get; private set; } = false;

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        StartCoroutine(LoadCsv());
    }

    public bool TryGetScore(string className, out float score)
    {
        score = 0f;
        if (!IsReady || string.IsNullOrWhiteSpace(className)) return false;

        var key = caseInsensitive ? className.Trim().ToLowerInvariant() : className.Trim();
        return _scoreByClass.TryGetValue(key, out score);
    }

    private IEnumerator LoadCsv()
    {
        IsReady = false;
        _scoreByClass.Clear();

        // StreamingAssets path differs on Android (Quest)
        string path = Path.Combine(Application.streamingAssetsPath, csvFileName);

        string csvText = null;

        if (path.Contains("://") || path.Contains("jar:"))
        {
            using var req = UnityWebRequest.Get(path);
            yield return req.SendWebRequest();
            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"[ComplexityScoreDB] Failed to load CSV: {path}\n{req.error}");
                yield break;
            }
            csvText = req.downloadHandler.text;
        }
        else
        {
            if (!File.Exists(path))
            {
                Debug.LogError($"[ComplexityScoreDB] CSV not found: {path}");
                yield break;
            }
            csvText = File.ReadAllText(path);
        }

        Parse(csvText);

        IsReady = true;
        Debug.Log($"[ComplexityScoreDB] Loaded {_scoreByClass.Count} class scores from {csvFileName}");
    }

    private void Parse(string csv)
    {
        // Expected header:
        // ID,Label,Geometric,Texture,Size,Specular,Transparent
        // 0,person,90,90,70,10,0

        var lines = csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length <= 1)
        {
            Debug.LogError("[ComplexityScoreDB] CSV has no data lines.");
            return;
        }

        // skip header
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line)) continue;

            // Basic CSV split (ok for your file; no quoted commas)
            var parts = line.Split(',');
            if (parts.Length < 7) continue;

            var label = parts[1].Trim();
            if (string.IsNullOrEmpty(label)) continue;

            // Parse the 5 metrics
            if (!TryParseFloat(parts[2], out float g)) continue;
            if (!TryParseFloat(parts[3], out float t)) continue;
            if (!TryParseFloat(parts[4], out float s)) continue;
            if (!TryParseFloat(parts[5], out float sp)) continue;
            if (!TryParseFloat(parts[6], out float tr)) continue;

            // final score = average of 5 columns
            float score = (g + t + s + sp + tr) / 5f;

            var key = caseInsensitive ? label.ToLowerInvariant() : label;
            _scoreByClass[key] = score;
        }
    }

    private bool TryParseFloat(string str, out float value)
    {
        return float.TryParse(str.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
