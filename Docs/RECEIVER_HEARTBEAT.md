# Direct camera receiver heartbeat

The `CAMERA_ONLY` source keeps video and control on the existing direct path:

```text
camera sender -- H.264 frame TCP --> PICO :12345
PICO          -- OPEN/CLOSE TCP --> camera sender :13579
PICO          -- status UDP --> configured heartbeat destination :13580
```

The UDP heartbeat is independent of `TcpHandler` and PC Service. It never carries
video or tracking data and uses a non-blocking socket with no retry queue. A send
that would block is dropped so telemetry cannot delay Unity tracking or rendering.

## Camera-only use

1. Select `CAMERA_ONLY` in the video source dropdown.
2. Enter the camera sender IPv4 address.
3. Start a UDP listener on the heartbeat destination (default: the sender address,
   port `13580`).
4. Start the existing direct camera sender/control service.
5. Toggle Listen in the PICO application.

On an existing app installation, the bundled v3 configuration automatically
replaces an older persistent `video_source.yml` and retains the old file as a
`.pre-v3.bak`. A same/newer-version persistent file remains user-editable.

`CAMERA_ONLY` uses the existing `RemoteCamera` prefab `RawImage`, disables the SBS
left/right eye canvases, and keeps the flat panel aligned to `Camera.main`. It does
not send `StartReceivePcCamera` or `StopReceivePcCamera` to PC Service.

The bundled Android decoder accepts the existing length-prefixed TCP stream. This
change does not alter its video packet format or add an alternate media transport,
relay, or wide-FOV projection.

## Heartbeat configuration

The following `video_source.yml` properties are available:

| Property | Default for `CAMERA_ONLY` | Meaning |
|---|---:|---|
| `ReceiverHeartbeatEnabled` | `true` | Enable independent receiver telemetry |
| `ReceiverHeartbeatHost` | empty | Empty uses the entered camera sender IPv4 |
| `ReceiverHeartbeatPort` | `13580` | UDP destination port |
| `ReceiverHeartbeatHz` | `2` | Publish rate; validation allows 0.2–10 Hz |
| `ReceiverHeartbeatTimeoutMs` | `2000` | Consumer-side freshness deadline |
| `ReceiverFrameStaleMs` | `1000` | Maximum decoder-input and texture age for local visual availability |
| `VisualDependentProfile` | `false` | Observation-only default; does not authorize a visual-dependent control profile |
| `VisualProfile` | `camera_flat_head_follow_v1` | Profile identifier |

Only IPv4 literals are accepted. This avoids DNS work on the Unity thread.

The bundled `CAMERA_ONLY` profile publishes heartbeat telemetry for local
observation only. Setting `VisualDependentProfile=true` is an explicit future
integration step, not a normal setup step. Do that only after all three of the
following exist:

1. receiver heartbeat monitoring with the fail-closed freshness policy below;
2. an external supervisor that removes visual-dependent robot authority on stale
   or invalid telemetry; and
3. a completed safety review on the real headset, network, and robot.

While `VisualDependentProfile=false`, diagnostic state, frame ages, and rates are
still published, but `visual_profile_available` is always `false`.

## Protocol and safety rule

Each UDP datagram is one UTF-8 JSON object with `version=1` and schema
`xrobo_receiver_heartbeat_v1`. The gateway-canonical fields are:

- `version`, `session_id`, and monotonically increasing `sequence`;
- cumulative `decoded_frames` and `displayed_frames`;
- `unity_update_hz` and `decoder_state` (`playing` is the healthy value).

Additional diagnostics include:

- `sent_unix_ns`;
- receiver, inferred video connection, and decoder states;
- last receive, decoded-frame, and texture-update ages;
- receive/decoded/texture FPS and Unity Update Hz;
- `visual_profile_available` and the advertised consumer timeout.

`decoded_frames` increments when decoder output becomes observable through
`MediaDecoder.isUpdateFrame()`. `displayed_frames` increments only after
`MediaDecoder.updateTexture()` returns successfully.

The bundled AAR has no raw socket receive callback. Therefore
`last_receive_age_ms` is explicitly labelled by `receive_observation`:

- `decoder_input_queued`: age of the AAR MediaCodec input timestamp, read at the
  low heartbeat rate;
- `decoder_input_timestamp_unavailable`: the expected AAR field is absent,
  inaccessible, or has not received a frame; `last_receive_age_ms` is `-1`.

`raw_receive_observable` remains `false`; consumers must not reinterpret this as
wire-arrival latency. Visual availability fails closed while the decoder-input
timestamp is unavailable, even if Unity can still display decoded frames.

### Android receiver dependency

The low-rate observation uses JNI to read the Java field
`com.xrobotoolkit.video.PersistentMediaDecoder.lastTimestamp:J`. The maintained,
reproducibly built artifact is:

```text
Assets/Plugins/Android/xrobot-persistent-media-decoder.aar
SHA-256 e29532b43b30a939db8307cf500816e34d3072e82a9df2e2f051b5cce68d5a16
```

Its source and deterministic build script live in `Tools/android_receiver/`.
The upstream `robotassistant_lib-i18n-release.aar` remains available for unrelated
camera/control integrations but no longer owns the direct video receiver.

A missing or changed timestamp field is caught by the C# bridge: video decode
continues, receive age stays unknown, and `visual_profile_available` stays false.
Rebuild and run the repository preflight after any Android receiver change:

```bash
Tools/android_receiver/build.sh
python3 Tools/verify_receiver_heartbeat.py
```

For a future, explicitly enabled visual-dependent profile, the consuming
supervisor must apply this fail-closed rule:

```text
visual profile usable =
    heartbeat exists
    AND heartbeat age <= heartbeat_timeout_ms
    AND session_id is non-empty
    AND sequence > 0
    AND visual_profile_available == true
```

No heartbeat, an expired heartbeat, a new/unknown session, or a stale decoded
texture means a visual-dependent control profile is unavailable. UDP send success
is not proof that a consumer received a packet.

The heartbeat is unauthenticated UDP telemetry. It can be lost, reordered, or
spoofed, and therefore must never be the only robot safety interlock. The default
observation-only configuration does not grant or revoke robot control authority.

## Lifecycle

Every explicit Listen/reconnect creates a new random `session_id` and resets
`seq`. Stopping attempts one final `stopped` heartbeat, disposes the UDP socket,
releases MediaCodec, and destroys the old texture. UDP socket errors are recovered
by recreating the socket on the next scheduled publish.

The maintained Android receiver keeps TCP `12345` listening for the complete
camera-window lifetime. A sender disconnect closes only that client socket and
returns to `accept()` without rebuilding MediaCodec or the Unity texture. Its
explicit stop closes both sockets before joining the receiver thread, and the
listener uses address reuse so reconnect does not depend on TCP timeout expiry.

The sender waits for a complete SPS/PPS/IDR recovery access unit before opening
the video connection, so a cold ROS or encoder startup cannot occupy the PICO
socket without sending decodable data. Camera control uses a managed socket with
the official four-byte big-endian outer framing, a four-second connect deadline,
per-generation cancellation, and a bounded ordered send queue. Old connect/I/O
workers cannot update status or deliver callbacks after a retry.

## Required device validation

Unity compilation and PICO device testing are still required. At minimum compare
tracking-only against tracking plus `CAMERA_ONLY` and record:

- PC-side tracking rate and p95/p99 inter-arrival gap;
- heartbeat Unity Update Hz and texture-update FPS;
- glass-to-glass video latency;
- reconnect, app pause/resume, and 60-minute thermal behavior.
