package com.tpn.adbautoenable;

import org.junit.Test;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import static org.junit.Assert.*;

public class OperationDeadlineTest {
    @Test public void silentPeerCannotHoldReadForever() throws Exception {
        try (ServerSocket server = new ServerSocket(0);
             Socket client = new Socket("127.0.0.1", server.getLocalPort());
             Socket peer = server.accept()) {
            CountDownLatch finished = new CountDownLatch(1);
            Thread worker = new Thread(() -> {
                try (OperationDeadline timeout = new OperationDeadline(100, Thread.currentThread(), () -> {
                    try { client.close(); } catch (Exception ignored) { }
                })) { client.getInputStream().read(); }
                catch (Exception expected) { }
                finally { finished.countDown(); }
            });
            worker.setDaemon(true);
            worker.start();
            assertTrue("Silent socket must be closed", finished.await(3, TimeUnit.SECONDS));
        }
    }
    @Test public void completedOperationCannotAbortNextOperation() throws Exception {
        AtomicBoolean aborted = new AtomicBoolean();
        Thread owner = new Thread();
        try (OperationDeadline deadline = new OperationDeadline(100, owner, () -> aborted.set(true))) { }
        Thread.sleep(250);
        assertFalse(aborted.get());
        assertFalse(owner.isInterrupted());
    }
}
