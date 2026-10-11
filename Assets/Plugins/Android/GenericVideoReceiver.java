package com.xrobotoolkit.video;

import android.media.MediaCodec;
import android.media.MediaCodecInfo;
import android.media.MediaCodecList;
import android.media.MediaFormat;
import android.os.Process;
import android.view.Surface;
import com.picoxr.fbolibrary.FBOPlugin;
import org.json.JSONObject;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.ByteBuffer;

/** Video v1 has its own connection and workers. Never calls the pose SDK. */
public final class GenericVideoReceiver {
    private static final int MAX_PACKET = 16 * 1024 * 1024;
    private final Object surfaceLock = new Object();
    private volatile boolean running;
    private volatile boolean stopped = true;
    private volatile Socket socket;
    private volatile int formatGeneration;
    private volatile String pendingFormat = "";
    private volatile String state = "stopped";
    private volatile String error = "";
    private volatile long receivedFrames, decodedFrames, displayedFrames, lastDisplayNs;
    private volatile float unityHz;
    private volatile double mainThreadMaxMs;
    private Surface surface;
    private volatile int surfaceGeneration;
    private FBOPlugin plugin; // Only touched on Unity's graphics-owning thread.
    private final java.util.concurrent.atomic.AtomicBoolean frameReady = new java.util.concurrent.atomic.AtomicBoolean();
    private Thread worker;

    public void start(String host, int port) {
        if (!stopped) throw new IllegalStateException("previous video receiver is still stopping");
        running = true;
        stopped = false;
        worker = new Thread(() -> run(host, port), "generic-video-network");
        worker.start();
    }

    public void requestStop() {
        running = false;
        Socket current = socket;
        if (current != null) try { current.close(); } catch (IOException ignored) {}
        synchronized (surfaceLock) { surfaceLock.notifyAll(); }
        // Socket close + condition notification wake I/O without interrupting codec cleanup.
    }

    public boolean isStopped() { return stopped; }
    public int getFormatGeneration() { return formatGeneration; }
    public String getFormat() { return pendingFormat; }
    public String getState() { return state; }
    public String getError() { return error; }
    public long getDisplayedFrames() { return displayedFrames; }
    public long getDisplayAgeMs() {
        return lastDisplayNs == 0 ? -1 : (System.nanoTime() - lastDisplayNs) / 1000000;
    }

    /** Called only by Unity, after the network worker has released the old codec. */
    public void prepareTexture(int textureId, int width, int height, int generation) {
        synchronized (surfaceLock) {
            if (!running || generation != formatGeneration) return;
            releaseTexture();
            plugin = new FBOPlugin();
            plugin.init();
            plugin.BuildTexture(textureId, width, height);
            surface = plugin.getSurface();
            surfaceGeneration = generation;
            frameReady.set(false);
            // The legacy plugin drains a callback counter in an unbounded GL loop.
            // Coalesce callbacks ourselves so Unity performs at most one blit per poll.
            plugin.getSurfaceTexture().setOnFrameAvailableListener(texture -> {
                if (running && generation == surfaceGeneration) frameReady.set(true);
            });
            surfaceLock.notifyAll();
        }
    }

    public boolean updateTexture() {
        if (plugin != null && frameReady.getAndSet(false)) {
            plugin.onFrameAvailable(plugin.getSurfaceTexture());
            plugin.updateTexture();
            displayedFrames++;
            lastDisplayNs = System.nanoTime();
            return true;
        }
        return false;
    }

    /** Caller ensures stopped, or a format request with no codec using this surface. */
    public void releaseTexture() {
        if (plugin != null) { plugin.release(); plugin = null; }
        frameReady.set(false);
        surface = null;
    }

    public void reportUnity(float hz, double maxMs) { unityHz = hz; mainThreadMaxMs = maxMs; }

    private void run(String host, int port) {
        Process.setThreadPriority(Process.THREAD_PRIORITY_BACKGROUND);
        try {
            while (running) {
                try {
                    state = "connecting";
                    Socket connection = new Socket();
                    socket = connection;
                    connection.connect(new InetSocketAddress(host, port), 1500);
                    connection.setTcpNoDelay(true);
                    connection.setSoTimeout(6000); // Initial source discovery / encoder startup.
                    stream(connection);
                } catch (Exception exception) {
                    if (running) { error = exception.toString(); state = "reconnecting"; }
                } finally {
                    Socket old = socket; socket = null;
                    if (old != null) try { old.close(); } catch (IOException ignored) {}
                }
                if (running) try { Thread.sleep(300); } catch (InterruptedException ignored) {}
            }
        } finally { state = "stopped"; stopped = true; }
    }

    private static MediaCodec createHardwareDecoder(int width, int height, int fps) throws Exception {
        for (MediaCodecInfo info : new MediaCodecList(MediaCodecList.REGULAR_CODECS).getCodecInfos()) {
            if (info.isEncoder() || info.isSoftwareOnly()) continue;
            for (String type : info.getSupportedTypes()) {
                if (!type.equalsIgnoreCase("video/avc")) continue;
                MediaCodecInfo.VideoCapabilities caps = info.getCapabilitiesForType(type).getVideoCapabilities();
                if (caps.areSizeAndRateSupported(width, height, fps)) return MediaCodec.createByCodecName(info.getName());
            }
        }
        throw new IOException("No hardware H.264 decoder for " + width + "x" + height + "@" + fps);
    }

    private void stream(Socket connection) throws Exception {
        DataInputStream input = new DataInputStream(connection.getInputStream());
        DataOutputStream output = new DataOutputStream(connection.getOutputStream());
        sendStatus(output, new JSONObject().put("protocol", 1));
        JSONObject config = new JSONObject(new String(readPacket(input, 1), "UTF-8"));
        if (config.getInt("protocol") != 1 || !config.getString("codec").equals("h264") ||
            !config.getString("layout").equals("mono")) throw new IOException("Unsupported video format");
        String session = config.getString("session");
        int width = config.getInt("width"), height = config.getInt("height"), fps = config.getInt("fps");
        if (width < 16 || height < 16 || width > 4096 || height > 4096 ||
            (width & 1) != 0 || (height & 1) != 0 || fps < 1 || fps > 60) throw new IOException("Invalid dimensions/rate");
        MediaCodec codec;
        try { codec = createHardwareDecoder(width, height, fps); }
        catch (Exception e) {
            sendStatus(output, new JSONObject().put("session", session).put("ready", false).put("error", e.toString()));
            throw e;
        }
        java.util.concurrent.atomic.AtomicBoolean draining = new java.util.concurrent.atomic.AtomicBoolean(true);
        java.util.concurrent.atomic.AtomicReference<String> decodeError = new java.util.concurrent.atomic.AtomicReference<>();
        Thread drain = null;
        try {
            state = "configuring";
            receivedFrames = decodedFrames = displayedFrames = lastDisplayNs = 0;
            synchronized (surfaceLock) {
                pendingFormat = config.toString();
                int generation = ++formatGeneration;
                long deadline = System.nanoTime() + 2500000000L;
                while (running && surfaceGeneration != generation && System.nanoTime() < deadline) surfaceLock.wait(50);
                if (!running || surfaceGeneration != generation || surface == null) throw new IOException("Unity surface not ready");
                MediaFormat format = MediaFormat.createVideoFormat("video/avc", width, height);
                format.setInteger(MediaFormat.KEY_PRIORITY, 1);
                format.setInteger("low-latency", 1);
                codec.configure(format, surface, null, 0);
            }
            codec.start();
            drain = new Thread(() -> {
                Process.setThreadPriority(Process.THREAD_PRIORITY_BACKGROUND);
                MediaCodec.BufferInfo info = new MediaCodec.BufferInfo();
                try {
                    while (draining.get()) {
                        int index = codec.dequeueOutputBuffer(info, 10000);
                        if (index >= 0) { codec.releaseOutputBuffer(index, true); decodedFrames++; }
                    }
                } catch (RuntimeException e) { decodeError.set(e.toString()); }
            }, "generic-video-decode");
            drain.start();
            sendStatus(output, new JSONObject().put("session", session).put("ready", true));
            state = "playing"; error = "";
            long nextStatus = 0;
            while (running) {
                byte[] unit = readPacket(input, 2);
                if (receivedFrames == 0) connection.setSoTimeout(1500);
                if (decodeError.get() != null) throw new IOException(decodeError.get());
                long deadline = System.nanoTime() + 120000000L;
                int index = -1;
                while (running && index < 0 && System.nanoTime() < deadline) index = codec.dequeueInputBuffer(10000);
                // Dropping a reference frame would corrupt subsequent frames. Reconnect for a new IDR.
                if (index < 0) throw new IOException("Decoder backpressure; requesting new session");
                ByteBuffer target = codec.getInputBuffer(index);
                if (target == null || target.capacity() < unit.length) throw new IOException("H.264 AU too large");
                target.clear(); target.put(unit);
                codec.queueInputBuffer(index, 0, unit.length, System.nanoTime() / 1000, 0);
                receivedFrames++;
                long now = System.nanoTime();
                if (now >= nextStatus) {
                    sendStatus(output, new JSONObject().put("session", session).put("received", receivedFrames)
                        .put("decoded", decodedFrames).put("displayed", displayedFrames)
                        .put("display_age_ms", getDisplayAgeMs()).put("unity_hz", unityHz)
                        .put("unity_max_frame_ms", mainThreadMaxMs));
                    nextStatus = now + 1000000000L;
                }
            }
        } finally {
            draining.set(false);
            try {
                if (drain != null) drain.join(500);
            } catch (InterruptedException ignored) {
                // Stop interrupts network waits; codec ownership still must be released.
            } finally {
                try { codec.stop(); } catch (Exception ignored) {}
                codec.release();
            }
        }
    }

    private static byte[] readPacket(DataInputStream input, int expected) throws IOException {
        int type = input.readUnsignedByte(), length = input.readInt();
        if (type != expected || length <= 0 || length > MAX_PACKET || (type != 2 && length > 16384))
            throw new IOException("Invalid video packet");
        byte[] payload = new byte[length];
        input.readFully(payload);
        return payload;
    }

    private static void sendStatus(DataOutputStream output, JSONObject value) throws IOException {
        byte[] payload = value.toString().getBytes("UTF-8");
        output.writeByte(3); output.writeInt(payload.length); output.write(payload); output.flush();
    }
}
