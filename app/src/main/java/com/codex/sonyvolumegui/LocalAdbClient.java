package com.codex.sonyvolumegui;

import android.util.Base64;

import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.SocketTimeoutException;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.security.KeyFactory;
import java.security.PrivateKey;
import java.security.Signature;
import java.security.spec.PKCS8EncodedKeySpec;

public final class LocalAdbClient implements AutoCloseable {
    private static final ScheduledExecutorService DEADLINES = Executors.newSingleThreadScheduledExecutor();
    private static final int OPERATION_TIMEOUT_MS = 12000;
    private static final int MAX_PAYLOAD = 4096;
    private static final int MAX_OUTPUT = 256 * 1024;
    private volatile Socket activeSocket;
    private final AtomicBoolean closed = new AtomicBoolean();
    private static final String HOST = "127.0.0.1";
    private static final int PORT = 5555;
    private static final int TIMEOUT_MS = 8000;
    private static final int CONNECT_TIMEOUT_MS = 600;

    private static final int A_CNXN = command("CNXN");
    private static final int A_AUTH = command("AUTH");
    private static final int A_OPEN = command("OPEN");
    private static final int A_OKAY = command("OKAY");
    private static final int A_CLSE = command("CLSE");
    private static final int A_WRTE = command("WRTE");

    private static final int AUTH_TOKEN = 1;
    private static final int AUTH_SIGNATURE = 2;
    private static final int AUTH_RSAPUBLICKEY = 3;

    private final File privateKeyFile;
    private final File publicKeyFile;
    private final int[] ports;

    public LocalAdbClient(File privateKeyFile, File publicKeyFile) {
        this(privateKeyFile, publicKeyFile, candidatePorts());
    }

    LocalAdbClient(File privateKeyFile, File publicKeyFile, int[] ports) {
        this.privateKeyFile = privateKeyFile;
        this.publicKeyFile = publicKeyFile;
        this.ports = ports.clone();
    }

    public String shell(String command) throws Exception {
        return shell(command, OPERATION_TIMEOUT_MS);
    }

    String shell(String command, int timeoutMs) throws Exception {
        AtomicBoolean timedOut = new AtomicBoolean();
        ScheduledFuture<?> deadline = DEADLINES.schedule(() -> {
            timedOut.set(true);
            close();
        }, timeoutMs, TimeUnit.MILLISECONDS);
        try {
            return executeShell(command);
        } catch (Exception exception) {
            if (timedOut.get()) {
                throw new SocketTimeoutException("ADB operation deadline exceeded");
            }
            throw exception;
        } finally {
            deadline.cancel(false);
            close();
        }
    }

    @Override
    public synchronized void close() {
        closed.set(true);
        if (activeSocket != null) {
            try { activeSocket.close(); } catch (IOException ignored) { }
            activeSocket = null;
        }
    }

    private synchronized void attach(Socket socket) throws IOException {
        if (closed.get()) {
            throw new IOException("ADB operation cancelled");
        }
        activeSocket = socket;
    }

    private String executeShell(String command) throws Exception {
        if (!privateKeyFile.isFile() || !publicKeyFile.isFile()) {
            throw new IOException("adbkey or adbkey.pub is missing in app files directory");
        }

        Exception lastException = null;
        for (int port : ports) {
            try (Socket socket = new Socket()) {
                attach(socket);
                socket.connect(new InetSocketAddress(HOST, port), CONNECT_TIMEOUT_MS);
                socket.setSoTimeout(TIMEOUT_MS);

                InputStream input = socket.getInputStream();
                OutputStream output = socket.getOutputStream();
                connect(input, output);
                return openShell(input, output, command);
            } catch (Exception exception) {
                if (closed.get() || Thread.currentThread().isInterrupted()) {
                    throw exception;
                }
                lastException = exception;
            }
        }

        throw lastException == null ? new IOException("no local adb port available") : lastException;
    }

    private static int[] candidatePorts() {
        return new int[] {
            PORT,
            45647,
            33821,
            65000
        };
    }

    private void connect(InputStream input, OutputStream output) throws Exception {
        writePacket(output, A_CNXN, 0x01000000, 4096, "host::\0".getBytes(StandardCharsets.UTF_8));

        while (true) {
            Packet packet = readPacket(input);
            if (packet.command == A_CNXN) {
                return;
            }

            if (packet.command != A_AUTH || packet.arg0 != AUTH_TOKEN) {
                throw new IOException("unexpected adb packet during auth: " + packet.command);
            }

            byte[] signature = signToken(packet.payload);
            writePacket(output, A_AUTH, AUTH_SIGNATURE, 0, signature);

            Packet authResult = readPacket(input);
            if (authResult.command == A_CNXN) {
                return;
            }

            if (authResult.command == A_AUTH && authResult.arg0 == AUTH_TOKEN) {
                byte[] publicKey = readPublicKeyPayload();
                writePacket(output, A_AUTH, AUTH_RSAPUBLICKEY, 0, publicKey);
                continue;
            }

            throw new IOException("adb authentication failed");
        }
    }

    private String openShell(InputStream input, OutputStream output, String command) throws IOException {
        int localId = 1;
        int remoteId = 0;
        byte[] destination = ("shell:" + command + "\0").getBytes(StandardCharsets.UTF_8);
        writePacket(output, A_OPEN, localId, 0, destination);

        ByteArrayOutputStream shellOutput = new ByteArrayOutputStream();
        boolean open = true;
        while (open) {
            Packet packet = readPacket(input);
            if (packet.command == A_OKAY && packet.arg1 == localId) {
                remoteId = packet.arg0;
            } else if (packet.command == A_WRTE && packet.arg1 == localId) {
                remoteId = packet.arg0;
                if (shellOutput.size() + packet.payload.length > MAX_OUTPUT) {
                    throw new IOException("ADB shell output limit exceeded");
                }
                shellOutput.write(packet.payload, 0, packet.payload.length);
                writePacket(output, A_OKAY, localId, remoteId, new byte[0]);
            } else if (packet.command == A_CLSE && packet.arg1 == localId) {
                if (remoteId == 0) {
                    remoteId = packet.arg0;
                }
                writePacket(output, A_CLSE, localId, remoteId, new byte[0]);
                open = false;
            }
        }

        return shellOutput.toString(StandardCharsets.UTF_8.name());
    }

    private byte[] signToken(byte[] token) throws Exception {
        PrivateKey privateKey = loadPrivateKey();
        Signature signature = Signature.getInstance("NONEwithRSA");
        signature.initSign(privateKey);

        byte[] digestInfo = new byte[] {
            0x30, 0x21, 0x30, 0x09, 0x06, 0x05, 0x2b, 0x0e,
            0x03, 0x02, 0x1a, 0x05, 0x00, 0x04, 0x14
        };

        signature.update(digestInfo);
        signature.update(token);
        return signature.sign();
    }

    private PrivateKey loadPrivateKey() throws Exception {
        String pem = new String(Files.readAllBytes(privateKeyFile.toPath()), StandardCharsets.UTF_8)
            .replace("-----BEGIN PRIVATE KEY-----", "")
            .replace("-----END PRIVATE KEY-----", "")
            .replaceAll("\\s", "");
        byte[] der = Base64.decode(pem, Base64.DEFAULT);
        return KeyFactory.getInstance("RSA").generatePrivate(new PKCS8EncodedKeySpec(der));
    }

    private byte[] readPublicKeyPayload() throws IOException {
        String publicKey = new String(Files.readAllBytes(publicKeyFile.toPath()), StandardCharsets.UTF_8).trim();
        return (publicKey + "\0").getBytes(StandardCharsets.UTF_8);
    }

    private static Packet readPacket(InputStream input) throws IOException {
        byte[] header = readFully(input, 24);
        ByteBuffer buffer = ByteBuffer.wrap(header).order(ByteOrder.LITTLE_ENDIAN);
        int command = buffer.getInt();
        int arg0 = buffer.getInt();
        int arg1 = buffer.getInt();
        int length = buffer.getInt();
        int checksum = buffer.getInt();
        int magic = buffer.getInt();
        if (length < 0 || length > MAX_PAYLOAD) {
            throw new IOException("invalid adb packet length: " + length);
        }
        if ((command ^ 0xffffffff) != magic) {
            throw new IOException("invalid adb packet magic");
        }

        byte[] payload = length == 0 ? new byte[0] : readFully(input, length);
        if (checksum(payload) != checksum) {
            throw new IOException("invalid adb packet checksum");
        }

        return new Packet(command, arg0, arg1, payload);
    }

    private static void writePacket(OutputStream output, int command, int arg0, int arg1, byte[] payload)
        throws IOException {
        ByteBuffer buffer = ByteBuffer.allocate(24).order(ByteOrder.LITTLE_ENDIAN);
        buffer.putInt(command);
        buffer.putInt(arg0);
        buffer.putInt(arg1);
        buffer.putInt(payload.length);
        buffer.putInt(checksum(payload));
        buffer.putInt(command ^ 0xffffffff);
        output.write(buffer.array());
        output.write(payload);
        output.flush();
    }

    private static byte[] readFully(InputStream input, int length) throws IOException {
        byte[] buffer = new byte[length];
        int offset = 0;
        while (offset < length) {
            int read = input.read(buffer, offset, length - offset);
            if (read < 0) {
                throw new EOFException("adb connection closed");
            }
            offset += read;
        }
        return buffer;
    }

    private static int checksum(byte[] payload) {
        int sum = 0;
        for (byte value : payload) {
            sum += value & 0xff;
        }
        return sum;
    }

    private static int command(String value) {
        byte[] bytes = value.getBytes(StandardCharsets.US_ASCII);
        return (bytes[0] & 0xff) |
            ((bytes[1] & 0xff) << 8) |
            ((bytes[2] & 0xff) << 16) |
            ((bytes[3] & 0xff) << 24);
    }

    private static final class Packet {
        final int command;
        final int arg0;
        final int arg1;
        final byte[] payload;

        Packet(int command, int arg0, int arg1, byte[] payload) {
            this.command = command;
            this.arg0 = arg0;
            this.arg1 = arg1;
            this.payload = payload;
        }
    }
}
