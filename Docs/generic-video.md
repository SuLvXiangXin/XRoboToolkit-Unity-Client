# Independent mono camera preview

This extension is based on official commit
`cdc53166b0bf412efae71046c6a225eb5091605f`. Select **GENERIC** in the camera list
and click **Open video**. Playback is off on every application launch. If PC
Service is connected (WORKING), video reuses its host
without another IP dialog. Otherwise the dialog asks for the sender computer's
IP and remembers it. The picture renders in front of the original menu. Click
the **×** on the picture's upper-right corner (or **Close video** in the camera
menu) to hide it immediately and disconnect video. The sender stops encoding
and transmitting to PICO when this connection closes; tracking and the camera's
existing ROS publication continue independently. Hidden controls behind the picture
cannot receive pointer clicks. For a sender on another
computer or a USB connection, use the explicit video settings file below.
The matching Python `video_sender` package from the Sender repository listens on
TCP 13579. This mode has its own protocol/socket/workers and does not request
camera/audio forwarding through PC Service.

The sender chooses the image dimensions on every connection. The app validates
them against the hardware H.264 decoder, creates a matching GPU texture and shows
one image to both eyes, preserving aspect ratio. The app does not know the camera
model. Crop stereo input on the sender side. Switching topic/resolution reconnects
the video session, without restarting the app or tracking connection.

`GenericVideoReceiver.java` performs TCP input and MediaCodec decoding on background
threads. `GenericVideoView` checks a coalesced frame flag each Unity Update and
only blits when a new decoded frame is available, at most once per Update. The
sender limits the source rate (30 fps by default). Android must keep the
project's OpenGLES3 / single graphics thread settings for this plugin. Rendering
still shares headset resources with tracking, so compare both before accepting
a deployment. No changes to TcpHandler or pose sampling/sending are required.

Unavailable/stale video is hidden after 500 ms and labelled; decoder congestion
reconnects for a fresh IDR instead of skipping reference frames. Codec release and
network shutdown do not block Unity Update. The app retries a disconnected sender.
Dimensions must be even, 16–4096, and supported by the actual encoder/decoder.

## Build and install

Use a licensed Unity editor compatible with `ProjectSettings/ProjectVersion.txt`
and the Android module, SDK, NDK and JDK required by that editor.

```sh
/absolute/path/to/Unity -batchmode -nographics -quit -buildTarget Android \
  -projectPath /absolute/path/to/XRoboToolkit-Unity-Client \
  -executeMethod GenericVideoBuilder.BuildBatch -logFile /absolute/path/to/build.log
adb install -r Builds/XRoboToolkit-GenericVideo.apk
```

`XRBT_VIDEO_APK` can override the output path. The build uses ARM64/IL2CPP and the
debug keystore for local installation. The package is
`com.xrobotoolkit.client.genericvideo` (XRoboToolkit Video), installed alongside
the original `com.xrobotoolkit.client`; it has separate settings. Stop the original
app and run only one tracking client at a time. Connect the new app to PC Service
and enable the same Head/Controller/Send/Full-body options as the baseline.

## Optional independent video address / USB

```sh
adb reverse tcp:23579 tcp:13579
```

Use video host `127.0.0.1` and port `23579` in the settings file below. The existing
legacy control server occupies device port 13579, so the forwarded device port
must differ. PC Service continues using its original connection.
To override the default PC Service host, write a small JSON file to the app's
external files directory, then atomically rename it to `generic_video.json`:

```json
{"host":"127.0.0.1","port":23579}
```

Path: `/sdcard/Android/data/com.xrobotoolkit.client.genericvideo/files/generic_video.json`.
This file only selects the endpoint when **Open video** is clicked. It never
starts or stops playback automatically; the former `enabled` field is ignored,
including in files left by older testing builds. Close and reopen video to apply
an endpoint change. Delete the file to return to the PC Service host default.
It never changes pose settings. Video ports assume a trusted
network or USB and are not authenticated/encrypted.

Logcat lines containing `GENERIC_VIDEO` report Unity Update frequency and maximum
frame interval every five seconds, including when video is off. While playing,
the sender receives decoded/displayed counts and display age. These ages are not
end-to-end latency. Compare ROS receive intervals and unique source timestamps
with video off/on, then test disconnect, recovery, source and resolution changes.
