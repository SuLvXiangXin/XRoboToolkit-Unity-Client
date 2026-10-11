using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.EventSystems;
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
    private RectTransform closeButtonRect, titleBarRect, resizeHandleRect;
    private Sprite frameSprite;
    private Texture2D frameTexture;
    public static Font ChineseFont => Resources.Load<Font>("GenericVideoChinese");
    private const float WindowDistance = 1.3f;
    private float windowScale = 1.5f;
    private Vector2 windowOffset;
    public float WindowScale => windowScale;
    public Vector2 WindowOffset => windowOffset;
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

    private void Awake()
    {
        windowScale = Mathf.Clamp(PlayerPrefs.GetFloat("GenericVideo.WindowScale", 1.5f), .75f, 2f);
        windowOffset = new Vector2(PlayerPrefs.GetFloat("GenericVideo.WindowX", 0), PlayerPrefs.GetFloat("GenericVideo.WindowY", 0));
        SetWindowLayout(windowOffset, windowScale);
    }

    public void SetWindowLayout(Vector2 offset, float scale)
    {
        windowScale = Mathf.Clamp(scale, .75f, 2f);
        // Bound movement so the controls remain within a comfortable field of view.
        windowOffset = new Vector2(Mathf.Clamp(offset.x, -.5f, .5f), Mathf.Clamp(offset.y, -.35f, .35f));
        if (panel != null) panel.transform.localScale = Vector3.one * (.001f * windowScale);
    }

    public void SaveWindowLayout()
    {
        PlayerPrefs.SetFloat("GenericVideo.WindowScale", windowScale);
        PlayerPrefs.SetFloat("GenericVideo.WindowX", windowOffset.x);
        PlayerPrefs.SetFloat("GenericVideo.WindowY", windowOffset.y);
        PlayerPrefs.Save();
        Debug.Log($"GENERIC_VIDEO_WINDOW scale={windowScale:F2} x={windowOffset.x:F3} y={windowOffset.y:F3}");
    }

    public bool TryGetPointerPosition(PointerEventData data, out Vector2 position)
    {
        position = default;
        if (Camera.main == null) return false;
        Transform head = Camera.main.transform;
        var plane = new Plane(head.forward, head.position + head.forward * WindowDistance);
        Vector3 hit;
        if (data is TrackedDeviceEventData tracked)
        {
            if (tracked.rayPoints == null) return false;
            for (int i = 1; i < tracked.rayPoints.Count; i++)
            {
                Vector3 from = tracked.rayPoints[i - 1];
                Vector3 delta = tracked.rayPoints[i] - from;
                if (delta.sqrMagnitude < .000001f) continue;
                var ray = new Ray(from, delta.normalized);
                if (!plane.Raycast(ray, out float distance) || distance > delta.magnitude) continue;
                hit = ray.GetPoint(distance) - head.position;
                position = new Vector2(Vector3.Dot(hit, head.right), Vector3.Dot(hit, head.up));
                return true;
            }
            return false;
        }
        var mouseRay = Camera.main.ScreenPointToRay(data.position);
        if (!plane.Raycast(mouseRay, out float mouseDistance)) return false;
        hit = mouseRay.GetPoint(mouseDistance) - head.position;
        position = new Vector2(Vector3.Dot(hit, head.right), Vector3.Dot(hit, head.up));
        return true;
    }

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
            status.text = "正在连接视频";
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
        panel.transform.localScale = Vector3.one * (.001f * windowScale);
        // One continuous frame keeps controls visually attached to the picture.
        var frame = panel.AddComponent<Image>();
        frame.material = overlayMaterial;
        frame.sprite = CreateFrameSprite();
        frame.type = Image.Type.Sliced;
        frame.color = new Color(.065f, .085f, .115f, .98f);
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
        status.font = ChineseFont;
        status.fontSize = 18;
        status.alignment = TextAnchor.MiddleLeft;
        status.color = new Color(.60f, .68f, .76f, 1);
        status.raycastTarget = false;
        status.rectTransform.sizeDelta = new Vector2(1100, 90);
        status.rectTransform.anchoredPosition = new Vector2(0, -375);
        titleBarRect = CreateHandle("Move video", "相机画面", false);
        resizeHandleRect = CreateHandle("Resize video", "", true);
        var close = new GameObject("Close video", typeof(RectTransform), typeof(Image), typeof(Button));
        close.transform.SetParent(panel.transform, false);
        closeButtonRect = close.GetComponent<RectTransform>();
        closeButtonRect.sizeDelta = new Vector2(70, 70);
        closeButtonRect.anchoredPosition = new Vector2(340, 375);
        var closeImage = close.GetComponent<Image>();
        closeImage.material = overlayMaterial;
        closeImage.color = Color.white;
        closeImage.sprite = frameSprite;
        closeImage.type = Image.Type.Sliced;
        close.GetComponent<Button>().targetGraphic = closeImage;
        SetControlColors(close.GetComponent<Button>(), true);
        close.GetComponent<Button>().onClick.AddListener(StopVideo);
        var caption = new GameObject("Label", typeof(RectTransform), typeof(Text));
        caption.transform.SetParent(close.transform, false);
        var closeText = caption.GetComponent<Text>();
        closeText.text = "×";
        closeText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        closeText.fontSize = 34;
        closeText.alignment = TextAnchor.MiddleCenter;
        closeText.material = overlayMaterial;
        closeText.raycastTarget = false;
        closeText.rectTransform.anchorMin = Vector2.zero;
        closeText.rectTransform.anchorMax = Vector2.one;
        closeText.rectTransform.sizeDelta = Vector2.zero;
        LayoutPanel(754, 650);
        // The official controller ray mask only includes the UI layer.
        // Runtime GameObjects otherwise default to layer 0 and cannot be clicked.
        int uiLayer = LayerMask.NameToLayer("UI");
        foreach (Transform child in panel.GetComponentsInChildren<Transform>(true))
            child.gameObject.layer = uiLayer;
    }

    private RectTransform CreateHandle(string name, string caption, bool resize)
    {
        var handle = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(GenericVideoWindowHandle));
        handle.transform.SetParent(panel.transform, false);
        var background = handle.GetComponent<Image>();
        background.material = overlayMaterial;
        background.color = Color.white;
        background.sprite = frameSprite;
        background.type = Image.Type.Sliced;
        var feedback = handle.AddComponent<Selectable>();
        feedback.targetGraphic = background;
        SetControlColors(feedback, false);
        var drag = handle.GetComponent<GenericVideoWindowHandle>();
        drag.View = this;
        drag.Resize = resize;
        var label = new GameObject("Label", typeof(RectTransform), typeof(Text));
        label.transform.SetParent(handle.transform, false);
        var text = label.GetComponent<Text>();
        text.text = caption;
        text.font = status.font;
        text.fontSize = 24;
        text.color = new Color(.90f, .94f, .98f, 1);
        text.alignment = TextAnchor.MiddleLeft;
        text.material = overlayMaterial;
        text.raycastTarget = false;
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = new Vector2(20, 0);
        text.rectTransform.offsetMax = new Vector2(-12, 0);
        if (resize)
        {
            // Draw a quiet grip instead of covering the video with an arrow button.
            for (int i = 0; i < 3; i++)
            {
                var line = new GameObject("Grip", typeof(RectTransform), typeof(Image));
                line.transform.SetParent(handle.transform, false);
                var graphic = line.GetComponent<Image>();
                graphic.material = overlayMaterial;
                graphic.color = new Color(.60f, .72f, .82f, 1);
                graphic.raycastTarget = false;
                graphic.rectTransform.sizeDelta = new Vector2(9 + i * 8, 2);
                graphic.rectTransform.anchoredPosition = new Vector2(17 - i * 4, -17 + i * 4);
                graphic.rectTransform.localRotation = Quaternion.Euler(0, 0, 45);
            }
        }
        return handle.GetComponent<RectTransform>();
    }

    private void SetControlColors(Selectable control, bool close)
    {
        ColorBlock colors = control.colors;
        colors.normalColor = new Color(0, 0, 0, 0);
        colors.highlightedColor = close ? new Color(.75f, .23f, .28f, .8f) : new Color(.23f, .46f, .64f, .35f);
        colors.pressedColor = close ? new Color(.85f, .24f, .29f, 1) : new Color(.23f, .46f, .64f, .6f);
        colors.selectedColor = colors.normalColor;
        colors.fadeDuration = .12f;
        control.colors = colors;
        control.navigation = new Navigation { mode = Navigation.Mode.None };
    }

    private Sprite CreateFrameSprite()
    {
        const int size = 64, radius = 16;
        frameTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        frameTexture.wrapMode = TextureWrapMode.Clamp;
        var pixels = new Color[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = Mathf.Max(radius - x - .5f, x + .5f - (size - radius), 0);
                float dy = Mathf.Max(radius - y - .5f, y + .5f - (size - radius), 0);
                pixels[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(radius - Mathf.Sqrt(dx * dx + dy * dy)));
            }
        frameTexture.SetPixels(pixels);
        frameTexture.Apply(false, true);
        frameSprite = Sprite.Create(frameTexture, new Rect(0, 0, size, size), Vector2.one * .5f,
            100, 0, SpriteMeshType.FullRect, Vector4.one * radius);
        return frameSprite;
    }

    private void LayoutPanel(float width, float height)
    {
        panel.GetComponent<RectTransform>().sizeDelta = new Vector2(width + 16, height + 112);
        panel.GetComponent<RectTransform>().pivot = new Vector2(.5f, (height / 2 + 48) / (height + 112));
        image.rectTransform.sizeDelta = new Vector2(width, height);
        // With a non-central frame pivot, children must anchor to that pivot to
        // preserve the video-centered head-relative drag coordinates.
        Vector2 pivot = panel.GetComponent<RectTransform>().pivot;
        foreach (RectTransform child in panel.transform)
            child.anchorMin = child.anchorMax = pivot;
        titleBarRect.sizeDelta = new Vector2(width - 56, 56);
        titleBarRect.anchoredPosition = new Vector2(-28, height / 2 + 32);
        closeButtonRect.sizeDelta = new Vector2(56, 56);
        closeButtonRect.anchoredPosition = new Vector2(width / 2 - 28, height / 2 + 32);
        resizeHandleRect.sizeDelta = new Vector2(64, 64);
        resizeHandleRect.anchoredPosition = new Vector2(width / 2 - 24, -height / 2 - 16);
        status.rectTransform.sizeDelta = new Vector2(width - 88, 40);
        status.rectTransform.anchoredPosition = new Vector2(-24, -height / 2 - 24);
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
                LayoutPanel(height * format.width / format.height, height);
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
            status.text = fresh ? "拖动标题栏移动　拖动右下角缩放"
                : state == "connecting" ? "正在连接视频"
                : state == "configuring" ? "正在准备画面" : "暂无画面，正在等待恢复";
            status.color = fresh ? new Color(.60f, .68f, .76f, 1) : new Color(1, .76f, .42f, 1);
        }
    }

    private void LateUpdate()
    {
        if (panel == null || !panel.activeSelf || Camera.main == null) return;
        Transform head = Camera.main.transform;
        panel.transform.SetPositionAndRotation(head.position + head.forward * WindowDistance
            + head.right * windowOffset.x + head.up * windowOffset.y, head.rotation);
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
        if (frameSprite != null) Destroy(frameSprite);
        if (frameTexture != null) Destroy(frameTexture);
        // The activity process owns final graphics teardown; never wait for network/codec here.
    }
}
