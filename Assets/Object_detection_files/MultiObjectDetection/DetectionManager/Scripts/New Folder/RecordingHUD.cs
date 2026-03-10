using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class RecordingHUD : MonoBehaviour
{
    [Header("UI")]
    public Image recDot;
    public TMP_Text recText;
    public TMP_Text recTimer;
    public TMP_Text toastText;

    [Header("Blink")]
    public bool blink = true;
    public float blinkInterval = 0.5f;

    private Coroutine _blinkRoutine;
    private Coroutine _toastRoutine;

    private float _recordStartTime;
    private bool _isRecording;

    void Update()
    {
        if (_isRecording && recTimer)
        {
            float elapsed = Time.time - _recordStartTime;
            recTimer.text = FormatTime(elapsed);
        }
    }

    public void SetRecording(bool isRecording)
    {
        _isRecording = isRecording;

        if (recDot) recDot.gameObject.SetActive(isRecording);
        if (recText) recText.gameObject.SetActive(isRecording);
        if (recTimer) recTimer.gameObject.SetActive(isRecording);

        if (isRecording)
        {
            _recordStartTime = Time.time;

            if (blink && _blinkRoutine == null)
                _blinkRoutine = StartCoroutine(Blink());

            ShowToast("Recording started");
        }
        else
        {
            if (_blinkRoutine != null)
            {
                StopCoroutine(_blinkRoutine);
                _blinkRoutine = null;
            }

            if (recDot) recDot.enabled = true;

            string lastTime = recTimer ? recTimer.text : "";
            ShowToast($"Recording stopped ({lastTime})");
        }
    }

    public void ShowToast(string msg, float seconds = 2.0f)
    {
        if (_toastRoutine != null)
            StopCoroutine(_toastRoutine);

        _toastRoutine = StartCoroutine(ToastRoutine(msg, seconds));
    }

    private IEnumerator ToastRoutine(string msg, float seconds)
    {
        if (toastText)
        {
            toastText.text = msg;
            toastText.gameObject.SetActive(true);
        }

        yield return new WaitForSeconds(seconds);

        if (toastText)
        {
            toastText.text = "";
            toastText.gameObject.SetActive(false);
        }

        _toastRoutine = null;
    }

    private IEnumerator Blink()
    {
        while (true)
        {
            if (recDot) recDot.enabled = !recDot.enabled;
            yield return new WaitForSeconds(blinkInterval);
        }
    }

    private string FormatTime(float seconds)
    {
        int min = Mathf.FloorToInt(seconds / 60f);
        int sec = Mathf.FloorToInt(seconds % 60f);
        return $"{min:00}:{sec:00}";
    }
}
