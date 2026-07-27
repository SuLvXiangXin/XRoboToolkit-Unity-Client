using NUnit.Framework;

namespace XRoboToolkit.ReceiverHeartbeat.Tests
{
    public sealed class ReceiverHeartbeatProtocolTests
    {
        [Test]
        public void VisualDependentSettingsRequireHeartbeat()
        {
            var settings = new ReceiverHeartbeatSettings
            {
                Enabled = false,
                VisualDependent = true
            };

            Assert.That(settings.TryValidate(out string error), Is.False);
            Assert.That(error, Does.Contain("requires receiver heartbeat"));
        }

        [Test]
        public void ObservationOnlySettingsDoNotRequireHeartbeat()
        {
            var settings = new ReceiverHeartbeatSettings
            {
                Enabled = false,
                VisualDependent = false
            };

            Assert.That(settings.TryValidate(out string error), Is.True, error);
        }

        [Test]
        public void EnabledSettingsRequireIpv4LiteralAndValidBounds()
        {
            var settings = ValidSettings();
            Assert.That(settings.TryValidate(out string validError), Is.True, validError);

            settings.DestinationHost = "camera.local";
            Assert.That(settings.TryValidate(out string hostError), Is.False);
            Assert.That(hostError, Does.Contain("IPv4"));

            settings.DestinationHost = "127.0.0.1";
            settings.PublishHz = 20f;
            Assert.That(settings.TryValidate(out string rateError), Is.False);
            Assert.That(rateError, Does.Contain("rate"));
        }

        [Test]
        public void NoOrStaleHeartbeatMakesRemoteVisualProfileUnavailable()
        {
            Assert.That(ReceiverHeartbeatPolicy.IsRemoteVisualProfileAvailable(null, 0, 2000), Is.False);

            var snapshot = new ReceiverHeartbeatSnapshot
            {
                SessionId = "session-a",
                Sequence = 1,
                VisualProfileAvailable = true
            };

            Assert.That(ReceiverHeartbeatPolicy.IsRemoteVisualProfileAvailable(snapshot, 2001, 2000), Is.False);
            Assert.That(ReceiverHeartbeatPolicy.IsRemoteVisualProfileAvailable(snapshot, 2000, 2000), Is.True);
        }

        [Test]
        public void LocalVisualProfileRequiresExplicitOptInFreshTextureAndConfiguredHeartbeat()
        {
            Assert.That(ReceiverHeartbeatPolicy.IsLocalVisualProfileAvailable(false, true, true, true, 10, 10, 1000), Is.False);
            Assert.That(ReceiverHeartbeatPolicy.IsLocalVisualProfileAvailable(true, false, true, true, 10, 10, 1000), Is.False);
            Assert.That(ReceiverHeartbeatPolicy.IsLocalVisualProfileAvailable(true, true, true, true, -1, 10, 1000), Is.False);
            Assert.That(ReceiverHeartbeatPolicy.IsLocalVisualProfileAvailable(true, true, true, true, 1001, 10, 1000), Is.False);
            Assert.That(ReceiverHeartbeatPolicy.IsLocalVisualProfileAvailable(true, true, true, true, 10, 1001, 1000), Is.False);
            Assert.That(ReceiverHeartbeatPolicy.IsLocalVisualProfileAvailable(true, true, true, true, 10, 1000, 1000), Is.True);
        }

        [Test]
        public void AgeMillisecondsUsesUnknownSentinelUntilFirstEvent()
        {
            Assert.That(ReceiverHeartbeatPolicy.AgeMilliseconds(10.0, -1.0), Is.EqualTo(-1));
            Assert.That(ReceiverHeartbeatPolicy.AgeMilliseconds(10.0, 9.25), Is.EqualTo(750));
            Assert.That(ReceiverHeartbeatPolicy.AgeMilliseconds(9.0, 10.0), Is.EqualTo(0));
        }

        [Test]
        public void JsonIncludesRequiredStateAndEscapesValues()
        {
            var snapshot = new ReceiverHeartbeatSnapshot
            {
                SessionId = "session-\"a",
                Sequence = 7,
                ReceiverState = "streaming",
                LastReceiveAgeMs = 12,
                LastDecodedAgeMs = 13,
                LastTextureUpdateAgeMs = 14,
                UnityUpdateHz = 89.95,
                VisualProfileAvailable = true
            };

            string json = ReceiverHeartbeatJson.Serialize(snapshot);

            Assert.That(json, Does.Contain("\"schema\":\"xrobo_receiver_heartbeat_v1\""));
            Assert.That(json, Does.Contain("\"session_id\":\"session-\\\"a\""));
            Assert.That(json, Does.Contain("\"version\":1"));
            Assert.That(json, Does.Contain("\"sequence\":7"));
            Assert.That(json, Does.Contain("\"last_receive_age_ms\":12"));
            Assert.That(json, Does.Contain("\"unity_update_hz\":89.95"));
            Assert.That(json, Does.Contain("\"visual_profile_available\":true"));
        }

        [Test]
        public void JsonMatchesGatewayCanonicalGoldenPayload()
        {
            var snapshot = new ReceiverHeartbeatSnapshot
            {
                SessionId = "session-a",
                Sequence = 42,
                SentUnixTimeNs = 123456789,
                SourceName = "CAMERA_ONLY",
                VisualProfile = "camera_flat_head_follow_v1",
                DisplayMode = "flat",
                FollowHead = true,
                ReceiverState = "streaming",
                ConnectionState = "streaming_inferred",
                DecoderState = "playing",
                ReceiveObservation = "decoder_input_queued",
                RawReceiveObservable = false,
                LastReceiveAgeMs = 10,
                LastDecodedAgeMs = 11,
                LastTextureUpdateAgeMs = 12,
                ReceiveFps = 29.9,
                DecodedFps = 29.8,
                TextureUpdateFps = 29.7,
                UnityUpdateHz = 89.9,
                DecodedFrames = 100,
                DisplayedFrames = 99,
                VisualProfileAvailable = true,
                HeartbeatTimeoutMs = 2000,
                PublisherDroppedCount = 3
            };

            const string expected =
                "{\"schema\":\"xrobo_receiver_heartbeat_v1\",\"version\":1," +
                "\"session_id\":\"session-a\",\"sequence\":42,\"sent_unix_ns\":123456789," +
                "\"source\":\"CAMERA_ONLY\",\"visual_profile\":\"camera_flat_head_follow_v1\"," +
                "\"display_mode\":\"flat\",\"follow_head\":true,\"receiver_state\":\"streaming\"," +
                "\"connection_state\":\"streaming_inferred\",\"decoder_state\":\"playing\"," +
                "\"receive_observation\":\"decoder_input_queued\",\"raw_receive_observable\":false," +
                "\"last_receive_age_ms\":10,\"last_decoded_age_ms\":11," +
                "\"last_texture_update_age_ms\":12,\"receive_fps\":29.9,\"decoded_fps\":29.8," +
                "\"texture_update_fps\":29.7,\"unity_update_hz\":89.9,\"decoded_frames\":100," +
                "\"displayed_frames\":99,\"visual_profile_available\":true," +
                "\"heartbeat_timeout_ms\":2000,\"publisher_dropped_count\":3}";

            Assert.That(ReceiverHeartbeatJson.Serialize(snapshot), Is.EqualTo(expected));
        }

        private static ReceiverHeartbeatSettings ValidSettings()
        {
            return new ReceiverHeartbeatSettings
            {
                Enabled = true,
                DestinationHost = "127.0.0.1",
                DestinationPort = 13580,
                PublishHz = 2f,
                HeartbeatTimeoutMs = 2000,
                FrameStaleMs = 1000,
                VisualDependent = true,
                VisualProfile = "camera_flat_head_follow_v1"
            };
        }
    }
}
