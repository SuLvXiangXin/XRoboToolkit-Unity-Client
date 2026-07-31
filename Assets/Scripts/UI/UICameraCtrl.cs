using System;
using System.Collections;
using System.IO;
using LitJson;
using Network;
using Robot;
using Robot.Conf;
using Robot.Network;
using Robot.V2.Network;
using Unity.XR.PICO.TOBSupport;
using UnityEngine;
using UnityEngine.UI;
using XRoboToolkit.Network;
using XRoboToolkit.ReceiverHeartbeat;

public partial class UICameraCtrl : MonoBehaviour
{
    public GameObject RemoteCameraWindowObj;

    public CameraRequestDialog CameraRequestDialog;
    public ResolutionDialog ResolutionDialog;
    public CameraSendInputDialog CameraSendInputDialog;
    public CustomButton ListenCameraBtn;
    public CustomButton CameraSendToBtn;

    public CustomButton ListenPCCameraBtn;
    public Dropdown cameraDropdown;

    public SetLERE setLere;

    private JsonData _recordJson;
    public Text CameraStatusText;

    public CustomButton listenBtn;
    public VideoSourceManager videoSourceManager;

    public TcpManager tcpManager;
    private string logTag => "UICameraCtrl";
    private Coroutine _cameraRequestCoroutine;
    private RemoteCameraWindow _remoteCameraWindow;

    private int streamingPort = 12345;

    [Space(30)] [Header("Record")] public RecordDialog RecordDialog;
    public CustomButton RecordBtn;

    public Toggle trackingToggle;
    public Toggle visionToggle;

    private void Awake()
    {
        RecordBtn.OnChange += OnRecordBtn;
        TcpHandler.ReceiveFunctionEvent += OnNetReceive;
        CameraHandle.AddStateListener(OnCameraStateChanged);

        // Refactoring
        listenBtn.OnChange += OnListenCameraBtn;

        // Bind event
        tcpManager.OnServerReceived += OnServerReceived;
        tcpManager.OnClientReceived += OnClientReceived;

        _remoteCameraWindow = RemoteCameraWindowObj == null
            ? null
            : RemoteCameraWindowObj.GetComponent<RemoteCameraWindow>();
        if (_remoteCameraWindow != null)
        {
            _remoteCameraWindow.CloseRequested += OnRemoteCameraCloseRequested;
        }
    }

    private void OnServerReceived(byte[] data)
    {
        // apply protocol
        Utils.WriteLog(logTag, $"OnServerReceived: {data.Length} bytes");

        // Log first few bytes for debugging
        if (data.Length > 0)
        {
            string hexDump = BitConverter.ToString(data, 0, Math.Min(data.Length, 32));
            Utils.WriteLog(logTag, $"First bytes (hex): {hexDump}");
        }

        EventExecutor.ExecuteInUpdate(() =>
        {
            try
            {
                Utils.WriteLog(logTag, $"Processing data...");

                // Check if it's a complete message first
                if (!NetworkDataProtocolSerializer.IsCompleteMessage(data))
                {
                    Utils.WriteLog(logTag, $"Incomplete message received");
                    return;
                }

                var protocol = NetworkDataProtocolSerializer.Deserialize(data);
                Utils.WriteLog(logTag,
                    $"Successfully deserialized: command='{protocol.command}', data length={protocol.data.Length}");

                // Process the command
                if (NetworkCommander.Instance == null)
                {
                    Utils.WriteLog(logTag, $"NetworkCommander.Instance is null");
                    return;
                }

                if (NetworkCommander.Instance.Processor == null)
                {
                    Utils.WriteLog(logTag, $"NetworkCommander.Instance.Processor is null");
                    return;
                }

                bool handled = NetworkCommander.Instance.Processor.ProcessCommand(protocol);
                Utils.WriteLog(logTag, $"Command processed: {handled}");
            }
            catch (Exception e)
            {
                Utils.WriteLog(logTag, $"Error processing command: {e.Message}");
                Utils.WriteLog(logTag, $"Stack trace: {e.StackTrace}");

                // Log detailed buffer analysis
                string bufferDebug = NetworkDataProtocolSerializer.DebugBufferContents(data);
                Utils.WriteLog(logTag, $"Buffer analysis:\n{bufferDebug}");
            }
        });
    }

    private void OnClientReceived(string msg)
    {
        Utils.WriteLog(logTag, $"OnClientReceived: {msg}");
    }

    public void OnListenCameraBtn(bool on)
    {
        if (on)
        {
            // check if the dropdown is updated
            if (cameraDropdown.options == null || cameraDropdown.options.Count == 0) return;

            // get the camera source from the dropdown
            var cameraSource = cameraDropdown.options[cameraDropdown.value].text;

            // Update camera source, including shaders, etc.
            videoSourceManager.UpdateVideoSource(cameraSource);

            // send video stream request to the server
            CameraSendInputDialog.Show(RequestCameraStream);
        }
        else
        {
            StopCameraStream(true);
        }

        // Update button
        listenBtn.SetOn(on);
    }

    public void RequestCameraStream(string ip)
    {
        StopCameraStream(true);
        _cameraRequestCoroutine = StartCoroutine(RequestCameraStreamCoroutine(ip));
    }

    IEnumerator RequestCameraStreamCoroutine(string ip)
    {
        string sourceIp = (ip ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sourceIp))
        {
            FailCameraRequest("Camera sender IP is required.");
            yield break;
        }

        VideoSourceConfigManager config = VideoSourceConfigManager.Instance;
        bool usePcServiceControl = config.UsePcServiceControl;

        if (TcpServer.Status == ServerStatus.Started)
        {
            // Close TcpServer first
            if (TcpManager.Instance != null) TcpManager.Instance.StopServer();
        }

        yield return new WaitForSeconds(0.1f);

        // Get camera parameters
        var camPara = config.CameraParameters;
        string heartbeatHost = string.IsNullOrWhiteSpace(config.ReceiverHeartbeatHost)
            ? sourceIp
            : config.ReceiverHeartbeatHost.Trim();
        var heartbeatSettings = new ReceiverHeartbeatSettings
        {
            Enabled = config.ReceiverHeartbeatEnabled,
            DestinationHost = heartbeatHost,
            DestinationPort = config.ReceiverHeartbeatPort,
            PublishHz = config.ReceiverHeartbeatHz,
            HeartbeatTimeoutMs = config.ReceiverHeartbeatTimeoutMs,
            FrameStaleMs = config.ReceiverFrameStaleMs,
            VisualDependent = config.VisualDependentProfile,
            VisualProfile = config.VisualProfile,
            SourceName = config.CurrentVideoSource?.name ?? string.Empty
        };
        if (!heartbeatSettings.TryValidate(out string heartbeatError))
        {
            FailCameraRequest(heartbeatError);
            yield break;
        }

        // Start listening to the camera
        RemoteCameraWindowObj.SetActive(true);
        _remoteCameraWindow = RemoteCameraWindowObj.GetComponent<RemoteCameraWindow>();
        _remoteCameraWindow.StartListen(camPara.width, camPara.height, camPara.fps,
            camPara.bitrate, streamingPort, config.FollowHead, usePcServiceControl, heartbeatSettings);

        yield return new WaitForSeconds(0.2f);

        // Reset LERE
        setLere.ResetCanvases();

        yield return new WaitForSeconds(0.1f);

        if (TcpClient.Status == ClientStatus.Connected || TcpClient.Status == ClientStatus.Connecting)
        {
            if (TcpManager.Instance != null) TcpManager.Instance.StopClient();
            yield return new WaitForSecondsRealtime(0.1f);
        }

        if (TcpManager.Instance == null)
        {
            HandleCameraRegistrationFailure(usePcServiceControl, "Camera control manager is unavailable.");
            yield break;
        }

        try
        {
            TcpManager.Instance.StartClient(sourceIp);
        }
        catch (Exception exception)
        {
            HandleCameraRegistrationFailure(usePcServiceControl,
                "Failed to start camera control connection: " + exception.Message);
            yield break;
        }

        float connectionDeadline = Time.realtimeSinceStartup + 5f;
        while (TcpClient.Status == ClientStatus.Connecting && Time.realtimeSinceStartup < connectionDeadline)
        {
            yield return null;
        }
        if (TcpClient.Status != ClientStatus.Connected)
        {
            HandleCameraRegistrationFailure(usePcServiceControl,
                "Camera registration connection timed out or failed.");
            yield break;
        }

        string localIP = TcpClient.LocalAddress;
        if (string.IsNullOrWhiteSpace(localIP))
        {
            HandleCameraRegistrationFailure(usePcServiceControl,
                "Camera registration socket did not expose a local IPv4 address.");
            yield break;
        }

        // Send request to the server
        var customConfig = CameraRequestSerializer.FromCameraParameters(
            camPara,
            0,
            2, // (int)PXRCaptureRenderMode.PXRCapture_RenderMode_3D
            VideoSourceConfigManager.Instance.CurrentVideoSource.camera,
            localIP, // local ip
            streamingPort);
        
        LogWindow.Info("Requesting camera stream with config: " + customConfig.ToString());

        // Utils.WriteLog(logTag, $"send camera config: {customConfig}");
        var data = CameraRequestSerializer.Serialize(customConfig);

        // Use network commander
        if (NetworkCommander.Instance == null)
        {
            HandleCameraRegistrationFailure(usePcServiceControl,
                "Camera registration command sender is unavailable.");
            yield break;
        }
        try
        {
            NetworkCommander.Instance.OpenCamera(data);
        }
        catch (Exception exception)
        {
            HandleCameraRegistrationFailure(usePcServiceControl,
                "Failed to register PICO camera receiver: " + exception.Message);
            yield break;
        }
        _cameraRequestCoroutine = null;
    }

    private void HandleCameraRegistrationFailure(bool usePcServiceControl, string message)
    {
        if (usePcServiceControl)
        {
            FailCameraRequest(message);
            return;
        }

        // CAMERA_ONLY uses this connection only to tell the gateway where this
        // PICO is listening. It is not a PC Service dependency. Keep the local
        // receiver alive when registration fails so a retry does not blank it.
        Debug.LogWarning(message);
        LogWindow.Warn(message + " PICO receiver remains listening.");
        Toast.Show("Camera registration failed; receiver remains listening.");
        _cameraRequestCoroutine = null;
        if (TcpClient.Status == ClientStatus.Connected || TcpClient.Status == ClientStatus.Connecting)
        {
            if (TcpManager.Instance != null) TcpManager.Instance.StopClient();
        }
    }

    private void FailCameraRequest(string message)
    {
        Debug.LogError(message);
        LogWindow.Error(message);
        Toast.Show(message);
        _cameraRequestCoroutine = null;
        listenBtn.SetOn(false);
        if (RemoteCameraWindowObj != null)
        {
            RemoteCameraWindowObj.SetActive(false);
        }
        if (TcpClient.Status == ClientStatus.Connected || TcpClient.Status == ClientStatus.Connecting)
        {
            if (TcpManager.Instance != null) TcpManager.Instance.StopClient();
        }
    }

    private void StopCameraStream(bool sendCloseCommand)
    {
        if (_cameraRequestCoroutine != null)
        {
            StopCoroutine(_cameraRequestCoroutine);
            _cameraRequestCoroutine = null;
        }

        if (sendCloseCommand && TcpClient.Status == ClientStatus.Connected && NetworkCommander.Instance != null)
        {
            try
            {
                NetworkCommander.Instance.CloseCamera();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Failed to send camera close command: " + exception.Message);
            }
        }

        if (RemoteCameraWindowObj != null)
        {
            RemoteCameraWindowObj.SetActive(false);
        }
        if (TcpClient.Status == ClientStatus.Connected || TcpClient.Status == ClientStatus.Connecting)
        {
            if (TcpManager.Instance != null) TcpManager.Instance.StopClient();
        }
    }

    private void OnRemoteCameraCloseRequested()
    {
        StopCameraStream(false);
        listenBtn?.SetOn(false);
    }

    private void OnCameraStateChanged(int state)
    {
        CameraStatusText.text = state.ToString();
    }


    private void OnDestroy()
    {
        StopCameraStream(false);
        TcpHandler.ReceiveFunctionEvent -= OnNetReceive;
        CameraHandle.RemoveStateListener(OnCameraStateChanged);
        listenBtn.OnChange -= OnListenCameraBtn;
        tcpManager.OnServerReceived -= OnServerReceived;
        tcpManager.OnClientReceived -= OnClientReceived;
        if (_remoteCameraWindow != null)
        {
            _remoteCameraWindow.CloseRequested -= OnRemoteCameraCloseRequested;
        }
    }

    private void OnRecordBtn(bool on)
    {
        if (on)
        {
            OpenRecord();
        }
        else
        {
            StopRecord();
        }
    }

    private void OpenRecord()
    {
        if (!visionToggle.isOn && !trackingToggle.isOn)
        {
            LogWindow.Error("Please select at least one option: Tracking or Vision.");
            return;
        }

        // Vision data
        if (visionToggle.isOn)
        {
            if (trackingToggle.isOn)
            {
                // Start together
                RecordDialog.Show(() =>
                {
                    StartRecord(RecordDialog.ResolutionWidth, RecordDialog.ResolutionHeight,
                        RecordDialog.Fps, RecordDialog.Bitrate, true);
                }, null);
            }
            else
            {
                // Vision data only
                RecordDialog.Show(() =>
                {
                    StartRecord(RecordDialog.ResolutionWidth, RecordDialog.ResolutionHeight,
                        RecordDialog.Fps, RecordDialog.Bitrate, false);
                }, null);
            }
        }
        else
        {
            if (trackingToggle.isOn)
            {
                // Only tracking data, use fixed resolution
                _recordTrackingData = true;
                OnStartRecordTracking(2160, 810);
                RecordBtn.SetOn(true);
            }
        }
    }

    private void StartRecord(int width, int height, int fps, int bitrate,
        bool onTrackingData)
    {
        LogWindow.Info($"Start record {width}x{height}@{fps} fps {bitrate} bps Tracking: {onTrackingData}");
        //The VR camera image acquisition is only effective on the B-end device of Pico4U.
        if (!Utils.IsPico4U())
        {
            Toast.Show("Please use B-end pico4U devices and apply for camera permissions.");
            return;
        }

        Toast.Show("Start Record");
        Debug.Log("StartRecord:" + width + "," + height + "," + fps + "," + bitrate + "," + onTrackingData);
        CameraHandle.StartCameraPreview(width, height, fps, bitrate, 0,
            (int)PXRCaptureRenderMode.PXRCapture_RenderMode_3D,
            () =>
            {
                string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string trackingFileName = $"CameraRecord_{timeStamp}.mp4";
                string filePath = Path.Combine("/sdcard/Download/", trackingFileName);
                LogWindow.Warn("Vision file path: " + filePath);
                CameraHandle.StartRecord(filePath);
                _recordTrackingData = onTrackingData;
                if (_recordTrackingData)
                    OnStartRecordTracking(width, height);
            });

        RecordBtn.SetOn(true);
        // PreviewCameraTog.gameObject.SetActive(true);
    }

    public void UpdateCameraParamsNew()
    {
        ResolutionDialog.Show("Setting the resolution", (width, height) =>
        {
            string cameraIntrinsics = CameraHandle.GetCameraIntrinsics(width, height);
            string cameraExtrinsics = CameraHandle.GetCameraExtrinsics();

            string saveStr = "CameraExtrinsics:" + cameraExtrinsics + "\n";
            saveStr += "cameraIntrinsics:" + cameraIntrinsics;
            WriteLocalText(string.Format("cameraParams_{0}x{1}", width, height), saveStr);
            Toast.Show("Parameter saved successfully!");
        }, null);
    }

    private void WriteLocalText(string fileName, string content)
    {
        string parentPath = Application.persistentDataPath;
        string filePath = Path.Combine(parentPath, fileName + ".txt");
        using (StreamWriter writer = new StreamWriter(filePath, false))
        {
            writer.Write(content + "\n");
        }

        Debug.Log("The file has been successfully written: " + filePath);
    }

    private void StopRecord()
    {
        Debug.Log(this + "StopRecord");
        LogWindow.Info("Stop record");
        if (_writer != null)
        {
            _writer.Close();
            _writer = null;
        }

        CameraHandle.StopPreview();
        CameraHandle.CloseCamera();
        RecordBtn.SetOn(false);
    }

    private void StopSendImage()
    {
        CameraHandle.StopPreview();
        CameraHandle.CloseCamera();
        CameraSendToBtn.SetOn(false);
    }

    public void OnRemoteCameraBtn()
    {
        Toast.Show("Request  camera screen on PC!");
        //Request camera screen on PC
        TcpHandler.SendFunctionValue("requestCameraList", "");
    }

    private StreamWriter _writer;
    private bool _recordTrackingData = false;
    private JsonData _trackingJsonData = new JsonData();
    private TrackingData _trackingData = new TrackingData();

    private void OnStartRecordTracking(int width, int height)
    {
        LogWindow.Info($"Start record tracking data: {width}x{height}");
        Debug.Log("OnStartRecordTracking");
        string timeStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string trackingFileName = $"trackingData_{timeStamp}.txt";
        string filePath = Path.Combine("/sdcard/Download/", trackingFileName);
        Debug.Log("trackingFilePath:" + filePath);
        LogWindow.Warn("Tracking file path:" + filePath);
        _writer = new StreamWriter(filePath, true);
        _writer.AutoFlush = true; // Enable automatic refresh to prevent data loss

        JsonData cameraParam = new JsonData();
        long nsTime = Utils.GetCurrentTimestamp();
        cameraParam["notice"] =
            "This is the timestamp and head pose information when obtaining the image for the first frame.";
        cameraParam["timeStampNs"] = nsTime;
        //Convert coordinate system to right-handed system (X right, Y up, Z in)

        string cameraExtrinsics = CameraHandle.GetCameraExtrinsics();
        string cameraIntrinsics = CameraHandle.GetCameraIntrinsics(width, height);
        cameraParam["cameraExtrinsics"] = cameraExtrinsics;
        cameraParam["cameraIntrinsics"] = cameraIntrinsics;
        _writer.WriteLine(cameraParam.ToJson());
    }

    private void Update()
    {
        if (_recordTrackingData)
        {
            if (_writer != null)
            {
                _trackingData.Get(ref _trackingJsonData);
                _writer.WriteLine(_trackingJsonData.ToJson());
            }
        }
    }

    private void OnApplicationPause(bool pauseStatus)
    {
        if (CameraHandle.GetCaptureState() == (int)PXRCaptureState.CAPTURE_STATE_CAMERA_OPENING)
        {
            if (pauseStatus)
            {
                //release camera
                CameraHandle.CloseCamera();
            }
            else
            {
                //reopen camera
                CameraHandle.OpenCamera();
            }
        }
    }
}
