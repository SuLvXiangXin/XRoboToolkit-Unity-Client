using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>Independent mono viewer; no TcpHandler, audio or pose lifecycle calls.</summary>
public sealed class GenericVideoView : MonoBehaviour
{
    [Serializable] private class Format { public int width, height, fps; public string session; }
    [Serializable] private class Settings { public bool enabled; public string host; public int port = 13579; }
    private AndroidJavaObject receiver;
    private GameObject panel;
    private RawImage image;
    private Text status;
    private Texture2D texture;
    private Material overlayMaterial, pictureMaterial;
    private RectTransform closeButtonRect;
    private int formatGeneration;
    private int videoFps = 30;
    private float nextPoll, nextStatus, rateStart;
    private int updates;
    private double maxFrameMs;
    private Coroutine transition;
    private Settings requested;
    private bool paused;
    public bool Requested => requested != null && requested.enabled;
    public event Action<bool> RequestedChanged;

    // Endpoint configuration is read only on an explicit Open video action.
    // Old automation files may contain enabled:true; it never starts playback.
    public bool TryGetConfiguredEndpoint(out string host, out int port)
    {
        host = null; port = 13579;
        try
        {
            string path = Path.Combine(Application.persistentDataPath, "generic_video.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 4096) return false;
            Settings settings = JsonUtility.FromJson<Settings>(File.ReadAllText(path));
            if (settings == null || string.IsNullOrWhiteSpace(settings.host) || settings.port < 1 || settings.port > 65535) return false;
            host = settings.host.Trim(); port = settings.port;
            return true;
        }
        catch (Exception) { return false; }
    }

    public void Connect(string host, int port = 13579)
    {
        if (string.IsNullOrWhiteSpace(host) || port < 1 || port > 65535)
            throw new ArgumentException("A video host and valid port are required");
        requested = new Settings { enabled = true, host = host.Trim(), port = port };
        RequestedChanged?.Invoke(true);
        BeginTransition();
    }

    public void StopVideo()
    {
        requested = new Settings { enabled = false };
        if (panel != null) panel.SetActive(false);
        RequestedChanged?.Invoke(false);
        BeginTransition();
    }

    private void BeginTransition()
    {
        if (receiver != null) receiver.Call("requestStop");
        if (transition == null) transition = StartCoroutine(ChangeSession());
    }

    private IEnumerator ChangeSession()
    {
        // Yield first so StartCoroutine's returned handle cannot overwrite a completed transition.
        yield return null;
        if (receiver != null)
        {
            while (!receiver.Call<bool>("isStopped")) yield return null;
            receiver.Call("releaseTexture");
            receiver.Dispose(); receiver = null;
        }
        if (texture != null) { Destroy(texture); texture = null; }
        if (image != null) { image.texture = null; image.enabled = false; }
        if (!paused && requested != null && requested.enabled)
        {
            EnsurePanel();
            panel.SetActive(true);
            status.text = "Connecting video...";
#if UNITY_ANDROID && !UNITY_EDITOR
            receiver = new AndroidJavaObject("com.xrobotoolkit.video.GenericVideoReceiver");
            formatGeneration = 0;
            receiver.Call("start", requested.host, requested.port);
#endif
        }
        else if (panel != null) panel.SetActive(false);
        transition = null;
    }

    private void EnsurePanel()
    {
        if (panel != null) return;
        panel = new GameObject("Generic Video", typeof(RectTransform), typeof(Canvas));
        Canvas canvas = panel.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.sortingOrder = 1000;
        canvas.worldCamera = Camera.main;
        var raycaster = panel.AddComponent<TrackedDeviceGraphicRaycaster>();
        raycaster.checkFor2DOcclusion = false;
        raycaster.checkFor3DOcclusion = false;
        overlayMaterial = new Material(Resources.Load<Shader>("GenericVideoOverlay"));
        pictureMaterial = new Material(overlayMaterial);
        pictureMaterial.SetFloat("_ForceOpaque", 1);
        panel.transform.localScale = Vector3.one * .001f;
        panel.GetComponent<RectTransform>().sizeDelta = new Vector2(800, 700);
        var picture = new GameObject("Image", typeof(RectTransform), typeof(RawImage));
        picture.transform.SetParent(panel.transform, false);
        image = picture.GetComponent<RawImage>();
        image.material = pictureMaterial;
        // Prevent clicks passing through the foreground image into hidden menus.
        image.raycastTarget = true;
        image.enabled = false;
        var label = new GameObject("Status", typeof(RectTransform), typeof(Text));
        label.transform.SetParent(panel.transform, false);
        status = label.GetComponent<Text>();
        status.material = overlayMaterial;
        status.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        status.fontSize = 22;
        status.alignment = TextAnchor.MiddleCenter;
        status.color = Color.white;
        status.raycastTarget = false;
        status.rectTransform.sizeDelta = new Vector2(1100, 90);
        status.rectTransform.anchoredPosition = new Vector2(0, -375);
        var close = new GameObject("Close video", typeof(RectTransform), typeof(Image), typeof(Button));
        close.transform.SetParent(panel.transform, false);
        closeButtonRect = close.GetComponent<RectTransform>();
        closeButtonRect.sizeDelta = new Vector2(70, 70);
        closeButtonRect.anchoredPosition = new Vector2(340, 375);
        var closeImage = close.GetComponent<Image>();
        closeImage.material = overlayMaterial;
        closeImage.color = new Color(.15f, .18f, .22f, 1);
        close.GetComponent<Button>().targetGraphic = closeImage;
        close.GetComponent<Button>().onClick.AddListener(StopVideo);
        var caption = new GameObject("Label", typeof(RectTransform), typeof(Text));
        caption.transform.SetParent(close.transform, false);
        var closeText = caption.GetComponent<Text>();
        closeText.text = "×";
        closeText.font = status.font;
        closeText.fontSize = 42;
        closeText.alignment = TextAnchor.MiddleCenter;
        closeText.material = overlayMaterial;
        closeText.raycastTarget = false;
        closeText.rectTransform.anchorMin = Vector2.zero;
        closeText.rectTransform.anchorMax = Vector2.one;
        closeText.rectTransform.sizeDelta = Vector2.zero;
        // The official controller ray mask only includes the UI layer.
        // Runtime GameObjects otherwise default to layer 0 and cannot be clicked.
        int uiLayer = LayerMask.NameToLayer("UI");
        foreach (Transform child in panel.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = uiLayer;
    }

    private void Update()
    {
        float now = Time.realtimeSinceStartup;
        updates++;
        maxFrameMs = Math.Max(maxFrameMs, Time.unscaledDeltaTime * 1000.0);
        float elapsed = now - rateStart;
        if (elapsed >= 5)
        {
            string state = receiver == null ? "off" : receiver.Call<string>("getState");
            long displayed = receiver == null ? 0 : receiver.Call<long>("getDisplayedFrames");
            if (receiver != null) receiver.Call("reportUnity", updates / elapsed, maxFrameMs);
            Debug.Log($"GENERIC_VIDEO state={state} displayed={displayed} unity_hz={updates / elapsed:F1} max_frame_ms={maxFrameMs:F2}");
            updates = 0; maxFrameMs = 0; rateStart = now;
        }
        if (paused || receiver == null || transition != null) return;
        if (now >= nextPoll)
        {
            nextPoll = Mathf.Max(nextPoll + 1f / Mathf.Clamp(videoFps, 1, 60), now);
            int generation = receiver.Call<int>("getFormatGeneration");
            if (generation != formatGeneration)
            {
                Format format = JsonUtility.FromJson<Format>(receiver.Call<string>("getFormat"));
                if (format.width < 16 || format.height < 16 || format.width > 4096 || format.height > 4096)
                { StopVideo(); return; }
                receiver.Call("releaseTexture");
                if (texture != null) Destroy(texture);
                texture = new Texture2D(format.width, format.height, TextureFormat.RGB24, false, false);
                image.texture = texture;
                image.enabled = false;
                float height = Mathf.Min(650, 1000f * format.height / format.width);
                image.rectTransform.sizeDelta = new Vector2(height * format.width / format.height, height);
                closeButtonRect.anchoredPosition = new Vector2(image.rectTransform.sizeDelta.x / 2 - 35, height / 2 + 40);
                receiver.Call("prepareTexture", (int)texture.GetNativeTexturePtr(), format.width, format.height, generation);
                formatGeneration = generation;
                videoFps = format.fps;
            }
        }
        // Poll the cheap coalesced flag each Unity frame. Polling at exactly the
        // source rate aliases with network jitter and unnecessarily drops frames.
        // A GL blit happens only for a new decoded frame, at most once per Update.
        if (texture != null && receiver.Call<bool>("updateTexture"))
        {
            GL.InvalidateState();
            image.enabled = true;
        }
        if (now >= nextStatus)
        {
            nextStatus = now + .5f;
            long age = receiver.Call<long>("getDisplayAgeMs");
            string state = receiver.Call<string>("getState");
            bool fresh = state == "playing" && age >= 0 && age < 500;
            image.enabled = fresh;
            status.text = fresh ? "Robot camera" : "VIDEO UNAVAILABLE — " + state;
            if (!fresh) status.text += "\n" + receiver.Call<string>("getError");
        }
    }

    private void LateUpdate()
    {
        if (panel == null || !panel.activeSelf || Camera.main == null) return;
        Transform head = Camera.main.transform;
        panel.transform.SetPositionAndRotation(head.position + head.forward * 1.3f, head.rotation);
    }

    private void OnApplicationPause(bool paused)
    {
        this.paused = paused;
        if (paused && receiver != null) receiver.Call("requestStop");
        if (!paused && Requested) BeginTransition();
    }

    private void OnDestroy()
    {
        if (receiver != null) receiver.Call("requestStop");
        if (panel != null) Destroy(panel);
        if (overlayMaterial != null) Destroy(overlayMaterial);
        if (pictureMaterial != null) Destroy(pictureMaterial);
        // The activity process owns final graphics teardown; never wait for network/codec here.
    }
}
