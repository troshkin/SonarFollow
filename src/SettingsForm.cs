using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

class SettingsForm : Form
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "SonarFollow";
    static readonly bool Ru = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";

    readonly AppSettings settings;
    readonly int wrapWidth;
    readonly Label sonarStatus, agentStatus;
    readonly Button agentButton;
    readonly TableLayoutPanel devicesTable;
    readonly System.Windows.Forms.Timer refreshTimer;
    bool devicesFromSonar, checking;

    static string T(string en, string ru) { return Ru ? ru : en; }

    static readonly DeviceMode[] Modes = { DeviceMode.Sonar, DeviceMode.Direct, DeviceMode.Ignore };
    static string ModeLabel(DeviceMode m)
    {
        switch (m)
        {
            case DeviceMode.Direct: return T("Direct", "Напрямую");
            case DeviceMode.Ignore: return T("Don't touch", "Не трогать");
            default: return T("Through Sonar", "Через Sonar");
        }
    }

    public SettingsForm()
    {
        settings = AppSettings.TryLoad() ?? new AppSettings();

        Text = "SonarFollow";
        Font = SystemFonts.MessageBoxFont;
        AutoScaleMode = AutoScaleMode.Font;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(16);
        Icon = Icon.ExtractAssociatedIcon(Assembly.GetEntryAssembly().Location);
        wrapWidth = Font.Height * 32;

        // Form.Padding only applies to docked controls, so place the panel explicitly.
        var root = new TableLayoutPanel
        {
            ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Location = new Point(Padding.Left, Padding.Top)
        };

        sonarStatus = new Label { AutoSize = true, MaximumSize = new Size(wrapWidth, 0) };
        root.Controls.Add(sonarStatus);

        root.Controls.Add(Header(T("Output devices", "Устройства вывода")));
        devicesTable = new TableLayoutPanel
        {
            ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 4, 0, 4)
        };
        root.Controls.Add(devicesTable);
        root.Controls.Add(Hint(T(
            "Through Sonar: audio is processed by Sonar, Windows switches back to Sonar - Gaming.\r\n" +
            "Direct: Windows stays on the device, Sonar is routed there too.\r\n" +
            "Don't touch: SonarFollow does nothing.",
            "Через Sonar — звук идёт через Sonar, в Windows снова выбирается Sonar - Gaming.\r\n" +
            "Напрямую — Windows остаётся на устройстве, Sonar тоже переключается на него.\r\n" +
            "Не трогать — SonarFollow ничего не делает.")));

        var syncVolume = new CheckBox
        {
            Text = T("Sync Sonar channel volumes", "Синхронизировать громкость каналов Sonar"),
            AutoSize = true, Checked = settings.SyncVolume, Margin = new Padding(0, 16, 0, 0)
        };
        syncVolume.CheckedChanged += (s, e) => { settings.SyncVolume = syncVolume.Checked; Save(); };
        root.Controls.Add(syncVolume);
        root.Controls.Add(Hint(T(
            "Windows volume keys change Chat, Media and Aux together with Gaming, keeping their balance.",
            "Регулятор громкости Windows меняет Chat, Media и Aux вместе с Gaming, сохраняя баланс между ними.")));

        var autostart = new CheckBox
        {
            Text = T("Start with Windows", "Запускать вместе с Windows"),
            AutoSize = true, Checked = IsAutostart(), Margin = new Padding(0, 12, 0, 0)
        };
        autostart.CheckedChanged += (s, e) => SetAutostart(autostart.Checked);
        root.Controls.Add(autostart);

        var agentRow = new FlowLayoutPanel
        {
            AutoSize = true, WrapContents = false, Margin = new Padding(0, 16, 0, 0)
        };
        agentStatus = new Label { AutoSize = true, Margin = new Padding(0, 7, 8, 0) };
        agentButton = new Button { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 0, 6, 0) };
        agentButton.Click += (s, e) => ToggleAgent();
        var logLink = new LinkLabel { Text = T("Open log", "Открыть лог"), AutoSize = true, Margin = new Padding(12, 7, 0, 0) };
        logLink.LinkClicked += (s, e) => { if (File.Exists(Log.Path)) Process.Start(Log.Path); };
        agentRow.Controls.Add(agentStatus);
        agentRow.Controls.Add(agentButton);
        agentRow.Controls.Add(logLink);
        root.Controls.Add(agentRow);

        root.Controls.Add(Hint(T(
            "Changes are saved immediately. You can close this window, SonarFollow keeps working in the background.",
            "Изменения сохраняются сразу. Окно можно закрыть — SonarFollow продолжит работать в фоне.")));

        Controls.Add(root);

        ShowDevices(SavedDevices(), false);
        refreshTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        refreshTimer.Tick += (s, e) => RefreshStatus();
        refreshTimer.Start();
        RefreshStatus();
    }

    Label Header(string text)
    {
        return new Label
        {
            Text = text, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 16, 0, 0)
        };
    }

    Label Hint(string text)
    {
        return new Label
        {
            Text = text, AutoSize = true, MaximumSize = new Size(wrapWidth, 0),
            ForeColor = SystemColors.GrayText, Margin = new Padding(0, 4, 0, 0)
        };
    }

    List<SonarDevice> SavedDevices()
    {
        return settings.Devices.Select(d => new SonarDevice { Id = d.Id, Name = d.Name }).ToList();
    }

    void ShowDevices(List<SonarDevice> devices, bool fromSonar)
    {
        devicesFromSonar = fromSonar;
        devicesTable.SuspendLayout();
        devicesTable.Controls.Clear();
        devicesTable.RowCount = 0;

        if (devices.Count == 0)
        {
            devicesTable.Controls.Add(Hint(T("No devices yet: start SteelSeries GG with Sonar.",
                                             "Устройств пока нет: запустите SteelSeries GG с Sonar.")));
        }
        foreach (var dev in devices)
        {
            var name = new Label
            {
                Text = dev.Name, AutoSize = true, MaximumSize = new Size(wrapWidth - Font.Height * 11, 0),
                Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 12, 4)
            };
            var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Font.Height * 10 };
            foreach (var m in Modes) combo.Items.Add(ModeLabel(m));
            combo.SelectedIndex = Array.IndexOf(Modes, settings.ModeFor(dev.Id, dev.Name));

            var device = dev;
            combo.SelectedIndexChanged += (s, e) => SetMode(device, Modes[combo.SelectedIndex]);
            devicesTable.Controls.Add(name);
            devicesTable.Controls.Add(combo);
        }
        devicesTable.ResumeLayout();
    }

    void SetMode(SonarDevice dev, DeviceMode mode)
    {
        var entry = settings.Find(dev.Id, dev.Name);
        if (entry == null)
        {
            entry = new DeviceSetting();
            settings.Devices.Add(entry);
        }
        entry.Id = dev.Id;
        entry.Name = dev.Name;
        entry.Mode = AppSettings.ModeName(mode);
        Save();
    }

    void Save()
    {
        try { settings.Save(); }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "SonarFollow", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // Sonar is queried off the UI thread: discovery may run netstat and time out.
    void RefreshStatus()
    {
        bool running = Agent.IsRunning();
        agentStatus.Text = running ? T("Background process: running", "Фоновый процесс: работает")
                                   : T("Background process: stopped", "Фоновый процесс: остановлен");
        agentButton.Text = running ? T("Stop", "Остановить") : T("Start", "Запустить");

        if (checking) return;
        checking = true;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string mode = null;
            List<SonarDevice> devices = null;
            try
            {
                mode = Sonar.GetMode();
                if (mode != null && !devicesFromSonar) devices = Sonar.GetDevices();
            }
            catch { }
            if (!IsHandleCreated) return;
            BeginInvoke((Action)(() =>
            {
                checking = false;
                ShowSonarStatus(mode);
                if (devices != null)
                    ShowDevices(devices.Where(d => !d.IsVad && d.IsActive && d.Flow == "render").ToList(), true);
            }));
        });
    }

    void ShowSonarStatus(string mode)
    {
        if (mode == null)
        {
            sonarStatus.Text = T("Sonar not found. Start SteelSeries GG with Sonar enabled.",
                                 "Sonar не найден. Запустите SteelSeries GG с включённым Sonar.");
            sonarStatus.ForeColor = Color.Firebrick;
        }
        else if (mode != "classic")
        {
            sonarStatus.Text = T("Sonar is in Streamer mode. SonarFollow only works in Classic mode.",
                                 "Sonar в режиме стримера. SonarFollow работает только в обычном режиме.");
            sonarStatus.ForeColor = Color.DarkOrange;
        }
        else
        {
            sonarStatus.Text = T("Sonar connected", "Sonar подключён");
            sonarStatus.ForeColor = Color.ForestGreen;
        }
    }

    void ToggleAgent()
    {
        if (Agent.IsRunning()) Agent.Stop();
        else Agent.Start();
        var t = new System.Windows.Forms.Timer { Interval = 700 };
        t.Tick += (s, e) => { t.Dispose(); RefreshStatus(); };
        t.Start();
    }

    static bool IsAutostart()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey))
            return key != null && key.GetValue(RunValue) != null;
    }

    static void SetAutostart(bool on)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) key.SetValue(RunValue, "\"" + Assembly.GetEntryAssembly().Location + "\" --agent");
            else key.DeleteValue(RunValue, false);
        }
    }
}
