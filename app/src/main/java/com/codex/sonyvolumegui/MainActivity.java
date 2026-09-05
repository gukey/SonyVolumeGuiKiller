package com.codex.sonyvolumegui;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.content.SharedPreferences;
import java.text.DateFormat;
import java.util.Date;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.TextView;

public final class MainActivity extends Activity {
    private final Handler handler = new Handler(Looper.getMainLooper());
    private TextView statusView;
    private final Runnable refresh = new Runnable() {
        @Override public void run() {
            SharedPreferences prefs = getSharedPreferences("guard", MODE_PRIVATE);
            long success = prefs.getLong("success", 0);
            String state = !RecoveryJobService.isEnabled(MainActivity.this) ? "无障碍服务未启用" :
                (VolumeGuardAccessibilityService.isConnected() ? "无障碍服务已连接" : "无障碍服务未连接，请关闭再开启");
            statusView.setText(state + "\n" + prefs.getString("status", "等待首次检查") +
                "\n最近成功：" + (success == 0 ? "尚无" : DateFormat.getDateTimeInstance().format(new Date(success))));
            handler.postDelayed(this, 2000);
        }
    };

    @Override protected void onResume() {
        super.onResume();
        handler.post(refresh);
    }

    @Override protected void onPause() {
        handler.removeCallbacks(refresh);
        super.onPause();
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER);
        root.setPadding(48, 48, 48, 48);

        TextView title = new TextView(this);
        title.setText("Sony Volume GUI Killer");
        title.setTextSize(28);
        title.setGravity(Gravity.CENTER);

        TextView body = new TextView(this);
        body.setText("请启用无障碍服务，并保持本机 ADB 端口及 key 授权有效。连接失败会自动重试；电视唤醒后立即检查。");
        body.setTextSize(18);
        body.setGravity(Gravity.CENTER);
        body.setPadding(0, 24, 0, 24);

        Button settingsButton = new Button(this);
        settingsButton.setText("打开无障碍设置");
        settingsButton.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View view) {
                startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
            }
        });

        root.addView(title);
        root.addView(body);
        statusView = new TextView(this);
        statusView.setGravity(Gravity.CENTER);
        root.addView(statusView);
        Button retryButton = new Button(this);
        retryButton.setText("立即检查 / 重试");
        retryButton.setOnClickListener(view -> {
            if (VolumeGuardAccessibilityService.isConnected()) {
                VolumeGuardAccessibilityService.requestCheck();
            } else if (RecoveryJobService.isEnabled(this)) {
                RecoveryJobService.schedule(this, false);
            } else {
                startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS));
            }
        });
        root.addView(retryButton);
        root.addView(settingsButton);
        setContentView(root);
    }
}
