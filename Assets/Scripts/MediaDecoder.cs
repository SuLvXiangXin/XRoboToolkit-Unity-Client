using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MediaDecoder
{
    private const string JavaClassName = "com.xrobotoolkit.video.PersistentMediaDecoder";
    private static AndroidJavaObject _javaObj = null;
    private static AndroidJavaClass _javaSystemClass;
    private static IntPtr _lastTimestampFieldId = IntPtr.Zero;

    private static AndroidJavaObject GetJavaObject()
    {
        if (_javaObj == null)
        {
            _javaObj = new AndroidJavaObject(JavaClassName);
        }

        return _javaObj;
    }

    public static void initialize(int unityTextureId, int width, int height)
    {
        LogWindow.Info($"Initializing MediaDecoder with texture ID: {unityTextureId}, size: {width}x{height}");
        GetJavaObject().Call("initialize", unityTextureId, width, height);
    }

    public static void startServer(int port, bool record)
    {
        LogWindow.Info($"Starting MediaDecoder server on port {port}, recording: {record}");
        GetJavaObject().Call("startServer", port, record);
    }

    public static bool isUpdateFrame()
    {
        return GetJavaObject().Call<bool>("isUpdateFrame");
    }

    public static void updateTexture()
    {
        GetJavaObject().Call("updateTexture");
    }

    public static void release()
    {
        LogWindow.Info("Releasing MediaDecoder resources");
        GetJavaObject().Call("release");
    }

    /// <summary>
    /// Returns the age of the last compressed frame accepted into MediaCodec.
    /// The bundled AAR does not expose raw socket receive timestamps, so this is
    /// deliberately reported as a decoder-input observation by the heartbeat.
    /// </summary>
    public static bool TryGetLastDecoderInputAgeMs(out long ageMs)
    {
        ageMs = -1;
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            AndroidJavaObject decoder = GetJavaObject();
            if (_lastTimestampFieldId == IntPtr.Zero)
            {
                _lastTimestampFieldId = AndroidJNI.GetFieldID(
                    decoder.GetRawClass(), "lastTimestamp", "J");
            }

            long lastTimestampUs = AndroidJNI.GetLongField(
                decoder.GetRawObject(), _lastTimestampFieldId);
            if (lastTimestampUs <= 0)
            {
                return false;
            }

            if (_javaSystemClass == null)
            {
                _javaSystemClass = new AndroidJavaClass("java.lang.System");
            }

            long nowUs = _javaSystemClass.CallStatic<long>("nanoTime") / 1000L;
            ageMs = Math.Max(0L, (nowUs - lastTimestampUs) / 1000L);
            return true;
        }
        catch
        {
            ageMs = -1;
            return false;
        }
#else
        return false;
#endif
    }
}
