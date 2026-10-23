package com.codex.sonyvolumegui;

import org.junit.Test;
import static org.junit.Assert.*;

public class GuardTimingTest {
    @Test public void successfulFirstCheckDoesNotEndWakeRecovery() {
        GuardTiming timing = new GuardTiming();
        timing.onWake(1000);
        assertEquals(300, timing.nextDelay(1100, true, 0, false));
        assertEquals(300, timing.nextDelay(30_999, true, 0, false));
        assertEquals(5000, timing.nextDelay(31_000, true, 0, false));
    }

    @Test public void wakeFailuresRetryQuicklyButNormalFailuresBackOff() {
        GuardTiming timing = new GuardTiming();
        timing.onWake(1000);
        assertEquals(300, timing.nextDelay(2000, false, 4, false));
        assertEquals(8000, timing.nextDelay(31_000, false, 4, false));
        assertEquals(1000, timing.nextDelay(31_000, false, 1, false));
    }

    @Test public void wakeDuringBlockedOperationIsNotDelayedByItsFailure() {
        GuardTiming timing = new GuardTiming();
        timing.onWake(1000);
        assertEquals(0, timing.nextDelay(2000, false, 4, true));
        timing.onWake(30_000);
        assertEquals(300, timing.nextDelay(31_000, true, 0, false));
        assertEquals(5000, timing.nextDelay(60_000, true, 0, false));
    }
}
