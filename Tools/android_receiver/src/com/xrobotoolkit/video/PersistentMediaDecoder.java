package com.xrobotoolkit.video;

import android.media.MediaCodec;
import android.media.MediaFormat;
import android.util.Log;

import com.picoxr.fbolibrary.FBOPlugin;

import java.io.DataInputStream;
import java.io.EOFException;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.net.SocketException;
import java.nio.ByteBuffer;

/**
 * MediaCodec receiver with one listener for the complete camera-window lifetime.
 * A disconnected sender returns to accept() without rebuilding MediaCodec or the
 * Unity texture.
 */
public final class PersistentMediaDecoder {
    private static final String TAG = "PersistentMediaDecoder";
    private static final int MAX_ACCESS_UNIT_BYTES = 16 * 1024 * 1024;
    private static final int INITIAL_BUFFER_BYTES = 1024 * 1024;
    private static final int INPUT_WAIT_US = 10_000;
    private static final int MAX_OUTPUTS_PER_INPUT = 16;
    private static final int COLOR_FORMAT_QCOM_YUV420_TILED = 0x7f420888;

    private final Object socketLock = new Object();
    private MediaCodec mediaCodec;
    private FBOPlugin fboPlugin;
    private Thread serverThread;
    private ServerSocket serverSocket;
    private Socket clientSocket;
    private volatile boolean running;

    // Read by the Unity heartbeat bridge through JNI.
    public volatile long lastTimestamp;

    public synchronized void initialize(int textureId, int width, int height) throws Exception {
        release();
        try {
            fboPlugin = new FBOPlugin();
            fboPlugin.init();
            fboPlugin.BuildTexture(textureId, width, height);

            MediaFormat format = MediaFormat.createVideoFormat("video/avc", width, height);
            format.setInteger(MediaFormat.KEY_COLOR_FORMAT, COLOR_FORMAT_QCOM_YUV420_TILED);
            format.setInteger(MediaFormat.KEY_PRIORITY, 0);
            format.setInteger("low-latency", 1);

            mediaCodec = MediaCodec.createDecoderByType("video/avc");
            mediaCodec.configure(format, fboPlugin.getSurface(), null, 0);
            mediaCodec.start();
            lastTimestamp = 0L;
            Log.i(TAG, "MediaCodec initialized: " + width + "x" + height);
        } catch (Exception exception) {
            release();
            throw exception;
        }
    }

    public void startServer(int port, boolean record) throws IOException {
        stopServer();
        if (record) {
            Log.w(TAG, "Recording is not supported by the persistent receiver.");
        }

        ServerSocket listener = new ServerSocket();
        listener.setReuseAddress(true);
        listener.bind(new InetSocketAddress(port));
        synchronized (socketLock) {
            running = true;
            serverSocket = listener;
            serverThread = new Thread(() -> acceptLoop(listener), "persistent-video-receiver");
            serverThread.start();
        }
        Log.i(TAG, "Listening on TCP " + port);
    }

    private void acceptLoop(ServerSocket listener) {
        try {
            while (running) {
                Socket socket = null;
                try {
                    socket = listener.accept();
                    socket.setTcpNoDelay(true);
                    synchronized (socketLock) {
                        if (!running) {
                            closeQuietly(socket);
                            break;
                        }
                        clientSocket = socket;
                    }
                    Log.i(TAG, "Video sender connected: " + socket.getRemoteSocketAddress());
                    receiveClient(socket);
                } catch (SocketException exception) {
                    if (running) {
                        Log.w(TAG, "Video socket failed; accepting a new sender", exception);
                    }
                } catch (IOException exception) {
                    if (running) {
                        Log.w(TAG, "Video sender failed; accepting a new sender", exception);
                    }
                } finally {
                    synchronized (socketLock) {
                        if (clientSocket == socket) {
                            clientSocket = null;
                        }
                    }
                    closeQuietly(socket);
                }
            }
        } finally {
            synchronized (socketLock) {
                if (serverSocket == listener) {
                    serverSocket = null;
                }
            }
            closeQuietly(listener);
            Log.i(TAG, "TCP receiver stopped");
        }
    }

    private void receiveClient(Socket socket) throws IOException {
        byte[] buffer = new byte[INITIAL_BUFFER_BYTES];
        DataInputStream input = new DataInputStream(socket.getInputStream());
        while (running) {
            final int size;
            try {
                size = input.readInt();
            } catch (EOFException endOfStream) {
                return;
            }
            if (size <= 0 || size > MAX_ACCESS_UNIT_BYTES) {
                throw new IOException("Invalid H.264 access-unit size: " + size);
            }
            if (buffer.length < size) {
                int capacity = Math.min(
                    MAX_ACCESS_UNIT_BYTES,
                    Math.max(size, Math.max(INITIAL_BUFFER_BYTES, buffer.length * 2))
                );
                buffer = new byte[capacity];
            }
            input.readFully(buffer, 0, size);
            queueAndDrain(buffer, size);
        }
    }

    private void queueAndDrain(byte[] payload, int size) throws IOException {
        MediaCodec codec = mediaCodec;
        if (codec == null) {
            throw new IOException("MediaCodec is not initialized");
        }
        try {
            int inputIndex = codec.dequeueInputBuffer(INPUT_WAIT_US);
            if (inputIndex < 0) {
                Log.w(TAG, "Dropping H.264 access unit: no decoder input buffer");
                return;
            }
            ByteBuffer inputBuffer = codec.getInputBuffer(inputIndex);
            if (inputBuffer == null || inputBuffer.capacity() < size) {
                throw new IOException("Decoder input buffer is too small for " + size + " bytes");
            }
            inputBuffer.clear();
            inputBuffer.put(payload, 0, size);
            long timestampUs = Math.max(lastTimestamp + 1L, System.nanoTime() / 1000L);
            codec.queueInputBuffer(inputIndex, 0, size, timestampUs, 0);
            lastTimestamp = timestampUs;

            MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
            for (int count = 0; count < MAX_OUTPUTS_PER_INPUT; count++) {
                int outputIndex = codec.dequeueOutputBuffer(info, 0L);
                if (outputIndex >= 0) {
                    codec.releaseOutputBuffer(outputIndex, true);
                    continue;
                }
                if (outputIndex == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                    Log.i(TAG, "Decoder output format: " + codec.getOutputFormat());
                    continue;
                }
                break;
            }
        } catch (RuntimeException exception) {
            throw new IOException("MediaCodec rejected H.264 input", exception);
        }
    }

    public boolean isUpdateFrame() {
        FBOPlugin plugin = fboPlugin;
        return plugin != null && plugin.isUpdateFrame();
    }

    public void updateTexture() {
        FBOPlugin plugin = fboPlugin;
        if (plugin != null && plugin.isUpdateFrame()) {
            plugin.updateTexture();
        }
    }

    public void stopServer() {
        Thread thread;
        synchronized (socketLock) {
            running = false;
            closeQuietly(clientSocket);
            closeQuietly(serverSocket);
            clientSocket = null;
            serverSocket = null;
            thread = serverThread;
            serverThread = null;
        }
        if (thread != null && thread != Thread.currentThread()) {
            thread.interrupt();
            try {
                thread.join(1000L);
            } catch (InterruptedException exception) {
                Thread.currentThread().interrupt();
            }
            if (thread.isAlive()) {
                Log.w(TAG, "TCP receiver thread did not stop within one second");
            }
        }
    }

    public synchronized void release() {
        stopServer();
        MediaCodec codec = mediaCodec;
        mediaCodec = null;
        if (codec != null) {
            try {
                codec.stop();
            } catch (RuntimeException exception) {
                Log.w(TAG, "MediaCodec stop failed", exception);
            }
            try {
                codec.release();
            } catch (RuntimeException exception) {
                Log.w(TAG, "MediaCodec release failed", exception);
            }
        }
        FBOPlugin plugin = fboPlugin;
        fboPlugin = null;
        if (plugin != null) {
            try {
                plugin.release();
            } catch (RuntimeException exception) {
                Log.w(TAG, "FBO release failed", exception);
            }
        }
        lastTimestamp = 0L;
    }

    private static void closeQuietly(Socket socket) {
        if (socket == null) {
            return;
        }
        try {
            socket.close();
        } catch (IOException ignored) {
        }
    }

    private static void closeQuietly(ServerSocket socket) {
        if (socket == null) {
            return;
        }
        try {
            socket.close();
        } catch (IOException ignored) {
        }
    }
}
