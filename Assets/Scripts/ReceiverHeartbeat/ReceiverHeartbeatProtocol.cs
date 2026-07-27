using System;
using System.Globalization;
using System.Net;
using System.Text;

namespace XRoboToolkit.ReceiverHeartbeat
{
    [Serializable]
    public sealed class ReceiverHeartbeatSettings
    {
        public const int DefaultDestinationPort = 13580;
        public const float DefaultPublishHz = 2f;
        public const int DefaultHeartbeatTimeoutMs = 2000;
        public const int DefaultFrameStaleMs = 1000;

        public bool Enabled;
        public string DestinationHost = string.Empty;
        public int DestinationPort = DefaultDestinationPort;
        public float PublishHz = DefaultPublishHz;
        public int HeartbeatTimeoutMs = DefaultHeartbeatTimeoutMs;
        public int FrameStaleMs = DefaultFrameStaleMs;
        public bool VisualDependent;
        public string VisualProfile = "camera_flat_v1";
        public string SourceName = string.Empty;

        public bool TryValidate(out string error)
        {
            if (!Enabled)
            {
                error = VisualDependent
                    ? "Visual-dependent video requires receiver heartbeat."
                    : string.Empty;
                return !VisualDependent;
            }

            if (!IPAddress.TryParse(DestinationHost, out IPAddress address) ||
                address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                error = "Receiver heartbeat destination must be an IPv4 address literal.";
                return false;
            }

            if (DestinationPort < 1 || DestinationPort > 65535)
            {
                error = "Receiver heartbeat destination port must be in [1, 65535].";
                return false;
            }

            if (float.IsNaN(PublishHz) || float.IsInfinity(PublishHz) || PublishHz < 0.2f || PublishHz > 10f)
            {
                error = "Receiver heartbeat rate must be in [0.2, 10] Hz.";
                return false;
            }

            if (HeartbeatTimeoutMs < 100 || FrameStaleMs < 100)
            {
                error = "Receiver heartbeat and frame-stale timeouts must be at least 100 ms.";
                return false;
            }

            if (VisualDependent && string.IsNullOrWhiteSpace(VisualProfile))
            {
                error = "Visual-dependent video requires a non-empty visual profile name.";
                return false;
            }

            error = string.Empty;
            return true;
        }
    }

    [Serializable]
    public sealed class ReceiverHeartbeatSnapshot
    {
        public const string Schema = "xrobo_receiver_heartbeat_v1";
        public const int Version = 1;

        public string SessionId = string.Empty;
        public ulong Sequence;
        public long SentUnixTimeNs;
        public string SourceName = string.Empty;
        public string VisualProfile = string.Empty;
        public string DisplayMode = "flat";
        public bool FollowHead;
        public string ReceiverState = "stopped";
        public string ConnectionState = "stopped";
        public string DecoderState = "stopped";
        public string ReceiveObservation = "decoder_input_queued";
        public bool RawReceiveObservable;
        public long LastReceiveAgeMs = -1;
        public long LastDecodedAgeMs = -1;
        public long LastTextureUpdateAgeMs = -1;
        public double ReceiveFps;
        public double DecodedFps;
        public double TextureUpdateFps;
        public double UnityUpdateHz;
        public ulong DecodedFrames;
        public ulong DisplayedFrames;
        public bool VisualProfileAvailable;
        public int HeartbeatTimeoutMs;
        public ulong PublisherDroppedCount;
    }

    public static class ReceiverHeartbeatPolicy
    {
        public static bool IsRemoteVisualProfileAvailable(ReceiverHeartbeatSnapshot snapshot,
            long heartbeatAgeMs, int heartbeatTimeoutMs)
        {
            return snapshot != null &&
                   heartbeatAgeMs >= 0 &&
                   heartbeatTimeoutMs > 0 &&
                   heartbeatAgeMs <= heartbeatTimeoutMs &&
                   !string.IsNullOrWhiteSpace(snapshot.SessionId) &&
                   snapshot.Sequence > 0 &&
                   snapshot.VisualProfileAvailable;
        }

        public static bool IsLocalVisualProfileAvailable(bool visualDependent, bool heartbeatConfigured,
            bool receiverActive, bool decoderReady, long lastReceiveAgeMs, long lastTextureUpdateAgeMs,
            int frameStaleMs)
        {
            return visualDependent && heartbeatConfigured && receiverActive && decoderReady &&
                   lastReceiveAgeMs >= 0 && lastTextureUpdateAgeMs >= 0 && frameStaleMs > 0 &&
                   lastReceiveAgeMs <= frameStaleMs &&
                   lastTextureUpdateAgeMs <= frameStaleMs;
        }

        public static long AgeMilliseconds(double nowSeconds, double eventSeconds)
        {
            if (double.IsNaN(nowSeconds) || double.IsInfinity(nowSeconds) ||
                double.IsNaN(eventSeconds) || double.IsInfinity(eventSeconds) ||
                nowSeconds < 0 || eventSeconds < 0)
            {
                return -1;
            }

            double age = Math.Max(0, nowSeconds - eventSeconds) * 1000.0;
            return age >= long.MaxValue ? long.MaxValue : (long)Math.Round(age);
        }
    }

    public static class ReceiverHeartbeatJson
    {
        public static string Serialize(ReceiverHeartbeatSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            var builder = new StringBuilder(768);
            builder.Append('{');
            AppendString(builder, "schema", ReceiverHeartbeatSnapshot.Schema);
            AppendSigned(builder, "version", ReceiverHeartbeatSnapshot.Version);
            AppendString(builder, "session_id", snapshot.SessionId);
            AppendUnsigned(builder, "sequence", snapshot.Sequence);
            AppendSigned(builder, "sent_unix_ns", snapshot.SentUnixTimeNs);
            AppendString(builder, "source", snapshot.SourceName);
            AppendString(builder, "visual_profile", snapshot.VisualProfile);
            AppendString(builder, "display_mode", snapshot.DisplayMode);
            AppendBoolean(builder, "follow_head", snapshot.FollowHead);
            AppendString(builder, "receiver_state", snapshot.ReceiverState);
            AppendString(builder, "connection_state", snapshot.ConnectionState);
            AppendString(builder, "decoder_state", snapshot.DecoderState);
            AppendString(builder, "receive_observation", snapshot.ReceiveObservation);
            AppendBoolean(builder, "raw_receive_observable", snapshot.RawReceiveObservable);
            AppendSigned(builder, "last_receive_age_ms", snapshot.LastReceiveAgeMs);
            AppendSigned(builder, "last_decoded_age_ms", snapshot.LastDecodedAgeMs);
            AppendSigned(builder, "last_texture_update_age_ms", snapshot.LastTextureUpdateAgeMs);
            AppendDouble(builder, "receive_fps", snapshot.ReceiveFps);
            AppendDouble(builder, "decoded_fps", snapshot.DecodedFps);
            AppendDouble(builder, "texture_update_fps", snapshot.TextureUpdateFps);
            AppendDouble(builder, "unity_update_hz", snapshot.UnityUpdateHz);
            AppendUnsigned(builder, "decoded_frames", snapshot.DecodedFrames);
            AppendUnsigned(builder, "displayed_frames", snapshot.DisplayedFrames);
            AppendBoolean(builder, "visual_profile_available", snapshot.VisualProfileAvailable);
            AppendSigned(builder, "heartbeat_timeout_ms", snapshot.HeartbeatTimeoutMs);
            AppendUnsigned(builder, "publisher_dropped_count", snapshot.PublisherDroppedCount);
            builder.Append('}');
            return builder.ToString();
        }

        private static void AppendName(StringBuilder builder, string name)
        {
            if (builder.Length > 1) builder.Append(',');
            AppendEscaped(builder, name);
            builder.Append(':');
        }

        private static void AppendString(StringBuilder builder, string name, string value)
        {
            AppendName(builder, name);
            AppendEscaped(builder, value ?? string.Empty);
        }

        private static void AppendBoolean(StringBuilder builder, string name, bool value)
        {
            AppendName(builder, name);
            builder.Append(value ? "true" : "false");
        }

        private static void AppendSigned(StringBuilder builder, string name, long value)
        {
            AppendName(builder, name);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendUnsigned(StringBuilder builder, string name, ulong value)
        {
            AppendName(builder, name);
            builder.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        private static void AppendDouble(StringBuilder builder, string name, double value)
        {
            AppendName(builder, name);
            double finiteValue = double.IsNaN(value) || double.IsInfinity(value) ? 0.0 : value;
            builder.Append(finiteValue.ToString("0.###", CultureInfo.InvariantCulture));
        }

        private static void AppendEscaped(StringBuilder builder, string value)
        {
            builder.Append('"');
            foreach (char character in value)
            {
                switch (character)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\b': builder.Append("\\b"); break;
                    case '\f': builder.Append("\\f"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(character);
                        }
                        break;
                }
            }
            builder.Append('"');
        }
    }
}
