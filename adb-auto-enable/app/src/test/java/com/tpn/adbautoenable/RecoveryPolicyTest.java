package com.tpn.adbautoenable;

import org.junit.Test;
import static org.junit.Assert.*;

public class RecoveryPolicyTest {
    @Test public void retriesContinueAndRemainBounded() {
        assertEquals(5000, RecoveryPolicy.delayMs(1));
        assertEquals(10000, RecoveryPolicy.delayMs(2));
        assertEquals(60000, RecoveryPolicy.delayMs(1000));
        assertEquals(15000, RecoveryPolicy.delayMs(0));
    }
    @Test public void healthySleepDoesNotTriggerRestart() {
        assertFalse(RecoveryPolicy.isStalled(60000, 0));
        assertFalse(RecoveryPolicy.isStalled(119999, 0));
        assertTrue(RecoveryPolicy.isStalled(120000, 0));
        assertFalse(RecoveryPolicy.isStalled(130000, 120000));
    }
}
