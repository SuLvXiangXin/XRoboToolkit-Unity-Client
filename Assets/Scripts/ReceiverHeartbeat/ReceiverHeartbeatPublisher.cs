using System;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace XRoboToolkit.ReceiverHeartbeat
{
    /// <summary>
    /// Best-effort UDP publisher. The socket is explicitly non-blocking and no
    /// queue is retained: a heartbeat is sent now or dropped.
    /// </summary>
    public sealed class ReceiverHeartbeatPublisher : IDisposable
    {
        private ReceiverHeartbeatSettings _settings;
        private IPEndPoint _destination;
        private Socket _socket;

        public bool IsConfigured => _settings != null && _destination != null;
        public ulong SentCount { get; private set; }
        public ulong DroppedCount { get; private set; }
        public string LastError { get; private set; } = string.Empty;

        public bool Configure(ReceiverHeartbeatSettings settings, out string error)
        {
            DisposeSocket();
            _settings = null;
            _destination = null;
            SentCount = 0;
            DroppedCount = 0;
            LastError = string.Empty;

            if (settings == null)
            {
                error = "Receiver heartbeat settings are missing.";
                LastError = error;
                return false;
            }

            if (!settings.TryValidate(out error))
            {
                LastError = error;
                return false;
            }

            if (!settings.Enabled)
            {
                error = string.Empty;
                return false;
            }

            IPAddress address = IPAddress.Parse(settings.DestinationHost);
            _settings = settings;
            _destination = new IPEndPoint(address, settings.DestinationPort);
            return EnsureSocket(out error);
        }

        public bool TryPublish(ReceiverHeartbeatSnapshot snapshot, out string error)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            if (!IsConfigured)
            {
                error = "Receiver heartbeat publisher is not configured.";
                LastError = error;
                DroppedCount++;
                return false;
            }

            if (!EnsureSocket(out error))
            {
                DroppedCount++;
                return false;
            }

            byte[] payload = Encoding.UTF8.GetBytes(ReceiverHeartbeatJson.Serialize(snapshot));
            try
            {
                int sent = _socket.SendTo(payload, 0, payload.Length, SocketFlags.None, _destination);
                if (sent != payload.Length)
                {
                    error = $"Receiver heartbeat UDP send was partial: {sent}/{payload.Length} bytes.";
                    LastError = error;
                    DroppedCount++;
                    return false;
                }

                SentCount++;
                LastError = string.Empty;
                error = string.Empty;
                return true;
            }
            catch (SocketException exception) when (
                exception.SocketErrorCode == SocketError.WouldBlock ||
                exception.SocketErrorCode == SocketError.IOPending ||
                exception.SocketErrorCode == SocketError.NoBufferSpaceAvailable)
            {
                error = "Receiver heartbeat UDP send would block; heartbeat dropped.";
                LastError = error;
                DroppedCount++;
                return false;
            }
            catch (Exception exception)
            {
                error = "Receiver heartbeat UDP send failed: " + exception.Message;
                LastError = error;
                DroppedCount++;
                DisposeSocket();
                return false;
            }
        }

        public void Dispose()
        {
            DisposeSocket();
            _settings = null;
            _destination = null;
        }

        private bool EnsureSocket(out string error)
        {
            if (_socket != null)
            {
                error = string.Empty;
                return true;
            }

            try
            {
                _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    Blocking = false
                };
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = "Receiver heartbeat UDP socket creation failed: " + exception.Message;
                LastError = error;
                DisposeSocket();
                return false;
            }
        }

        private void DisposeSocket()
        {
            if (_socket == null) return;
            try
            {
                _socket.Close();
            }
            catch
            {
                // Best-effort telemetry must never fail the video lifecycle.
            }
            finally
            {
                _socket = null;
            }
        }
    }
}
