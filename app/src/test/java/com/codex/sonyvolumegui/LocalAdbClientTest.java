package com.codex.sonyvolumegui;

import org.junit.Test;
import org.junit.Rule;
import org.junit.rules.TemporaryFolder;
import java.io.*;
import java.net.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.util.concurrent.*;
import static org.junit.Assert.*;

public class LocalAdbClientTest {
    @Rule public TemporaryFolder temp = new TemporaryFolder();
    interface Peer { void run(Socket socket) throws Exception; }

    private String exchange(Peer peer, int timeout) throws Exception {
        try (ServerSocket server = new ServerSocket(0)) {
            ExecutorService executor = Executors.newSingleThreadExecutor();
            Future<?> remote = executor.submit(() -> {
                try (Socket socket = server.accept()) { peer.run(socket); }
                catch (Exception ignored) { /* Client is expected to close on faults. */ }
            });
            try (LocalAdbClient client = new LocalAdbClient(temp.newFile(), temp.newFile(),
                    new int[] {server.getLocalPort()})) {
                return client.shell("test", timeout);
            } finally {
                remote.cancel(true);
                executor.shutdownNow();
            }
        }
    }

    private static void packet(Socket socket, String command, int arg0, int arg1, byte[] data) throws IOException {
        int cmd = ByteBuffer.wrap(command.getBytes(StandardCharsets.US_ASCII)).order(ByteOrder.LITTLE_ENDIAN).getInt();
        int sum = 0;
        for (byte b : data) sum += b & 255;
        socket.getOutputStream().write(ByteBuffer.allocate(24).order(ByteOrder.LITTLE_ENDIAN)
            .putInt(cmd).putInt(arg0).putInt(arg1).putInt(data.length).putInt(sum).putInt(~cmd).array());
        socket.getOutputStream().write(data);
        socket.getOutputStream().flush();
    }

    @Test public void validShellAndNextConnectionWork() throws Exception {
        for (int i = 0; i < 2; i++) {
            assertEquals("mVolumeController=VolumeController(null)", exchange(socket -> {
                packet(socket, "CNXN", 0x01000000, 4096, new byte[0]);
                packet(socket, "OKAY", 1, 1, new byte[0]);
                packet(socket, "WRTE", 1, 1, "mVolumeController=VolumeController(null)".getBytes(StandardCharsets.UTF_8));
                packet(socket, "CLSE", 1, 1, new byte[0]);
                Thread.sleep(200);
            }, 2000));
        }
    }

    @Test public void deadlineStopsPeerThatNeverFinishesDespiteIncomingData() throws Exception {
        long start = System.nanoTime();
        assertThrows(SocketTimeoutException.class, () -> exchange(socket -> {
            packet(socket, "CNXN", 0x01000000, 4096, new byte[0]);
            while (!Thread.currentThread().isInterrupted()) {
                packet(socket, "OKAY", 1, 1, new byte[0]);
                Thread.sleep(20);
            }
        }, 300));
        assertTrue(TimeUnit.NANOSECONDS.toMillis(System.nanoTime() - start) < 2000);
        validShellAndNextConnectionWork();
    }

    @Test public void rejectsNegativeAndOversizedPayloadBeforeAllocating() throws Exception {
        for (int size : new int[] {-1, Integer.MAX_VALUE, 4097}) {
            IOException exception = assertThrows(IOException.class, () -> exchange(socket -> {
                int cmd = 0x4e584e43;
                socket.getOutputStream().write(ByteBuffer.allocate(24).order(ByteOrder.LITTLE_ENDIAN)
                    .putInt(cmd).putInt(0).putInt(0).putInt(size).putInt(0).putInt(~cmd).array());
                Thread.sleep(200);
            }, 2000));
            assertTrue(exception.getMessage().contains("length"));
        }
    }

    @Test public void outputIsBounded() throws Exception {
        IOException exception = assertThrows(IOException.class, () -> exchange(socket -> {
            packet(socket, "CNXN", 0x01000000, 4096, new byte[0]);
            for (int i = 0; i < 70; i++) packet(socket, "WRTE", 1, 1, new byte[4096]);
            Thread.sleep(200);
        }, 2000));
        assertTrue(exception.getMessage().contains("output limit"));
    }

    @Test public void cancellationClosesBlockedRead() throws Exception {
        try (ServerSocket server = new ServerSocket(0);
             LocalAdbClient client = new LocalAdbClient(temp.newFile(), temp.newFile(), new int[] {server.getLocalPort()})) {
            ExecutorService executor = Executors.newSingleThreadExecutor();
            Future<?> future = executor.submit(() -> {
                assertThrows(IOException.class, () -> client.shell("test", 10000));
            });
            try (Socket ignored = server.accept()) {
                client.close();
                future.get(2, TimeUnit.SECONDS);
            } finally { executor.shutdownNow(); }
        }
    }

    @Test public void controllerVerificationDoesNotAcceptUnrelatedNull() {
        assertTrue(VolumeGuardAccessibilityService.controllerHidden("mVolumeController=VolumeController(null)"));
        assertTrue(VolumeGuardAccessibilityService.controllerHidden("mVolumeController=null"));
        assertFalse(VolumeGuardAccessibilityService.controllerHidden("mVolumeController=active\nother=VolumeController(null)"));
        assertFalse(VolumeGuardAccessibilityService.controllerHidden("Permission denied"));
    }
}
