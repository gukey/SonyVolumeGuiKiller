package com.codex.sonyvolumegui;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.AccessibilityServiceInfo;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;
import android.view.KeyEvent;
import android.view.accessibility.AccessibilityEvent;
import java.io.File;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.RejectedExecutionException;

public final class VolumeGuardAccessibilityService extends AccessibilityService {
    public static final String TAG = "SonyVolumeGuiKiller";
    static final String GUARD_COMMAND =
        "service call audio 97 null >/dev/null; dumpsys audio | grep mVolumeController; exit";
    private static volatile VolumeGuardAccessibilityService connectedService;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private LocalAdbClient activeClient;
    private boolean running;
    private boolean inFlight;
    private boolean pending;
    private boolean receiverRegistered;
    private int failures;
    private final Runnable guard = this::runGuard;
    private final BroadcastReceiver wakeReceiver = new BroadcastReceiver() {
        @Override public void onReceive(Context context, Intent intent) { requestGuard(0); }
    };

    static boolean isConnected() { return connectedService != null; }

    static void requestCheck() {
        VolumeGuardAccessibilityService service = connectedService;
        if (service != null) service.handler.post(() -> service.requestGuard(0));
    }

    static boolean controllerHidden(String output) {
        for (String line : output.split("\\r?\\n")) {
            if (line.contains("mVolumeController") &&
                (line.contains("VolumeController(null") || line.contains("mVolumeController=null"))) {
                return true;
            }
        }
        return false;
    }

    static LocalAdbClient newClient(Context context) {
        return new LocalAdbClient(new File(context.getFilesDir(), "adbkey"),
            new File(context.getFilesDir(), "adbkey.pub"));
    }

    static void status(Context context, String message, boolean success) {
        android.content.SharedPreferences.Editor edit = context.getSharedPreferences("guard", MODE_PRIVATE)
            .edit().putString("status", message).putLong("attempt", System.currentTimeMillis());
        if (success) edit.putLong("success", System.currentTimeMillis());
        edit.apply();
    }

    @Override protected void onServiceConnected() {
        AccessibilityServiceInfo info = getServiceInfo();
        if (info == null) info = new AccessibilityServiceInfo();
        info.eventTypes = AccessibilityEvent.TYPE_WINDOW_STATE_CHANGED | AccessibilityEvent.TYPE_WINDOWS_CHANGED;
        info.feedbackType = AccessibilityServiceInfo.FEEDBACK_GENERIC;
        info.flags |= AccessibilityServiceInfo.FLAG_REQUEST_FILTER_KEY_EVENTS |
            AccessibilityServiceInfo.FLAG_RETRIEVE_INTERACTIVE_WINDOWS;
        info.notificationTimeout = 0;
        setServiceInfo(info);
        running = true;
        connectedService = this;
        if (!receiverRegistered) {
            IntentFilter filter = new IntentFilter(Intent.ACTION_SCREEN_ON);
            filter.addAction(Intent.ACTION_USER_PRESENT);
            if (Build.VERSION.SDK_INT >= 33) registerReceiver(wakeReceiver, filter, Context.RECEIVER_NOT_EXPORTED);
            else registerReceiver(wakeReceiver, filter);
            receiverRegistered = true;
        }
        RecoveryJobService.schedule(this, true);
        requestGuard(0);
        Log.i(TAG, "accessibility service connected");
    }

    @Override public void onAccessibilityEvent(AccessibilityEvent event) {
        if (event != null && "com.android.systemui".contentEquals(
                event.getPackageName() == null ? "" : event.getPackageName())) requestGuard(300);
    }

    @Override protected boolean onKeyEvent(KeyEvent event) {
        int code = event.getKeyCode();
        if (event.getAction() == KeyEvent.ACTION_DOWN && (code == KeyEvent.KEYCODE_VOLUME_UP ||
            code == KeyEvent.KEYCODE_VOLUME_DOWN || code == KeyEvent.KEYCODE_VOLUME_MUTE)) requestGuard(300);
        return false;
    }

    @Override public void onInterrupt() {
        // This callback interrupts feedback, not the service binding.
        requestGuard(0);
    }

    private void requestGuard(long delay) {
        if (!running) return;
        if (inFlight) { pending = true; return; }
        // A fixed runnable bounds event storms to one queued request.
        if (delay > 0) {
            // Move the normal 5 second check forward; subsequent events must not postpone it.
            if (fastQueued) return;
        }
        handler.removeCallbacks(guard);
        fastQueued = true;
        handler.postDelayed(guard, delay);
    }

    private boolean fastQueued;

    private void runGuard() {
        fastQueued = false;
        if (!running || inFlight) return;
        inFlight = true;
        LocalAdbClient client = newClient(this);
        activeClient = client;
        try {
            executor.execute(() -> {
                boolean success = false;
                String message;
                try {
                    success = controllerHidden(client.shell(GUARD_COMMAND));
                    message = success ? "音量 GUI 已拦截" : "控制器未清除：将重试，请检查电视系统兼容性";
                } catch (Exception exception) {
                    message = "ADB 连接或授权失败，将自动重试：" + exception.getMessage();
                    Log.w(TAG, "guard failed", exception);
                } finally { client.close(); }
                final boolean ok = success;
                final String result = message;
                handler.post(() -> {
                    activeClient = null;
                    inFlight = false;
                    if (!running) return;
                    status(this, result, ok);
                    failures = ok ? 0 : Math.min(failures + 1, 4);
                    long delay = ok ? 5000 : Math.min(1000L << (failures - 1), 8000);
                    if (pending && ok) delay = 300;
                    pending = false;
                    handler.removeCallbacks(guard);
                    handler.postDelayed(guard, delay);
                });
            });
        } catch (RejectedExecutionException exception) {
            client.close();
            activeClient = null;
            inFlight = false;
            Log.w(TAG, "guard executor stopped", exception);
        }
    }

    private void stopGuard() {
        running = false;
        if (connectedService == this) connectedService = null;
        pending = false;
        fastQueued = false;
        handler.removeCallbacksAndMessages(null);
        if (activeClient != null) activeClient.close();
        if (receiverRegistered) {
            unregisterReceiver(wakeReceiver);
            receiverRegistered = false;
        }
    }

    @Override public boolean onUnbind(Intent intent) {
        stopGuard();
        return super.onUnbind(intent);
    }

    @Override public void onDestroy() {
        stopGuard();
        executor.shutdownNow();
        super.onDestroy();
    }
}
