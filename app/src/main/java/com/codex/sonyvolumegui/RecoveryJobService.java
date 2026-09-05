package com.codex.sonyvolumegui;

import android.app.job.JobInfo;
import android.app.job.JobParameters;
import android.app.job.JobScheduler;
import android.app.job.JobService;
import android.content.ComponentName;
import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.util.Log;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** System-owned fallback: never holds a boot broadcast open or rewrites accessibility settings. */
public final class RecoveryJobService extends JobService {
    private static final int PERIODIC_ID = 1701;
    private static final int RECOVERY_ID = 1702;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ExecutorService executor = Executors.newSingleThreadExecutor();
    private final Map<Integer, LocalAdbClient> clients = new HashMap<>();

    static boolean isEnabled(Context context) {
        if (Settings.Secure.getInt(context.getContentResolver(), Settings.Secure.ACCESSIBILITY_ENABLED, 0) != 1) {
            return false;
        }
        String services = Settings.Secure.getString(context.getContentResolver(),
            Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES);
        ComponentName own = new ComponentName(context, VolumeGuardAccessibilityService.class);
        if (services != null) for (String service : services.split(":")) {
            if (own.equals(ComponentName.unflattenFromString(service))) return true;
        }
        return false;
    }

    static void schedule(Context context, boolean periodic) {
        JobScheduler scheduler = context.getSystemService(JobScheduler.class);
        if (scheduler == null) return;
        int id = periodic ? PERIODIC_ID : RECOVERY_ID;
        if (periodic && scheduler.getPendingJob(id) != null) return;
        JobInfo.Builder builder = new JobInfo.Builder(id, new ComponentName(context, RecoveryJobService.class))
            .setPersisted(true).setBackoffCriteria(30_000, JobInfo.BACKOFF_POLICY_EXPONENTIAL);
        if (periodic) builder.setPeriodic(15 * 60_000L);
        else builder.setMinimumLatency(1000).setOverrideDeadline(5000);
        if (scheduler.schedule(builder.build()) != JobScheduler.RESULT_SUCCESS) {
            Log.w(VolumeGuardAccessibilityService.TAG, "recovery job scheduling failed");
        }
    }

    @Override public boolean onStartJob(JobParameters params) {
        if (!isEnabled(this)) {
            getSystemService(JobScheduler.class).cancel(PERIODIC_ID);
            return false;
        }
        if (VolumeGuardAccessibilityService.isConnected()) {
            VolumeGuardAccessibilityService.requestCheck();
            return false;
        }
        LocalAdbClient client = VolumeGuardAccessibilityService.newClient(this);
        clients.put(params.getJobId(), client);
        executor.execute(() -> {
            boolean success = false;
            String message;
            try {
                success = VolumeGuardAccessibilityService.controllerHidden(
                    client.shell(VolumeGuardAccessibilityService.GUARD_COMMAND));
                message = success ? "后台检查已隐藏 GUI；无障碍未连接，请在设置中关闭再开启服务" : "后台检查失败，将重试";
            } catch (Exception exception) {
                message = "后台恢复失败，请检查 ADB 授权：" + exception.getMessage();
                Log.w(VolumeGuardAccessibilityService.TAG, "recovery failed", exception);
            } finally { client.close(); }
            final boolean ok = success;
            final String result = message;
            handler.post(() -> {
                if (clients.get(params.getJobId()) != client) return;
                clients.remove(params.getJobId());
                VolumeGuardAccessibilityService.status(this, result, ok);
                jobFinished(params, !ok);
            });
        });
        return true;
    }

    @Override public boolean onStopJob(JobParameters params) {
        LocalAdbClient client = clients.remove(params.getJobId());
        if (client != null) client.close();
        return isEnabled(this);
    }

    @Override public void onDestroy() {
        for (LocalAdbClient client : clients.values()) client.close();
        clients.clear();
        executor.shutdownNow();
        handler.removeCallbacksAndMessages(null);
        super.onDestroy();
    }
}
