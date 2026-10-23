package com.codex.sonyvolumegui;

/** A bounded recovery window covers controllers registered late during wake-up. */
final class GuardTiming {
    private long recoveryUntil;

    void onWake(long now) { recoveryUntil = now + 30_000; }

    boolean recovering(long now) { return now < recoveryUntil; }

    long nextDelay(long now, boolean success, int failures, boolean pending) {
        if (pending) return 0;
        if (recovering(now)) return 300;
        return success ? 5000 : Math.min(1000L << (Math.max(1, Math.min(failures, 4)) - 1), 8000);
    }
}
