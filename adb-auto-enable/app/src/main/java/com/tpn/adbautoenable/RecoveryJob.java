package com.tpn.adbautoenable;

import android.app.job.JobInfo;
import android.app.job.JobParameters;
import android.app.job.JobScheduler;
import android.app.job.JobService;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

/** Best-effort fallback when Android has reclaimed the foreground service. */
public class RecoveryJob extends JobService {
    static void schedule(Context context, boolean urgent) {
        try {
            JobScheduler scheduler = context.getSystemService(JobScheduler.class);
            JobInfo.Builder job = new JobInfo.Builder(urgent ? 5556 : 5555,
                    new ComponentName(context, RecoveryJob.class));
            if (urgent) job.setMinimumLatency(1000).setOverrideDeadline(10_000);
            else job.setPeriodic(15 * 60_000L).setPersisted(true);
            scheduler.schedule(job.build());
        } catch (RuntimeException e) { Log.e("ADBAutoEnable", "Cannot schedule recovery", e); }
    }
    @Override public boolean onStartJob(JobParameters params) {
        try { startForegroundService(new Intent(this, AdbConfigService.class)); }
        catch (RuntimeException e) { Log.e("ADBAutoEnable", "Recovery deferred by system", e); }
        return false;
    }
    @Override public boolean onStopJob(JobParameters params) { return true; }
}
