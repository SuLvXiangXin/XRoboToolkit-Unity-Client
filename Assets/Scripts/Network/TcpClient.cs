using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using ManagedTcpClient = System.Net.Sockets.TcpClient;

namespace Robot.V2.Network
{
    public enum ClientStatus
    {
        None,
        Connecting,
        Connected,
        Disconnected,
    }

    /// <summary>
    /// Managed camera-control client for the official TCP 13579 protocol.
    /// Video bytes still use the Android MediaDecoder server on TCP 12345.
    /// </summary>
    public sealed class TcpClient
    {
        private const int ConnectTimeoutMs = 4000;
        private const int SendTimeoutMs = 1000;
        private const int MaxControlBodyBytes = 10 * 1024 * 1024;
        private const int MaxQueuedMessages = 32;
        private const int MaxQueuedBytes = 1024 * 1024;

        private static readonly object Sync = new object();
        private static readonly TcpClient _callbackProxy = new TcpClient();
        private static int _generation;
        private static int _status = (int)ClientStatus.None;
        private static ConnectionState _connection;

        public static TcpClient CallbackProxy => _callbackProxy;
        public static ClientStatus Status => (ClientStatus)Volatile.Read(ref _status);
        public static string LocalAddress
        {
            get
            {
                lock (Sync)
                {
                    return _connection?.LocalAddress ?? string.Empty;
                }
            }
        }

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<byte[], int> OnDataReceived;
        public event Action<string, Exception> OnError;

        private sealed class ConnectionState
        {
            public readonly int Generation;
            public readonly ManagedTcpClient Client = new ManagedTcpClient(AddressFamily.InterNetwork);
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
            public readonly Queue<byte[]> SendQueue = new Queue<byte[]>();
            public readonly SemaphoreSlim SendSignal = new SemaphoreSlim(0, MaxQueuedMessages);
            public NetworkStream Stream;
            public string LocalAddress = string.Empty;
            public int QueuedBytes;

            public ConnectionState(int generation)
            {
                Generation = generation;
            }
        }

        public static void ConnectToServer(string ip, int port, Action onConnected)
        {
            if (!IPAddress.TryParse(ip, out IPAddress address) ||
                address.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new ArgumentException("Camera control host must be an IPv4 address literal.", nameof(ip));
            }
            if (port < 1 || port > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(port));
            }

            LogWindow.Info($"Attempting to connect to camera control server at {ip}:{port}");
            ConnectionState state;
            ConnectionState previous;
            lock (Sync)
            {
                previous = _connection;
                state = new ConnectionState(Interlocked.Increment(ref _generation));
                _connection = state;
                _callbackProxy.OnConnected = onConnected;
                SetStatus(ClientStatus.Connecting);
            }
            CancelAndClose(previous);
            _ = ConnectAsync(state, address, port);
        }

        public static void Send(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (data.Length <= 0 || data.Length > MaxControlBodyBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(data),
                    $"Camera control body must be in [1, {MaxControlBodyBytes}] bytes.");
            }

            byte[] framed = FrameControlBody(data);
            ConnectionState state;
            lock (Sync)
            {
                state = _connection;
                if (state == null || state.Stream == null || Status != ClientStatus.Connected)
                {
                    throw new InvalidOperationException("Camera control client is not connected.");
                }
                if (state.SendQueue.Count >= MaxQueuedMessages ||
                    state.QueuedBytes + framed.Length > MaxQueuedBytes)
                {
                    throw new InvalidOperationException("Camera control send queue is full.");
                }

                state.SendQueue.Enqueue(framed);
                state.QueuedBytes += framed.Length;
                state.SendSignal.Release();
            }
        }

        public static void Disconnect()
        {
            ConnectionState state;
            int notificationGeneration;
            lock (Sync)
            {
                state = _connection;
                if (state == null)
                {
                    SetStatus(ClientStatus.Disconnected);
                    return;
                }
                _connection = null;
                notificationGeneration = Interlocked.Increment(ref _generation);
                SetStatus(ClientStatus.Disconnected);
            }

            CancelAndClose(state);
            PostForGeneration(notificationGeneration, _callbackProxy.NotifyDisconnected);
        }

        private static async Task ConnectAsync(ConnectionState state, IPAddress address, int port)
        {
            try
            {
                Task connectTask = state.Client.ConnectAsync(address, port);
                Task deadlineTask = Task.Delay(ConnectTimeoutMs, state.Cancellation.Token);
                Task completed = await Task.WhenAny(connectTask, deadlineTask).ConfigureAwait(false);
                if (completed != connectTask)
                {
                    ObserveFault(connectTask);
                    if (state.Cancellation.IsCancellationRequested) return;
                    throw new TimeoutException($"Camera control connect timed out after {ConnectTimeoutMs} ms.");
                }

                await connectTask.ConfigureAwait(false);
                state.Client.NoDelay = true;
                NetworkStream stream = state.Client.GetStream();
                string localAddress =
                    (state.Client.Client.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? string.Empty;
                bool accepted;
                lock (Sync)
                {
                    accepted = IsCurrentLocked(state) && !state.Cancellation.IsCancellationRequested;
                    if (accepted)
                    {
                        state.Stream = stream;
                        state.LocalAddress = localAddress;
                        SetStatus(ClientStatus.Connected);
                    }
                }
                if (!accepted)
                {
                    CancelAndClose(state);
                    return;
                }

                PostForGeneration(state.Generation, _callbackProxy.NotifyConnected);
                _ = SendLoopAsync(state);
                _ = ReceiveLoopAsync(state);
            }
            catch (Exception exception)
            {
                FailCurrent(state, "Camera control connect failed", exception, true);
            }
        }

        private static async Task SendLoopAsync(ConnectionState state)
        {
            try
            {
                while (!state.Cancellation.IsCancellationRequested)
                {
                    await state.SendSignal.WaitAsync(state.Cancellation.Token).ConfigureAwait(false);
                    byte[] packet;
                    lock (Sync)
                    {
                        if (!IsCurrentLocked(state)) return;
                        if (state.SendQueue.Count == 0) continue;
                        packet = state.SendQueue.Dequeue();
                        state.QueuedBytes -= packet.Length;
                    }

                    Task writeTask = state.Stream.WriteAsync(
                        packet, 0, packet.Length, state.Cancellation.Token);
                    Task deadlineTask = Task.Delay(SendTimeoutMs, state.Cancellation.Token);
                    Task completed = await Task.WhenAny(writeTask, deadlineTask).ConfigureAwait(false);
                    if (completed != writeTask)
                    {
                        ObserveFault(writeTask);
                        state.Cancellation.Token.ThrowIfCancellationRequested();
                        throw new TimeoutException(
                            $"Camera control send timed out after {SendTimeoutMs} ms.");
                    }
                    await writeTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when this generation is closed or replaced.
            }
            catch (Exception exception)
            {
                FailCurrent(state, "Camera control send failed", exception, true);
            }
        }

        private static async Task ReceiveLoopAsync(ConnectionState state)
        {
            try
            {
                byte[] header = new byte[4];
                while (!state.Cancellation.IsCancellationRequested)
                {
                    if (!await ReadExactAsync(state, header, 4).ConfigureAwait(false))
                    {
                        FailCurrent(state, "Camera control server disconnected", null, false);
                        return;
                    }

                    int bodyLength = (header[0] << 24) | (header[1] << 16) |
                                     (header[2] << 8) | header[3];
                    if (bodyLength <= 0 || bodyLength > MaxControlBodyBytes)
                    {
                        throw new InvalidOperationException(
                            $"Invalid camera control frame length: {bodyLength}.");
                    }

                    byte[] body = new byte[bodyLength];
                    if (!await ReadExactAsync(state, body, bodyLength).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("Camera control frame ended early.");
                    }
                    PostForGeneration(
                        state.Generation,
                        () => _callbackProxy.NotifyDataReceived(body, body.Length));
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when this generation is closed or replaced.
            }
            catch (Exception exception)
            {
                FailCurrent(state, "Camera control receive failed", exception, true);
            }
        }

        private static async Task<bool> ReadExactAsync(
            ConnectionState state, byte[] buffer, int length)
        {
            int offset = 0;
            while (offset < length)
            {
                int read = await state.Stream.ReadAsync(
                    buffer, offset, length - offset, state.Cancellation.Token).ConfigureAwait(false);
                if (read == 0) return false;
                offset += read;
            }
            return true;
        }

        private static byte[] FrameControlBody(byte[] body)
        {
            int length = body.Length;
            byte[] packet = new byte[length + 4];
            packet[0] = (byte)(length >> 24);
            packet[1] = (byte)(length >> 16);
            packet[2] = (byte)(length >> 8);
            packet[3] = (byte)length;
            Buffer.BlockCopy(body, 0, packet, 4, length);
            return packet;
        }

        private static void FailCurrent(
            ConnectionState state, string message, Exception exception, bool reportError)
        {
            int notificationGeneration;
            lock (Sync)
            {
                if (!IsCurrentLocked(state)) return;
                _connection = null;
                notificationGeneration = Interlocked.Increment(ref _generation);
                SetStatus(ClientStatus.Disconnected);
            }

            CancelAndClose(state);
            if (reportError)
            {
                Exception reported = exception ?? new IOException(message);
                PostForGeneration(
                    notificationGeneration,
                    () => _callbackProxy.NotifyError(message, reported));
            }
            else
            {
                PostForGeneration(notificationGeneration, _callbackProxy.NotifyDisconnected);
            }
        }

        private static bool IsCurrentLocked(ConnectionState state)
        {
            return ReferenceEquals(_connection, state) &&
                   state.Generation == Volatile.Read(ref _generation);
        }

        private static void CancelAndClose(ConnectionState state)
        {
            if (state == null) return;
            try
            {
                state.Cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            try
            {
                state.Stream?.Close();
            }
            catch (Exception)
            {
            }
            try
            {
                state.Client.Close();
            }
            catch (Exception)
            {
            }
            lock (Sync)
            {
                state.SendQueue.Clear();
                state.QueuedBytes = 0;
            }
        }

        private static void ObserveFault(Task task)
        {
            _ = task.ContinueWith(
                completed => { _ = completed.Exception; },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private static void SetStatus(ClientStatus status)
        {
            Volatile.Write(ref _status, (int)status);
        }

        private static void PostForGeneration(int generation, Action action)
        {
            EventExecutor.ExecuteInUpdate(() =>
            {
                if (generation != Volatile.Read(ref _generation)) return;
                action();
            });
        }

        private void NotifyConnected()
        {
            Utils.WriteLog("TcpClient(C#)", "Managed TCP client connected");
            LogWindow.Info("TCP Client successfully connected to camera control server");
            OnConnected?.Invoke();
        }

        private void NotifyDisconnected()
        {
            Utils.WriteLog("TcpClient(C#)", "Managed TCP client disconnected");
            LogWindow.Warn("TCP Client disconnected from camera control server");
            OnDisconnected?.Invoke();
        }

        private void NotifyDataReceived(byte[] data, int length)
        {
            Utils.WriteLog("TcpClient(C#)", $"Managed TCP client received {length} bytes");
            OnDataReceived?.Invoke(data, length);
        }

        private void NotifyError(string message, Exception exception)
        {
            Utils.WriteLog("TcpClient(C#)", $"Managed TCP client error: {message}: {exception}");
            LogWindow.Error($"TCP Client error: {message}: {exception.Message}");
            OnError?.Invoke(message, exception);
        }
    }
}
