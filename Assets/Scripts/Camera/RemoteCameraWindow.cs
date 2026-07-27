using System;
using System.Collections;
using LitJson;
using Network;
using Robot;
using UnityEngine;
using UnityEngine.UI;
using XRoboToolkit.ReceiverHeartbeat;


/// <summary>
/// Display window of PC camera.
/// The compressed video remains on the bundled direct TCP/MediaCodec path;
/// receiver heartbeat is a separate best-effort UDP status channel.
/// </summary>
public class RemoteCameraWindow : MonoBehaviour
{
    private const double ReceiverRestartCooldownSeconds = 2.0;

    public event Action CloseRequested;

    public RawImage RemoteCameraImage;
    private Texture2D _texture;
    public Texture2D Texture => _texture;

    private int _resolutionWidth = 2160;
    private int _resolutionHeight = 2160 / 2 * 4 / 3;
    private int _videoFps = 60;
    private int _bitrate = 40 * 1024 * 1024;
    private int _streamingPort = 12345;

    public CustomButton listenBtn;

    private readonly ReceiverHeartbeatPublisher _heartbeatPublisher = new ReceiverHeartbeatPublisher();
    private ReceiverHeartbeatSettings _heartbeatSettings;
    private Coroutine _startListenCoroutine;
    private int _sessionGeneration;
    private string _sessionId = string.Empty;
    private ulong _heartbeatSequence;
    private bool _receiverActive;
    private bool _decoderReady;
    private bool _decoderWasInitialized;
    private bool _notifyPcService = true;
    private bool _followMainCamera = true;
    private string _decoderError = string.Empty;
    private double _nextHeartbeatTime = -1;
    private double _lastDecodedTime = -1;
    private double _lastTextureUpdateTime = -1;
    private double _rateWindowStart = -1;
    private double _nextHeartbeatWarningTime;
    private double _nextReceiverRestartTime = -1;
    private int _windowUpdateCount;
    private int _windowDecodedCount;
    private int _windowTextureUpdateCount;
    private double _unityUpdateHz;
    private double _decodedFps;
    private double _textureUpdateFps;
    private ulong _decodedFrames;
    private ulong _displayedFrames;

    private void Awake()
    {
        PlaceAtMainCamera();
    }

    public void StartListen(int width, int height, int fps, int bitrate, int port)
    {
        StartListen(width, height, fps, bitrate, port, true, true, null);
    }

    public void StartListen(int width, int height, int fps, int bitrate, int port,
        bool followMainCamera, bool notifyPcService, ReceiverHeartbeatSettings heartbeatSettings)
    {
        StopReceiverSession(true);

        _resolutionWidth = width;
        _resolutionHeight = height;
        _videoFps = fps;
        _bitrate = bitrate;
        _streamingPort = port;
        _followMainCamera = followMainCamera;
        _notifyPcService = notifyPcService;
        _heartbeatSettings = heartbeatSettings;
        _receiverActive = true;
        _sessionId = Guid.NewGuid().ToString("N");
        _heartbeatSequence = 0;
        _decoderError = string.Empty;
        _lastDecodedTime = -1;
        _lastTextureUpdateTime = -1;
        _rateWindowStart = Time.realtimeSinceStartupAsDouble;
        _nextHeartbeatTime = _rateWindowStart;
        _nextHeartbeatWarningTime = 0;
        _nextReceiverRestartTime = -1;
        _windowUpdateCount = 0;
        _windowDecodedCount = 0;
        _windowTextureUpdateCount = 0;
        _unityUpdateHz = 0;
        _decodedFps = 0;
        _textureUpdateFps = 0;
        _decodedFrames = 0;
        _displayedFrames = 0;
        _sessionGeneration++;

        PlaceAtMainCamera();
        ConfigureHeartbeat();
        _startListenCoroutine = StartCoroutine(OnStartListen(port, _sessionGeneration));
    }

    private void OnDisable()
    {
        bool notifyPcService = _notifyPcService && _receiverActive;
        StopReceiverSession(true);
        Debug.Log("RemoteCameraWindow OnDisable");
        if (notifyPcService)
        {
            TcpHandler.SendFunctionValue("StopReceivePcCamera", "");
        }
    }

    private void OnDestroy()
    {
        StopReceiverSession(true);
        _heartbeatPublisher.Dispose();
    }

    public void OnCloseBtn()
    {
        listenBtn?.SetOn(false);
        try
        {
            if (NetworkCommander.Instance != null)
            {
                NetworkCommander.Instance.CloseCamera();
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Failed to send camera close command: " + exception.Message);
        }

        gameObject.SetActive(false);
        CloseRequested?.Invoke();
    }

    private IEnumerator OnStartListen(int port, int generation)
    {
        Debug.Log("StartListen port:" + port);

        _texture = new Texture2D(_resolutionWidth, _resolutionHeight, TextureFormat.RGB24, false, false);
        RemoteCameraImage.texture = _texture;
        ApplyDisplayAspectRatio();
        yield return null;
        if (generation != _sessionGeneration || !_receiverActive) yield break;

#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            _decoderWasInitialized = true;
            MediaDecoder.initialize((int)_texture.GetNativeTexturePtr(), _resolutionWidth, _resolutionHeight);
            MediaDecoder.startServer(port, false);
            _decoderReady = true;
        }
        catch (Exception exception)
        {
            _decoderReady = false;
            _decoderError = exception.Message;
            Debug.LogError("Failed to start direct video receiver: " + exception);
            PublishHeartbeatNow(Time.realtimeSinceStartupAsDouble);
            yield break;
        }
#else
        _decoderError = "Direct MediaCodec receiver is only available in an Android device build.";
        Debug.LogWarning(_decoderError);
        PublishHeartbeatNow(Time.realtimeSinceStartupAsDouble);
        yield break;
#endif

        yield return null;
        if (generation != _sessionGeneration || !_receiverActive) yield break;

        if (_notifyPcService)
        {
            JsonData cameraParam = new JsonData();
            cameraParam["ip"] = Utils.GetLocalIPv4();
            cameraParam["port"] = port;
            cameraParam["width"] = _resolutionWidth;
            cameraParam["height"] = _resolutionHeight;
            cameraParam["fps"] = _videoFps;
            cameraParam["bitrate"] = _bitrate;
            TcpHandler.SendFunctionValue("StartReceivePcCamera", cameraParam.ToJson());
        }

        _startListenCoroutine = null;
    }

    private void LateUpdate()
    {
        if (_followMainCamera)
        {
            PlaceAtMainCamera();
        }
    }

    private void Update()
    {
        if (!_receiverActive) return;

        double now = Time.realtimeSinceStartupAsDouble;
        _windowUpdateCount++;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (_texture != null && _decoderReady)
        {
            try
            {
                if (MediaDecoder.isUpdateFrame())
                {
                    _lastDecodedTime = now;
                    _windowDecodedCount++;
                    _decodedFrames++;
                    MediaDecoder.updateTexture();
                    GL.InvalidateState();
                    _lastTextureUpdateTime = now;
                    _windowTextureUpdateCount++;
                    _displayedFrames++;
                }
            }
            catch (Exception exception)
            {
                _decoderReady = false;
                _decoderError = exception.Message;
                Debug.LogError("Direct video decoder update failed: " + exception);
            }
        }
#endif

        RestartStalledReceiverIfNeeded(now);
        UpdateRates(now);
        PublishHeartbeatIfDue(now);
    }

    private void ApplyDisplayAspectRatio()
    {
        if (RemoteCameraImage == null || _resolutionWidth <= 0 || _resolutionHeight <= 0) return;

        RectTransform rect = RemoteCameraImage.rectTransform;
        float height = rect.sizeDelta.y;
        if (height <= 0f) return;

        rect.sizeDelta = new Vector2(height * _resolutionWidth / _resolutionHeight, height);
    }

    private void RestartStalledReceiverIfNeeded(double now)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!_decoderReady || _lastTextureUpdateTime < 0 || now < _nextReceiverRestartTime) return;

        int staleMs = _heartbeatSettings?.FrameStaleMs ?? ReceiverHeartbeatSettings.DefaultFrameStaleMs;
        if ((now - _lastTextureUpdateTime) * 1000.0 <= staleMs) return;

        _nextReceiverRestartTime = now + ReceiverRestartCooldownSeconds;
        try
        {
            // The bundled receiver accepts one TCP connection and exits after it
            // disconnects. Reopening its server is enough; MediaCodec can stay alive.
            MediaDecoder.startServer(_streamingPort, false);
            Debug.LogWarning($"Restarted stalled direct video receiver on port {_streamingPort}.");
        }
        catch (Exception exception)
        {
            Debug.LogWarning("Failed to restart stalled direct video receiver: " + exception.Message);
        }
#endif
    }

    private void ConfigureHeartbeat()
    {
        if (_heartbeatSettings == null || !_heartbeatSettings.Enabled) return;
        if (!_heartbeatPublisher.Configure(_heartbeatSettings, out string error))
        {
            Debug.LogWarning(error);
        }
    }

    private void PublishHeartbeatIfDue(double now)
    {
        if (_heartbeatSettings == null || !_heartbeatSettings.Enabled || !_heartbeatPublisher.IsConfigured) return;
        if (now < _nextHeartbeatTime) return;

        double interval = 1.0 / Math.Max(0.2, _heartbeatSettings.PublishHz);
        _nextHeartbeatTime = now + interval;
        PublishHeartbeatNow(now);
    }

    private void PublishHeartbeatNow(double now)
    {
        if (_heartbeatSettings == null || !_heartbeatSettings.Enabled || !_heartbeatPublisher.IsConfigured) return;

        ulong candidateSequence = _heartbeatSequence == ulong.MaxValue ? 1ul : _heartbeatSequence + 1ul;
        ReceiverHeartbeatSnapshot snapshot = BuildHeartbeatSnapshot(now, candidateSequence);
        if (_heartbeatPublisher.TryPublish(snapshot, out string error))
        {
            _heartbeatSequence = candidateSequence;
            return;
        }

        if (now >= _nextHeartbeatWarningTime)
        {
            _nextHeartbeatWarningTime = now + 5.0;
            Debug.LogWarning(error);
        }
    }

    private ReceiverHeartbeatSnapshot BuildHeartbeatSnapshot(double now, ulong sequence)
    {
        long decodedAgeMs = ReceiverHeartbeatPolicy.AgeMilliseconds(now, _lastDecodedTime);
        long textureAgeMs = ReceiverHeartbeatPolicy.AgeMilliseconds(now, _lastTextureUpdateTime);
        long receiveAgeMs;
        string receiveObservation;
        if (MediaDecoder.TryGetLastDecoderInputAgeMs(out long decoderInputAgeMs))
        {
            receiveAgeMs = decoderInputAgeMs;
            receiveObservation = "decoder_input_queued";
        }
        else
        {
            receiveAgeMs = -1;
            receiveObservation = "decoder_input_timestamp_unavailable";
        }

        int frameStaleMs = _heartbeatSettings?.FrameStaleMs ?? ReceiverHeartbeatSettings.DefaultFrameStaleMs;
        bool frameFresh = textureAgeMs >= 0 && textureAgeMs <= frameStaleMs;
        string receiverState = !_receiverActive
            ? "stopped"
            : !string.IsNullOrEmpty(_decoderError)
                ? "error"
                : frameFresh ? "streaming" : textureAgeMs < 0 ? "listening" : "stale";
        string connectionState = !_receiverActive
            ? "stopped"
            : frameFresh ? "streaming_inferred" : textureAgeMs < 0 ? "listening" : "stale";
        string decoderState = !_receiverActive
            ? "stopped"
            : !string.IsNullOrEmpty(_decoderError)
                ? "error"
                : !_decoderReady || textureAgeMs < 0
                    ? "starting"
                    : frameFresh ? "playing" : "stalled";

        long unixTicks = DateTime.UtcNow.Ticks - 621355968000000000L;
        bool visualAvailable = ReceiverHeartbeatPolicy.IsLocalVisualProfileAvailable(
            _heartbeatSettings?.VisualDependent == true, _heartbeatPublisher.IsConfigured,
            _receiverActive, _decoderReady,
            receiveAgeMs, textureAgeMs, frameStaleMs);

        return new ReceiverHeartbeatSnapshot
        {
            SessionId = _sessionId,
            Sequence = sequence,
            SentUnixTimeNs = unixTicks > 0 ? unixTicks * 100L : 0L,
            SourceName = _heartbeatSettings?.SourceName ?? string.Empty,
            VisualProfile = _heartbeatSettings?.VisualProfile ?? string.Empty,
            DisplayMode = "flat",
            FollowHead = _followMainCamera,
            ReceiverState = receiverState,
            ConnectionState = connectionState,
            DecoderState = decoderState,
            ReceiveObservation = receiveObservation,
            RawReceiveObservable = false,
            LastReceiveAgeMs = receiveAgeMs,
            LastDecodedAgeMs = decodedAgeMs,
            LastTextureUpdateAgeMs = textureAgeMs,
            ReceiveFps = _decodedFps,
            DecodedFps = _decodedFps,
            TextureUpdateFps = _textureUpdateFps,
            UnityUpdateHz = _unityUpdateHz,
            DecodedFrames = _decodedFrames,
            DisplayedFrames = _displayedFrames,
            VisualProfileAvailable = visualAvailable,
            HeartbeatTimeoutMs = _heartbeatSettings?.HeartbeatTimeoutMs ??
                                 ReceiverHeartbeatSettings.DefaultHeartbeatTimeoutMs,
            PublisherDroppedCount = _heartbeatPublisher.DroppedCount
        };
    }

    private void UpdateRates(double now)
    {
        if (_rateWindowStart < 0)
        {
            _rateWindowStart = now;
            return;
        }

        double elapsed = now - _rateWindowStart;
        if (elapsed < 1.0) return;

        _unityUpdateHz = _windowUpdateCount / elapsed;
        _decodedFps = _windowDecodedCount / elapsed;
        _textureUpdateFps = _windowTextureUpdateCount / elapsed;
        _windowUpdateCount = 0;
        _windowDecodedCount = 0;
        _windowTextureUpdateCount = 0;
        _rateWindowStart = now;
    }

    private void StopReceiverSession(bool releaseDecoder)
    {
        if (_startListenCoroutine != null)
        {
            StopCoroutine(_startListenCoroutine);
            _startListenCoroutine = null;
        }

        bool hadSession = _receiverActive;
        _receiverActive = false;
        _decoderReady = false;
        _sessionGeneration++;
        if (hadSession)
        {
            PublishHeartbeatNow(Time.realtimeSinceStartupAsDouble);
        }
        _heartbeatPublisher.Dispose();

#if UNITY_ANDROID && !UNITY_EDITOR
        if (releaseDecoder && _decoderWasInitialized)
        {
            try
            {
                MediaDecoder.release();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Failed to release direct video decoder: " + exception.Message);
            }
            finally
            {
                _decoderWasInitialized = false;
            }
        }
#endif

        if (_texture != null)
        {
            if (RemoteCameraImage != null && RemoteCameraImage.texture == _texture)
            {
                RemoteCameraImage.texture = null;
            }
            Destroy(_texture);
            _texture = null;
        }

        _heartbeatSettings = null;
        _sessionId = string.Empty;
        _heartbeatSequence = 0;
        _nextHeartbeatTime = -1;
        _nextReceiverRestartTime = -1;
    }

    private void PlaceAtMainCamera()
    {
        if (Camera.main == null) return;
        transform.position = Camera.main.transform.position;
        transform.rotation = Camera.main.transform.rotation;
    }
}
