package com.tpn.adbautoenable;

/** Uses elapsed time so wall-clock changes cannot stall recovery. */
final class RecoveryPolicy {
    static final long STALL_MS = 120_000;
    static long delayMs(int failures) {
        return failures == 0 ? 15_000 : Math.min(60_000, 5_000L << Math.min(4, failures - 1));
    }
    static boolean isStalled(long now, long progress) {
        return now - progress >= STALL_MS;
    }
}
