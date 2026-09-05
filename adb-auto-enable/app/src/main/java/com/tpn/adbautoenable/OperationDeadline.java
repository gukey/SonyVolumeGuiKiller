package com.tpn.adbautoenable;

import java.util.concurrent.ScheduledFuture;
import java.util.concurrent.ScheduledThreadPoolExecutor;
import java.util.concurrent.TimeUnit;

/** Cancellation is serialized with expiry so a completed operation cannot interrupt its successor. */
final class OperationDeadline implements AutoCloseable {
    private static final ScheduledThreadPoolExecutor TIMER = new ScheduledThreadPoolExecutor(2, task -> {
        Thread thread = new Thread(task, "adb-operation-deadline");
        thread.setDaemon(true);
        return thread;
    });
    static { TIMER.setRemoveOnCancelPolicy(true); }
    private final ScheduledFuture<?> future;
    private boolean closed;

    OperationDeadline(long timeoutMs, Thread owner, Runnable abort) {
        future = TIMER.schedule(() -> {
            synchronized (this) {
                if (closed) return;
                closed = true;
                owner.interrupt();
            }
            abort.run();
        }, timeoutMs, TimeUnit.MILLISECONDS);
    }
    @Override public synchronized void close() {
        closed = true;
        future.cancel(false);
    }
}
