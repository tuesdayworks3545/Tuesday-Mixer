using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

[assembly: AssemblyTitle("Tuesday Mixer")]
[assembly: AssemblyDescription("A Windows volume mixer by Tuesday Works")]
[assembly: AssemblyCompany("Tuesday Works")]
[assembly: AssemblyProduct("Tuesday Mixer")]
[assembly: AssemblyCopyright("Copyright © 2026 Tuesday Works")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace TuesdayMixer
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MixerForm());
        }
    }

    internal sealed class MixerForm : Form
    {
        private readonly Color Background = Color.FromArgb(15, 17, 23);
        private readonly Color PanelColor = Color.FromArgb(24, 27, 36);
        private readonly Color TextColor = Color.FromArgb(245, 247, 251);
        private readonly Color SubtleText = Color.FromArgb(157, 167, 183);
        private readonly Color Accent = Color.FromArgb(223, 79, 112);
        private readonly Color LineColor = Color.FromArgb(42, 47, 60);
        private readonly Color Green = Color.FromArgb(72, 213, 151);

        private readonly TuesdaySlider masterSlider = new TuesdaySlider();
        private readonly StereoMeter masterMeter = new StereoMeter();
        private readonly Label masterPercent = new Label();
        private readonly Button masterMute = new Button();
        private readonly FlowLayoutPanel sessionsPanel = new FlowLayoutPanel();
        private readonly Label statusLabel = new Label();
        private readonly Label deviceLabel = new Label();
        private readonly Timer refreshTimer = new Timer();
        private readonly Timer meterTimer = new Timer();
        private readonly Timer sessionTimer = new Timer();
        private readonly Dictionary<string, SessionRow> rows = new Dictionary<string, SessionRow>();
        private readonly List<string> sessionOrder = new List<string>();
        private readonly Dictionary<string, string> channelNames = new Dictionary<string, string>();
        private List<AudioSession> currentSessions = new List<AudioSession>();
        private AudioEngine engine;
        private bool syncing;
        private string lastSignature = "";

        public MixerForm()
        {
            Text = "Tuesday Mixer";
            string appFolder = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string iconPath = System.IO.Path.Combine(appFolder, "tuesday.ico");
            if (System.IO.File.Exists(iconPath)) Icon = new Icon(iconPath);
            Size = new Size(600, 740);
            MinimumSize = new Size(360, 460);
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Background;
            ForeColor = TextColor;
            Font = new Font("Segoe UI", 10F);
            AutoScaleMode = AutoScaleMode.Dpi;

            LoadSessionOrder();
            LoadChannelNames();
            BuildUi();
            Load += (s, e) => ReconnectAndRefresh(true);
            Shown += (s, e) =>
            {
                NativeTheme.ApplyDark(this);
                NativeTheme.ApplyDark(sessionsPanel);
            };
            FormClosed += (s, e) => { refreshTimer.Stop(); meterTimer.Stop(); sessionTimer.Stop(); DisposeEngine(); };

            refreshTimer.Interval = 300;
            refreshTimer.Tick += (s, e) =>
            {
                if (engine == null) ReconnectAndRefresh(false);
                else RefreshFromWindows(false);
            };
            refreshTimer.Start();

            // Audio meters use their own lightweight 30 FPS loop. This keeps their
            // motion fluid without repeating the more expensive session discovery.
            meterTimer.Interval = 33;
            meterTimer.Tick += (s, e) => RefreshMeters();
            meterTimer.Start();

            sessionTimer.Interval = 5000;
            sessionTimer.Tick += (s, e) => { if (engine != null) RefreshFromWindows(true); };
            sessionTimer.Start();
        }

        private void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 108, Padding = new Padding(20, 14, 20, 12), BackColor = Color.FromArgb(18, 20, 28) };
            var logo = new PictureBox
            {
                Location = new Point(18, 14),
                Size = new Size(74, 74),
                SizeMode = PictureBoxSizeMode.Zoom
            };
            string appFolder = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string logoPath = System.IO.Path.Combine(appFolder, "tuesday.png");
            if (System.IO.File.Exists(logoPath)) logo.Image = Image.FromFile(logoPath);
            var title = new Label
            {
                Text = "Tuesday Mixer",
                AutoSize = true,
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 20F),
                Location = new Point(104, 20)
            };
            var subtitle = new Label
            {
                Text = "Simple control. Complete balance.",
                AutoSize = true,
                ForeColor = SubtleText,
                Font = new Font("Segoe UI", 9.5F),
                Location = new Point(107, 58)
            };

            var refresh = MakeButton("↻ Refresh", 86);
            refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            refresh.Location = new Point(486, 32);
            refresh.Click += (s, e) => ReconnectAndRefresh(true);
            header.Resize += (s, e) =>
            {
                refresh.Left = header.ClientSize.Width - refresh.Width - 20;
            };
            header.Controls.Add(logo);
            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(refresh);

            var masterHost = new Panel { Dock = DockStyle.Top, Height = 212, Padding = new Padding(14, 14, 14, 8), BackColor = Background };
            var masterCard = new RoundedPanel
            {
                Dock = DockStyle.Fill,
                Size = new Size(560, 190),
                BackColor = PanelColor,
                BorderColor = LineColor,
                CornerRadius = 15,
                Padding = new Padding(22, 16, 22, 12)
            };
            var masterTitle = new Label
            {
                Text = "Master output",
                AutoSize = true,
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 12F),
                Location = new Point(22, 16)
            };
            deviceLabel.AutoSize = false;
            deviceLabel.Size = new Size(420, 22);
            deviceLabel.Location = new Point(22, 44);
            deviceLabel.ForeColor = SubtleText;
            deviceLabel.Font = new Font("Segoe UI", 9F);
            deviceLabel.AutoEllipsis = true;
            deviceLabel.Text = "Connecting to Windows audio…";
            masterPercent.Text = "--%";
            masterPercent.AutoSize = false;
            masterPercent.TextAlign = ContentAlignment.MiddleRight;
            masterPercent.Font = new Font("Segoe UI Semibold", 11F);
            masterPercent.ForeColor = TextColor;
            masterPercent.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            masterMeter.SetBounds(22, 75, 370, 32);
            masterMeter.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            masterPercent.SetBounds(478, 119, 54, 28);

            ConfigureSlider(masterSlider);
            masterSlider.SetBounds(22, 116, 370, 34);
            masterSlider.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            masterSlider.ValueChanged += (s, e) =>
            {
                masterPercent.Text = masterSlider.Value + "%";
                if (!syncing && engine != null) engine.MasterVolume = masterSlider.Value / 100f;
            };
            masterMute.Text = "🔊";
            masterMute.Font = new Font("Segoe UI Emoji", 11F);
            masterMute.SetBounds(410, 116, 46, 34);
            masterMute.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            StyleFlatButton(masterMute);
            masterMute.Click += (s, e) =>
            {
                if (engine == null) return;
                engine.MasterMuted = !engine.MasterMuted;
                RefreshFromWindows(false);
            };
            masterCard.Resize += (s, e) =>
            {
                masterPercent.Left = masterCard.ClientSize.Width - masterPercent.Width - 22;
                deviceLabel.Width = Math.Max(160, masterCard.ClientSize.Width - 44);
            };
            masterCard.Controls.Add(masterTitle);
            masterCard.Controls.Add(deviceLabel);
            masterCard.Controls.Add(masterMeter);
            masterCard.Controls.Add(masterPercent);
            masterCard.Controls.Add(masterSlider);
            masterCard.Controls.Add(masterMute);
            masterHost.Controls.Add(masterCard);

            var section = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(22, 16, 22, 8), BackColor = Background };
            var appsTitle = new Label
            {
                Text = "Audio mixer",
                AutoSize = true,
                ForeColor = TextColor,
                Font = new Font("Segoe UI Semibold", 13F),
                Location = new Point(22, 17)
            };
            statusLabel.Text = "";
            statusLabel.AutoSize = false;
            statusLabel.TextAlign = ContentAlignment.MiddleRight;
            statusLabel.ForeColor = Green;
            statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            statusLabel.SetBounds(315, 16, 240, 30);
            section.Resize += (s, e) => statusLabel.Left = section.ClientSize.Width - statusLabel.Width - 22;
            section.Controls.Add(appsTitle);
            section.Controls.Add(statusLabel);

            sessionsPanel.Dock = DockStyle.Fill;
            sessionsPanel.FlowDirection = FlowDirection.TopDown;
            sessionsPanel.WrapContents = false;
            sessionsPanel.AutoScroll = true;
            sessionsPanel.AllowDrop = true;
            sessionsPanel.BackColor = Background;
            sessionsPanel.Padding = new Padding(6, 0, 8, 16);
            sessionsPanel.Resize += (s, e) => ResizeRows();
            sessionsPanel.DragEnter += (s, e) =>
            {
                if (e.Data.GetDataPresent(typeof(RoundedPanel))) e.Effect = DragDropEffects.Move;
            };
            sessionsPanel.DragOver += (s, e) =>
            {
                if (e.Data.GetDataPresent(typeof(RoundedPanel))) e.Effect = DragDropEffects.Move;
            };
            sessionsPanel.DragDrop += (s, e) => ReorderFromDrop(e);
            sessionsPanel.HandleCreated += (s, e) => NativeTheme.ApplyDark(sessionsPanel);

            Controls.Add(sessionsPanel);
            Controls.Add(section);
            Controls.Add(masterHost);
            Controls.Add(header);
        }

        private Button MakeButton(string text, int width)
        {
            var button = new Button { Text = text, Size = new Size(width, 36) };
            StyleFlatButton(button);
            return button;
        }

        private void StyleFlatButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = LineColor;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 49, 64);
            button.BackColor = Color.FromArgb(35, 39, 53);
            button.ForeColor = TextColor;
            button.Cursor = Cursors.Hand;
            button.Resize += (s, e) => RoundControl(button, 9);
            RoundControl(button, 9);
        }

        private static void RoundControl(Control control, int radius)
        {
            if (control.Width <= 0 || control.Height <= 0) return;
            using (var path = RoundedPanel.CreateRoundRect(control.ClientRectangle, radius))
                control.Region = new Region(path);
        }

        private void ConfigureSlider(TuesdaySlider slider)
        {
            slider.Minimum = 0;
            slider.Maximum = 100;
            slider.AccentColor = Accent;
            slider.TrackColor = Color.FromArgb(10, 13, 18);
            slider.Cursor = Cursors.Hand;
        }

        private void ReconnectAndRefresh(bool rebuild)
        {
            try
            {
                DisposeEngine();
                engine = new AudioEngine();
                deviceLabel.Text = engine.DeviceName;
                lastSignature = "";
                currentSessions = engine.GetSessions();
                RebuildRows(currentSessions);
                RefreshFromWindows(false);
            }
            catch (Exception ex)
            {
                deviceLabel.Text = "Windows audio is unavailable";
                statusLabel.Text = ex.Message;
                masterSlider.Enabled = false;
                masterMute.Enabled = false;
            }
        }

        private void RefreshFromWindows(bool discoverSessions)
        {
            if (engine == null) return;
            try
            {
                var sessions = currentSessions;
                if (discoverSessions)
                {
                    sessions = engine.GetSessions();
                    currentSessions = sessions;
                    string signature = string.Join("|", sessions.Select(x => x.Key).OrderBy(x => x));
                    if (signature != lastSignature) RebuildRows(sessions);
                }

                syncing = true;
                int master = ClampPercent(engine.MasterVolume);
                if (masterSlider.Value != master) masterSlider.Value = master;
                masterPercent.Text = master + "%";
                SetMuteButton(masterMute, engine.MasterMuted, "Mute main output", "Unmute main output");
                masterSlider.Enabled = true;
                masterMute.Enabled = true;

                foreach (var session in sessions)
                {
                    SessionRow row;
                    if (rows.TryGetValue(session.Key, out row)) row.Sync(session);
                }
                statusLabel.Text = sessions.Count == 1 ? "●  Ready  ·  1 audio app" : "●  Ready  ·  " + sessions.Count + " audio apps";
            }
            catch (Exception ex)
            {
                statusLabel.Text = "Reconnecting…";
                deviceLabel.Text = "Audio connection interrupted: " + ex.Message;
                DisposeEngine();
            }
            finally { syncing = false; }
        }

        private void RebuildRows(List<AudioSession> sessions)
        {
            lastSignature = string.Join("|", sessions.Select(x => x.Key).OrderBy(x => x));
            sessionsPanel.SuspendLayout();
            sessionsPanel.Controls.Clear();
            rows.Clear();

            if (sessions.Count == 0)
            {
                var empty = new Label
                {
                    Text = "♫\r\nNo apps are using audio right now.\r\nStart playing sound and it will appear here.",
                    ForeColor = SubtleText,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 11F),
                    Height = 110,
                    Margin = new Padding(8, 12, 8, 0)
                };
                sessionsPanel.Controls.Add(empty);
            }
            else
            {
                var ordered = sessions.OrderBy(x =>
                {
                    int index = sessionOrder.IndexOf(x.Key);
                    return index < 0 ? int.MaxValue : index;
                }).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
                foreach (var session in ordered)
                {
                    string displayName;
                    if (!channelNames.TryGetValue(session.Key, out displayName)) displayName = session.Name;
                    var row = new SessionRow(session, PanelColor, TextColor, SubtleText, Accent, () => syncing,
                        control => control.DoDragDrop(control, DragDropEffects.Move), displayName,
                        currentName => RenameChannel(session.Key, session.Name, currentName));
                    rows[session.Key] = row;
                    sessionsPanel.Controls.Add(row.Container);
                }
            }
            ResizeRows();
            sessionsPanel.ResumeLayout();
        }

        private void RefreshMeters()
        {
            if (engine == null) return;
            try
            {
                float left, right;
                engine.GetMasterPeaks(out left, out right);
                masterMeter.SetLevels(left, right);
                foreach (var session in currentSessions)
                {
                    SessionRow row;
                    if (rows.TryGetValue(session.Key, out row)) row.SyncMeter(session);
                }
            }
            catch { }
        }

        private void ResizeRows()
        {
            int width = Math.Max(290, sessionsPanel.ClientSize.Width - sessionsPanel.Padding.Horizontal - 22);
            foreach (Control control in sessionsPanel.Controls) control.Width = width;
        }

        private void ReorderFromDrop(DragEventArgs e)
        {
            var dragged = e.Data.GetData(typeof(RoundedPanel)) as Control;
            if (dragged == null || !sessionsPanel.Controls.Contains(dragged)) return;
            Point point = sessionsPanel.PointToClient(new Point(e.X, e.Y));
            int target = sessionsPanel.Controls.Count - 1;
            for (int i = 0; i < sessionsPanel.Controls.Count; i++)
            {
                Control candidate = sessionsPanel.Controls[i];
                if (candidate == dragged) continue;
                if (point.Y < candidate.Top + candidate.Height / 2) { target = i; break; }
            }
            sessionsPanel.Controls.SetChildIndex(dragged, target);
            sessionsPanel.PerformLayout();
            sessionOrder.Clear();
            foreach (Control control in sessionsPanel.Controls)
                if (control.Tag is string) sessionOrder.Add((string)control.Tag);
            SaveSessionOrder();
        }

        private string GetOrderFilePath()
        {
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tuesday Mixer");
            return System.IO.Path.Combine(folder, "channel-order.txt");
        }

        private void LoadSessionOrder()
        {
            try
            {
                string path = GetOrderFilePath();
                if (System.IO.File.Exists(path)) sessionOrder.AddRange(System.IO.File.ReadAllLines(path).Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            catch { }
        }

        private void SaveSessionOrder()
        {
            try
            {
                string path = GetOrderFilePath();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllLines(path, sessionOrder.ToArray());
            }
            catch { }
        }

        private string GetNamesFilePath()
        {
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tuesday Mixer");
            return System.IO.Path.Combine(folder, "channel-names.txt");
        }

        private void LoadChannelNames()
        {
            try
            {
                string path = GetNamesFilePath();
                if (!System.IO.File.Exists(path)) return;
                foreach (string line in System.IO.File.ReadAllLines(path))
                {
                    int separator = line.IndexOf('\t');
                    if (separator <= 0) continue;
                    string key = line.Substring(0, separator);
                    string encoded = line.Substring(separator + 1);
                    string value = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    if (!string.IsNullOrWhiteSpace(value)) channelNames[key] = value;
                }
            }
            catch { }
        }

        private void SaveChannelNames()
        {
            try
            {
                string path = GetNamesFilePath();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                var lines = channelNames.OrderBy(x => x.Key).Select(x =>
                    x.Key + "\t" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(x.Value)));
                System.IO.File.WriteAllLines(path, lines.ToArray());
            }
            catch { }
        }

        private void RenameChannel(string key, string originalName, string currentName)
        {
            using (var dialog = new Form())
            {
                dialog.Text = "Rename mixer channel";
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.ClientSize = new Size(430, 172);
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.BackColor = Background;
                dialog.ForeColor = TextColor;
                dialog.Font = Font;

                var prompt = new Label
                {
                    Text = "Channel name",
                    AutoSize = true,
                    ForeColor = TextColor,
                    Location = new Point(20, 18)
                };
                var input = new TextBox
                {
                    Text = currentName,
                    MaxLength = 80,
                    BackColor = Color.FromArgb(24, 27, 36),
                    ForeColor = TextColor,
                    BorderStyle = BorderStyle.FixedSingle,
                    Location = new Point(22, 45),
                    Size = new Size(386, 28)
                };
                var hint = new Label
                {
                    Text = "The original app name is “" + originalName + "”.",
                    AutoEllipsis = true,
                    ForeColor = SubtleText,
                    Location = new Point(22, 79),
                    Size = new Size(386, 22)
                };
                var reset = MakeButton("Use app name", 112);
                reset.Location = new Point(22, 119);
                reset.DialogResult = DialogResult.No;
                var cancel = MakeButton("Cancel", 82);
                cancel.Location = new Point(232, 119);
                cancel.DialogResult = DialogResult.Cancel;
                var save = MakeButton("Save", 82);
                save.Location = new Point(326, 119);
                save.BackColor = Accent;
                save.DialogResult = DialogResult.OK;

                dialog.Controls.Add(prompt);
                dialog.Controls.Add(input);
                dialog.Controls.Add(hint);
                dialog.Controls.Add(reset);
                dialog.Controls.Add(cancel);
                dialog.Controls.Add(save);
                dialog.AcceptButton = save;
                dialog.CancelButton = cancel;
                dialog.Shown += (s, e) =>
                {
                    NativeTheme.ApplyDark(dialog);
                    input.SelectAll();
                    input.Focus();
                };

                DialogResult result = dialog.ShowDialog(this);
                string newName = originalName;
                if (result == DialogResult.OK)
                {
                    string entered = input.Text.Trim();
                    if (string.IsNullOrWhiteSpace(entered)) channelNames.Remove(key);
                    else { channelNames[key] = entered; newName = entered; }
                }
                else if (result == DialogResult.No) channelNames.Remove(key);
                else return;

                SaveChannelNames();
                SessionRow row;
                if (rows.TryGetValue(key, out row)) row.SetDisplayName(newName);
            }
        }

        private static int ClampPercent(float value)
        {
            return Math.Max(0, Math.Min(100, (int)Math.Round(value * 100)));
        }

        private void SetMuteButton(Button button, bool muted, string normal, string mutedText)
        {
            button.Text = muted ? "🔇" : "🔊";
            button.BackColor = muted ? Color.FromArgb(77, 37, 52) : Color.FromArgb(35, 39, 53);
        }

        private void DisposeEngine()
        {
            if (engine != null) { engine.Dispose(); engine = null; }
            currentSessions = new List<AudioSession>();
        }

        private sealed class SessionRow
        {
            public readonly Panel Container;
            private readonly Label name;
            private readonly Label percent;
            private readonly StereoMeter meter;
            private readonly TuesdaySlider slider;
            private readonly Button mute;
            private readonly Func<bool> isParentSyncing;
            private AudioSession session;
            private bool syncing;

            public SessionRow(AudioSession initial, Color panelColor, Color textColor, Color subtle, Color accent,
                Func<bool> parentSyncing, Action<Control> beginDrag, string displayName, Action<string> requestRename)
            {
                session = initial;
                isParentSyncing = parentSyncing;
                Container = new RoundedPanel
                {
                    Width = 520,
                    Height = 150,
                    BackColor = panelColor,
                    BorderColor = Color.FromArgb(42, 47, 60),
                    CornerRadius = 15,
                    Margin = new Padding(8, 0, 8, 10)
                };
                Container.Tag = initial.Key;

                var sourceIcon = new Label
                {
                    Text = "♫",
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = Color.FromArgb(242, 123, 149),
                    BackColor = Color.FromArgb(44, 32, 48),
                    Font = new Font("Segoe UI Symbol", 15F),
                    Location = new Point(16, 15),
                    Size = new Size(40, 40)
                };
                sourceIcon.Resize += (s, e) => RoundControl(sourceIcon, 9);
                RoundControl(sourceIcon, 9);

                name = new Label
                {
                    Text = displayName,
                    AutoEllipsis = true,
                    ForeColor = textColor,
                    Font = new Font("Segoe UI Semibold", 11F),
                    Location = new Point(67, 13),
                    Size = new Size(280, 24),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Cursor = Cursors.Hand
                };
                name.DoubleClick += (s, e) => requestRename(name.Text);
                var kind = new Label
                {
                    Text = "Application audio",
                    ForeColor = subtle,
                    Font = new Font("Segoe UI", 8.5F),
                    Location = new Point(68, 37),
                    Size = new Size(220, 20)
                };
                percent = new Label
                {
                    TextAlign = ContentAlignment.MiddleRight,
                    ForeColor = textColor,
                    Font = new Font("Segoe UI Semibold", 10F),
                    Size = new Size(58, 26),
                    Location = new Point(354, 17),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                var renameButton = new Button
                {
                    Text = "✎",
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = textColor,
                    BackColor = Color.FromArgb(35, 39, 53),
                    Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI Symbol", 10F),
                    Size = new Size(34, 32),
                    Location = new Point(424, 14),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    TabStop = false
                };
                renameButton.FlatAppearance.BorderColor = Color.FromArgb(42, 47, 60);
                renameButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 49, 64);
                renameButton.Resize += (s, e) => RoundControl(renameButton, 8);
                RoundControl(renameButton, 8);
                renameButton.Click += (s, e) => requestRename(name.Text);
                var dragHandle = new Label
                {
                    Text = "☷",
                    TextAlign = ContentAlignment.MiddleCenter,
                    ForeColor = subtle,
                    Font = new Font("Segoe UI Symbol", 17F),
                    Cursor = Cursors.SizeAll,
                    Size = new Size(38, 34),
                    Location = new Point(466, 13),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                dragHandle.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) beginDrag(Container); };
                var tip = new ToolTip();
                tip.SetToolTip(name, "Double-click to rename this channel");
                tip.SetToolTip(renameButton, "Rename this channel");
                tip.SetToolTip(dragHandle, "Drag to rearrange this channel");

                meter = new StereoMeter
                {
                    Location = new Point(18, 64),
                    Size = new Size(388, 32),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                slider = new TuesdaySlider
                {
                    Minimum = 0,
                    Maximum = 100,
                    AccentColor = accent,
                    TrackColor = Color.FromArgb(10, 13, 18),
                    Cursor = Cursors.Hand,
                    Location = new Point(18, 108),
                    Size = new Size(388, 32),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };
                mute = new Button
                {
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = textColor,
                    BackColor = Color.FromArgb(35, 39, 53),
                    Cursor = Cursors.Hand,
                    Font = new Font("Segoe UI Emoji", 10.5F),
                    Size = new Size(44, 34),
                    Location = new Point(420, 106),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                mute.FlatAppearance.BorderColor = Color.FromArgb(42, 47, 60);
                mute.FlatAppearance.MouseOverBackColor = Color.FromArgb(45, 49, 64);
                mute.Resize += (s, e) => RoundControl(mute, 9);
                RoundControl(mute, 9);

                slider.ValueChanged += (s, e) =>
                {
                    percent.Text = slider.Value + "%";
                    if (!syncing && !isParentSyncing()) session.Volume = slider.Value / 100f;
                };
                mute.Click += (s, e) =>
                {
                    session.Muted = !session.Muted;
                    Sync(session);
                };

                Container.Controls.Add(sourceIcon);
                Container.Controls.Add(name);
                Container.Controls.Add(kind);
                Container.Controls.Add(percent);
                Container.Controls.Add(renameButton);
                Container.Controls.Add(dragHandle);
                Container.Controls.Add(meter);
                Container.Controls.Add(slider);
                Container.Controls.Add(mute);
                Sync(initial);
            }

            public void Sync(AudioSession current)
            {
                session = current;
                syncing = true;
                int value = ClampPercent(current.Volume);
                if (slider.Value != value) slider.Value = value;
                percent.Text = value + "%";
                mute.Text = current.Muted ? "🔇" : "🔊";
                mute.BackColor = current.Muted ? Color.FromArgb(77, 37, 52) : Color.FromArgb(35, 39, 53);
                syncing = false;
            }

            public void SetDisplayName(string displayName)
            {
                name.Text = displayName;
            }

            public void SyncMeter(AudioSession current)
            {
                float left, right;
                current.GetPeaks(out left, out right);
                meter.SetLevels(left, right);
            }
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        public int CornerRadius { get; set; }
        public Color BorderColor { get; set; }

        public RoundedPanel()
        {
            CornerRadius = 12;
            BorderColor = Color.FromArgb(42, 47, 60);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            if (Width > 0 && Height > 0)
            {
                using (var path = CreateRoundRect(ClientRectangle, CornerRadius)) Region = new Region(path);
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = CreateRoundRect(rect, CornerRadius))
            using (var pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, path);
        }

        public static GraphicsPath CreateRoundRect(Rectangle bounds, int radius)
        {
            int diameter = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class TuesdaySlider : Control
    {
        private int minimum;
        private int maximum = 100;
        private int currentValue;
        private bool dragging;

        public event EventHandler ValueChanged;
        public Color AccentColor { get; set; }
        public Color TrackColor { get; set; }
        public int Minimum { get { return minimum; } set { minimum = value; Value = currentValue; } }
        public int Maximum { get { return maximum; } set { maximum = Math.Max(value, minimum + 1); Value = currentValue; } }
        public int Value
        {
            get { return currentValue; }
            set
            {
                int next = Math.Max(minimum, Math.Min(maximum, value));
                if (next == currentValue) return;
                currentValue = next;
                Invalidate();
                var handler = ValueChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
        }

        public TuesdaySlider()
        {
            AccentColor = Color.FromArgb(223, 79, 112);
            TrackColor = Color.FromArgb(10, 13, 18);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            TabStop = true;
            Height = 32;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = 9;
            int y = Height / 2;
            int usable = Math.Max(1, Width - pad * 2);
            float ratio = (currentValue - minimum) / (float)(maximum - minimum);
            int x = pad + (int)Math.Round(usable * ratio);
            using (var track = new Pen(TrackColor, 7F))
            using (var active = new Pen(Enabled ? AccentColor : Color.FromArgb(90, 90, 96), 7F))
            using (var thumb = new SolidBrush(Enabled ? Color.FromArgb(245, 247, 251) : Color.FromArgb(130, 130, 136)))
            {
                track.StartCap = track.EndCap = LineCap.Round;
                active.StartCap = active.EndCap = LineCap.Round;
                e.Graphics.DrawLine(track, pad, y, Width - pad, y);
                e.Graphics.DrawLine(active, pad, y, x, y);
                e.Graphics.FillEllipse(thumb, x - 8, y - 8, 16, 16);
                using (var outline = new Pen(AccentColor, 2F)) e.Graphics.DrawEllipse(outline, x - 8, y - 8, 16, 16);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Focus(); dragging = true; SetFromX(e.X);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) SetFromX(e.X);
        }

        protected override void OnMouseUp(MouseEventArgs e) { dragging = false; base.OnMouseUp(e); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) { Value--; e.Handled = true; }
            if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) { Value++; e.Handled = true; }
            base.OnKeyDown(e);
        }

        private void SetFromX(int x)
        {
            int pad = 9;
            float ratio = (x - pad) / (float)Math.Max(1, Width - pad * 2);
            ratio = Math.Max(0, Math.Min(1, ratio));
            Value = minimum + (int)Math.Round((maximum - minimum) * ratio);
        }
    }

    internal sealed class StereoMeter : Control
    {
        private float leftLevel;
        private float rightLevel;

        public StereoMeter()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.FromArgb(24, 27, 36);
            Height = 32;
        }

        public void SetLevels(float left, float right)
        {
            left = Math.Max(0, Math.Min(1, left));
            right = Math.Max(0, Math.Min(1, right));
            leftLevel = SmoothLevel(leftLevel, left);
            rightLevel = SmoothLevel(rightLevel, right);
            Invalidate();
        }

        private static float SmoothLevel(float current, float target)
        {
            // Quick attack catches transients; slower release prevents flicker.
            float factor = target > current ? 0.72f : 0.18f;
            float next = current + (target - current) * factor;
            return Math.Abs(next) < 0.002f ? 0 : next;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var font = new Font("Segoe UI", 7.5F))
            using (var labelBrush = new SolidBrush(Color.FromArgb(157, 167, 183)))
            {
                e.Graphics.DrawString("L", font, labelBrush, 0, 1);
                e.Graphics.DrawString("R", font, labelBrush, 0, 17);
            }
            DrawBar(e.Graphics, new Rectangle(17, 4, Math.Max(8, Width - 17), 8), leftLevel);
            DrawBar(e.Graphics, new Rectangle(17, 20, Math.Max(8, Width - 17), 8), rightLevel);
        }

        private static void DrawBar(Graphics graphics, Rectangle bounds, float level)
        {
            using (var trackPath = RoundedPanel.CreateRoundRect(bounds, 4))
            using (var trackBrush = new SolidBrush(Color.FromArgb(9, 12, 17)))
                graphics.FillPath(trackBrush, trackPath);
            int activeWidth = (int)Math.Round(bounds.Width * level);
            if (activeWidth <= 0) return;
            var activeBounds = new Rectangle(bounds.X, bounds.Y, Math.Max(2, activeWidth), bounds.Height);
            using (var gradient = new LinearGradientBrush(bounds, Color.FromArgb(72, 213, 151), Color.FromArgb(239, 84, 115), 0F))
            {
                var blend = new ColorBlend();
                blend.Colors = new[] { Color.FromArgb(72, 213, 151), Color.FromArgb(211, 224, 82), Color.FromArgb(239, 84, 115) };
                blend.Positions = new[] { 0F, 0.72F, 1F };
                gradient.InterpolationColors = blend;
                GraphicsState state = graphics.Save();
                graphics.SetClip(activeBounds);
                using (var activePath = RoundedPanel.CreateRoundRect(bounds, 4)) graphics.FillPath(gradient, activePath);
                graphics.Restore(state);
            }
        }
    }

    internal sealed class AudioSession
    {
        private readonly List<ISimpleAudioVolume> volumes = new List<ISimpleAudioVolume>();
        private readonly List<IAudioMeterInformation> meters = new List<IAudioMeterInformation>();
        public string Key { get; private set; }
        public string Name { get; private set; }

        public AudioSession(string key, string name, ISimpleAudioVolume volume, IAudioMeterInformation meter)
        {
            Key = key;
            Name = name;
            volumes.Add(volume);
            if (meter != null) meters.Add(meter);
        }

        public void AddVolume(ISimpleAudioVolume volume, IAudioMeterInformation meter)
        {
            volumes.Add(volume);
            if (meter != null) meters.Add(meter);
        }

        public void GetPeaks(out float left, out float right)
        {
            left = 0; right = 0;
            foreach (var meter in meters)
            {
                float meterLeft, meterRight;
                AudioEngine.ReadPeaks(meter, out meterLeft, out meterRight);
                left = Math.Max(left, meterLeft);
                right = Math.Max(right, meterRight);
            }
        }

        public float Volume
        {
            get { float v; volumes[0].GetMasterVolume(out v); return v; }
            set
            {
                Guid context = Guid.Empty;
                float level = Math.Max(0, Math.Min(1, value));
                foreach (var volume in volumes) volume.SetMasterVolume(level, ref context);
            }
        }

        public bool Muted
        {
            get
            {
                foreach (var volume in volumes) { bool muted; volume.GetMute(out muted); if (!muted) return false; }
                return true;
            }
            set { Guid context = Guid.Empty; foreach (var volume in volumes) volume.SetMute(value, ref context); }
        }
    }

    internal sealed class AudioEngine : IDisposable
    {
        private IMMDevice device;
        private IAudioEndpointVolume endpoint;
        private IAudioMeterInformation endpointMeter;
        private IAudioSessionManager2 manager;
        public string DeviceName { get; private set; }

        public AudioEngine()
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out device));
            object endpointObject;
            Guid endpointId = typeof(IAudioEndpointVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref endpointId, 23, IntPtr.Zero, out endpointObject));
            endpoint = (IAudioEndpointVolume)endpointObject;
            object meterObject;
            Guid meterId = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref meterId, 23, IntPtr.Zero, out meterObject) == 0)
                endpointMeter = (IAudioMeterInformation)meterObject;
            object managerObject;
            Guid managerId = typeof(IAudioSessionManager2).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref managerId, 23, IntPtr.Zero, out managerObject));
            manager = (IAudioSessionManager2)managerObject;
            DeviceName = "Default Windows output";
            try
            {
                IPropertyStore store;
                if (device.OpenPropertyStore(0, out store) == 0)
                {
                    var key = PropertyKeys.PKEY_Device_FriendlyName;
                    PropVariant value;
                    if (store.GetValue(ref key, out value) == 0)
                    {
                        string friendly = value.GetString();
                        if (!string.IsNullOrWhiteSpace(friendly)) DeviceName = friendly;
                        value.Clear();
                    }
                    Marshal.ReleaseComObject(store);
                }
            }
            catch { }
            Marshal.ReleaseComObject(enumerator);
        }

        public float MasterVolume
        {
            get { float value; Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out value)); return value; }
            set { Guid context = Guid.Empty; Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(Math.Max(0, Math.Min(1, value)), ref context)); }
        }

        public bool MasterMuted
        {
            get { bool value; Marshal.ThrowExceptionForHR(endpoint.GetMute(out value)); return value; }
            set { Guid context = Guid.Empty; Marshal.ThrowExceptionForHR(endpoint.SetMute(value, ref context)); }
        }

        public void GetMasterPeaks(out float left, out float right)
        {
            ReadPeaks(endpointMeter, out left, out right);
        }

        internal static void ReadPeaks(IAudioMeterInformation meter, out float left, out float right)
        {
            left = 0; right = 0;
            if (meter == null) return;
            try
            {
                int channels;
                if (meter.GetMeteringChannelCount(out channels) == 0 && channels > 0)
                {
                    var values = new float[channels];
                    if (meter.GetChannelsPeakValues(channels, values) == 0)
                    {
                        left = values[0];
                        right = channels > 1 ? values[1] : values[0];
                        return;
                    }
                }
                float peak;
                if (meter.GetPeakValue(out peak) == 0) left = right = peak;
            }
            catch { left = right = 0; }
        }

        public List<AudioSession> GetSessions()
        {
            IAudioSessionEnumerator enumerator;
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out enumerator));
            int count;
            Marshal.ThrowExceptionForHR(enumerator.GetCount(out count));
            var result = new Dictionary<string, AudioSession>();
            var processKeys = new Dictionary<uint, string>();
            for (int i = 0; i < count; i++)
            {
                IAudioSessionControl control;
                if (enumerator.GetSession(i, out control) != 0) continue;
                var control2 = control as IAudioSessionControl2;
                var simple = control as ISimpleAudioVolume;
                var meter = control as IAudioMeterInformation;
                if (control2 == null || simple == null) continue;

                AudioSessionState state;
                control.GetState(out state);
                if (state == AudioSessionState.Expired) continue;

                uint pid;
                control2.GetProcessId(out pid);
                string key;
                AudioSession existing;
                if (processKeys.TryGetValue(pid, out key) && result.TryGetValue(key, out existing))
                {
                    existing.AddVolume(simple, meter);
                    continue;
                }
                if (pid != 0 && !IsProcessRunning(pid)) continue;
                string display = "";
                control.GetDisplayName(out display);
                string name = ResolveName(pid, display);
                key = pid == 0 ? "system-sounds" : "app:" + name.ToLowerInvariant();
                processKeys[pid] = key;
                if (result.TryGetValue(key, out existing)) existing.AddVolume(simple, meter);
                else result[key] = new AudioSession(key, name, simple, meter);
            }
            Marshal.ReleaseComObject(enumerator);
            return result.Values.ToList();
        }

        private static string ResolveName(uint pid, string display)
        {
            if (!string.IsNullOrWhiteSpace(display) && !display.StartsWith("@")) return display;
            if (pid == 0) return "System sounds";
            try
            {
                using (var process = Process.GetProcessById((int)pid))
                {
                    try
                    {
                        string description = process.MainModule.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(description)) return description;
                    }
                    catch { }
                    string n = process.ProcessName;
                    return char.ToUpper(n[0]) + n.Substring(1);
                }
            }
            catch { return "App (" + pid + ")"; }
        }

        private static bool IsProcessRunning(uint pid)
        {
            try
            {
                using (var process = Process.GetProcessById((int)pid)) return !process.HasExited;
            }
            catch { return false; }
        }

        public void Dispose()
        {
            if (manager != null && Marshal.IsComObject(manager)) Marshal.ReleaseComObject(manager);
            if (endpointMeter != null && Marshal.IsComObject(endpointMeter)) Marshal.ReleaseComObject(endpointMeter);
            if (endpoint != null && Marshal.IsComObject(endpoint)) Marshal.ReleaseComObject(endpoint);
            if (device != null && Marshal.IsComObject(device)) Marshal.ReleaseComObject(device);
            manager = null; endpoint = null; device = null;
        }
    }

    internal enum EDataFlow { eRender, eCapture, eAll }
    internal enum ERole { eConsole, eMultimedia, eCommunications }
    internal enum AudioSessionState { Inactive, Active, Expired }

    internal static class NativeTheme
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr handle, string subAppName, string subIdList);

        public static void ApplyDark(Control control)
        {
            if (control == null || !control.IsHandleCreated) return;
            try
            {
                int enabled = 1;
                DwmSetWindowAttribute(control.Handle, 20, ref enabled, sizeof(int));
                DwmSetWindowAttribute(control.Handle, 19, ref enabled, sizeof(int));
                int captionColor = 0x0018170F;
                int captionText = 0x00FBF7F5;
                int borderColor = 0x003C2F2A;
                DwmSetWindowAttribute(control.Handle, 35, ref captionColor, sizeof(int));
                DwmSetWindowAttribute(control.Handle, 36, ref captionText, sizeof(int));
                DwmSetWindowAttribute(control.Handle, 34, ref borderColor, sizeof(int));
                SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
            }
            catch { }
        }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid id, uint context, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
        int OpenPropertyStore(uint access, out IPropertyStore properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out uint state);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PropertyKey key);
        int GetValue(ref PropertyKey key, out PropVariant value);
        int SetValue(ref PropertyKey key, ref PropVariant value);
        int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PropertyKey { public Guid formatId; public int propertyId; }

    internal static class PropertyKeys
    {
        public static PropertyKey PKEY_Device_FriendlyName = new PropertyKey
        {
            formatId = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), propertyId = 14
        };
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct PropVariant
    {
        [FieldOffset(0)] private ushort type;
        [FieldOffset(8)] private IntPtr pointer;
        public string GetString() { return type == 31 && pointer != IntPtr.Zero ? Marshal.PtrToStringUni(pointer) : null; }
        public void Clear() { PropVariantClear(ref this); }
        [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant variant);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr notify);
        int UnregisterControlChangeNotify(IntPtr notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float levelDb, ref Guid context);
        int SetMasterVolumeLevelScalar(float level, ref Guid context);
        int GetMasterVolumeLevel(out float levelDb);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        int GetChannelVolumeLevel(uint channel, out float levelDb);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(ref Guid context);
        int VolumeStepDown(ref Guid context);
        int QueryHardwareSupport(out uint mask);
        int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionManager2
    {
        int GetAudioSessionControl(ref Guid sessionGuid, uint flags, out IAudioSessionControl control);
        int GetSimpleAudioVolume(ref Guid sessionGuid, uint flags, out ISimpleAudioVolume volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
        int RegisterSessionNotification(IntPtr notification);
        int UnregisterSessionNotification(IntPtr notification);
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl control);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl
    {
        int GetState(out AudioSessionState state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        int GetGroupingParam(out Guid grouping);
        int SetGroupingParam(ref Guid grouping, ref Guid context);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioSessionControl2
    {
        int GetState(out AudioSessionState state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
        int GetGroupingParam(out Guid grouping);
        int SetGroupingParam(ref Guid grouping, ref Guid context);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out uint processId);
        int IsSystemSoundsSession();
        int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid context);
        int GetMasterVolume(out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioMeterInformation
    {
        int GetPeakValue(out float peak);
        int GetMeteringChannelCount(out int channelCount);
        int GetChannelsPeakValues(int channelCount, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] float[] peakValues);
        int QueryHardwareSupport(out int hardwareSupportMask);
    }
}
