using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.ServiceProcess;
using System.Security.Principal;
using System.Text;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SimpleUnlockerRemastered
{
    public class MainForm : Form
    {
        private const string WinlogonKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        private static readonly string[] StartupKeys = { @"Software\Microsoft\Windows\CurrentVersion\Run", @"Software\Microsoft\Windows\CurrentVersion\RunOnce" };

        private TabControl tabs = new TabControl();
        private TextBox logBox;
        private System.Windows.Forms.Timer processTimer;
        private int liveTickCounter;
        private readonly bool lowResourceMode = Environment.ProcessorCount <= 2 || GetPhysicalMemoryGb() <= 6.0;

        private static double GetPhysicalMemoryGb()
        {
            try { var m = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
                return NativeMethods.GlobalMemoryStatusEx(ref m) ? m.ullTotalPhys / 1073741824.0 : 8.0; }
            catch { return 8.0; }
        }
        private bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        // LIVE
        private bool live = true;
        private bool wmiOk;
        private Label liveLabel, cpuLabel, ramLabel, eventLabel;
        private Button pauseButton;
        private ManagementEventWatcher startWatcher, stopWatcher;
        private readonly List<RegistryWatcher> regWatchers = new List<RegistryWatcher>();
        private readonly List<FileSystemWatcher> fsWatchers = new List<FileSystemWatcher>();

        // Процессы
        private ListView procList;
        private readonly Dictionary<int, ListViewItem> procItems = new Dictionary<int, ListViewItem>();
        private readonly ProcSorter procSorter = new ProcSorter();

        // Автозагрузка / Winlogon
        private ListView startupList, winlogonList;
        private System.Windows.Forms.Timer startupDebounce, winlogonDebounce;
        private HashSet<string> startupPrev;
        private Dictionary<string, string> winlogonPrev;
        private readonly Dictionary<string, string> winlogonMarks = new Dictionary<string, string>();

        private sealed class StartupEntry
        {
            public string Hive = "", Key = "", Name = "", Command = "";
            public string Id => Hive + "|" + Key + "|" + Name + "|" + Command;
        }

        public MainForm()
        {
            Text = "SimpleUnlocker Remastered 5.4 LIVE";
            Width = 1280; Height = 820; MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(BuildLiveBar(), 0, 0);
            tabs.Dock = DockStyle.Fill; root.Controls.Add(tabs, 0, 1);
            Controls.Add(root);

            AddInfoTab("Обзор", "SimpleUnlocker Remastered 5.4 LIVE\n\nДиагностика Windows и инструменты восстановления.\nПроцессы, автозагрузка и Winlogon отслеживаются в реальном времени.\nПо умолчанию проверки только читают данные.\nОперации изменения системы требуют подтверждения и запуска от администратора.");
            AddProcesses(); AddStartup(); AddServices(); AddTasks(); AddRestrictions(); AddWinlogon();
            AddDrivers(); AddSystemInfo(); AddDisk(); AddNetwork(); AddSecurity(); AddRepair(); AddRestorePoint(); AddRegistryBackup(); AddEventLog(); AddExplorer();
            var logTab = new TabPage("Журнал"); logBox = new TextBox { Multiline=true, ReadOnly=true, Dock=DockStyle.Fill, ScrollBars=ScrollBars.Both, Font=new Font("Consolas",9) }; logTab.Controls.Add(logBox); tabs.TabPages.Add(logTab);
            ApplyTheme();
            PolishAllTabs();
            Log("Программа запущена. Администратор: " + IsAdmin);
            tabs.SelectedIndexChanged += (s, e) =>
            {
                if (live && procList != null && tabs.SelectedTab == procList.Parent)
                { liveTickCounter = 0; SyncProcesses(true); }
            };
            StartLive();
        }

        // ================= LIVE =================
        Control BuildLiveBar()
        {
            // Компактная верхняя панель в стиле современной системной утилиты.
            var header = new Panel
            {
                Dock = DockStyle.Fill,
                Height = 94,
                BackColor = Color.FromArgb(23, 30, 43),
                Padding = new Padding(18, 10, 18, 10)
            };

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.Transparent,
                Margin = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66f));
            header.Controls.Add(layout);

            var brand = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
            var title = new Label
            {
                Text = "SIMPLE UNLOCKER",
                AutoSize = true,
                Location = new Point(0, 8),
                Font = new Font("Segoe UI Semibold", 15f, FontStyle.Bold),
                ForeColor = Color.FromArgb(240, 245, 255)
            };
            var subtitle = new Label
            {
                Text = "REMАSTERED  •  WINDOWS DIAGNOSTICS",
                AutoSize = true,
                Location = new Point(2, 42),
                Font = new Font("Segoe UI", 8.2f, FontStyle.Regular),
                ForeColor = Color.FromArgb(137, 153, 178)
            };
            brand.Controls.Add(title);
            brand.Controls.Add(subtitle);
            layout.Controls.Add(brand, 0, 0);

            var stats = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoScroll = true,
                BackColor = Color.Transparent,
                Padding = new Padding(4, 15, 0, 0),
                Margin = Padding.Empty
            };

            var metricFont = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            liveLabel = new Label
            {
                AutoSize = true,
                Font = metricFont,
                Margin = new Padding(0, 7, 18, 0),
                Text = "● LIVE",
                ForeColor = Color.FromArgb(75, 205, 145)
            };
            cpuLabel = new Label
            {
                AutoSize = true,
                Font = metricFont,
                Margin = new Padding(0, 7, 18, 0),
                Text = "CPU: —",
                ForeColor = Color.FromArgb(213, 224, 240)
            };
            ramLabel = new Label
            {
                AutoSize = true,
                Font = metricFont,
                Margin = new Padding(0, 7, 18, 0),
                Text = "RAM: —",
                ForeColor = Color.FromArgb(213, 224, 240)
            };
            eventLabel = new Label
            {
                AutoSize = false,
                Width = 145,
                Height = 25,
                AutoEllipsis = true,
                Margin = new Padding(0, 8, 12, 0),
                ForeColor = Color.FromArgb(137, 153, 178),
                Text = "Событий пока нет"
            };
            pauseButton = new Button
            {
                AutoSize = true,
                Text = "Пауза LIVE",
                Padding = new Padding(13, 7, 13, 7),
                Margin = new Padding(0, 0, 0, 0),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(42, 57, 79),
                ForeColor = Color.FromArgb(235, 242, 252),
                Cursor = Cursors.Hand
            };
            pauseButton.FlatAppearance.BorderSize = 0;
            pauseButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(55, 91, 139);
            pauseButton.Click += (sender, e) => SetLive(!live);

            stats.Controls.AddRange(new Control[]
            {
                liveLabel, cpuLabel, ramLabel, eventLabel, pauseButton
            });
            layout.Controls.Add(stats, 1, 0);

            header.Paint += (sender, e) =>
            {
                using (var pen = new Pen(Color.FromArgb(48, 91, 145), 2f))
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
            };

            return header;
        }

        void StartLive()
        {
            // Более редкий опрос на слабых машинах снижает нагрузку без отключения функций.
            processTimer = new System.Windows.Forms.Timer { Interval = lowResourceMode ? 4000 : 2500 };
            processTimer.Tick += (s, e) => OnLiveTick();
            processTimer.Start();
            StartProcessEvents();
            StartRegistryWatchers();
            UpdateLiveLabel();
            OnLiveTick();
        }

        void StopLive()
        {
            try { processTimer?.Stop(); } catch { }
            try { startWatcher?.Stop(); startWatcher?.Dispose(); } catch { }
            try { stopWatcher?.Stop(); stopWatcher?.Dispose(); } catch { }
            foreach (var w in regWatchers) w.Dispose();
            foreach (var w in fsWatchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        }

        protected override void OnFormClosing(FormClosingEventArgs e) { StopLive(); base.OnFormClosing(e); }

        void UiPost(Action a) { try { if (IsHandleCreated && !IsDisposed && !Disposing) BeginInvoke(a); } catch { } }

        void SetLive(bool on)
        {
            live = on;
            processTimer.Enabled = on;
            pauseButton.Text = on ? "Пауза LIVE" : "Продолжить LIVE";
            Log("LIVE-мониторинг: " + (on ? "включён" : "на паузе"));
            if (on) { if (procList != null && tabs.SelectedTab == procList.Parent) SyncProcesses(true); RefreshStartup(); RefreshWinlogon(); OnLiveTick(); }
            UpdateLiveLabel();
        }

        void UpdateLiveLabel()
        {
            if (liveLabel == null) return;
            if (!live) { liveLabel.Text = "● ПАУЗА"; liveLabel.ForeColor = Color.Gray; return; }
            bool regOk = regWatchers.Count > 0 && regWatchers.All(w => w.Active);
            if (wmiOk && regOk) { liveLabel.Text = "● LIVE"; liveLabel.ForeColor = Color.FromArgb(0, 150, 60); }
            else { liveLabel.Text = "● LIVE (частично)"; liveLabel.ForeColor = Color.DarkOrange; }
        }

        void OnLiveTick()
        {
            if (!live || IsDisposed) return;
            double cpu = SystemLoad.CpuPercent();
            cpuLabel.Text = "CPU: " + cpu.ToString("F0") + "%";
            cpuLabel.ForeColor = cpu >= 85 ? Color.DarkRed : (cpu >= 50 ? Color.DarkOrange : Color.FromArgb(0, 120, 50));
            if (SystemLoad.Memory(out var used, out var total, out var pct))
            {
                ramLabel.Text = "RAM: " + used.ToString("F1") + " / " + total.ToString("F1") + " ГБ (" + pct + "%)";
                ramLabel.ForeColor = pct >= 90 ? Color.DarkRed : (pct >= 75 ? Color.DarkOrange : Color.FromArgb(0, 120, 50));
            }
            // Список процессов дорогой для слабых HDD/CPU: обновляем реже и только когда вкладка видна.
            if (procList != null && tabs.SelectedTab == procList.Parent)
            {
                liveTickCounter++;
                int refreshEvery = lowResourceMode ? 2 : 2;
                if (liveTickCounter >= refreshEvery)
                {
                    liveTickCounter = 0;
                    SyncProcesses(!wmiOk); // при недоступности WMI это резервный опрос
                }
            }
            UpdateLiveLabel();
        }

        void StartProcessEvents()
        {
            try
            {
                startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
                startWatcher.EventArrived += (s, e) =>
                {
                    int pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                    string name = Convert.ToString(e.NewEvent["ProcessName"]) ?? "";
                    UiPost(() => OnProcessStarted(pid, name));
                };
                stopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
                stopWatcher.EventArrived += (s, e) =>
                {
                    int pid = Convert.ToInt32(e.NewEvent["ProcessID"]);
                    string name = Convert.ToString(e.NewEvent["ProcessName"]) ?? "";
                    UiPost(() => OnProcessStopped(pid, name));
                };
                startWatcher.Start(); stopWatcher.Start();
                wmiOk = true;
                Log("WMI-события запуска/завершения процессов активны.");
            }
            catch (Exception ex)
            {
                wmiOk = false;
                Log("WMI-события недоступны (" + ex.Message + "). Включён резервный опрос раз в секунду.");
            }
        }

        void StartRegistryWatchers()
        {
            foreach (var key in StartupKeys)
            {
                regWatchers.Add(new RegistryWatcher(RegistryWatcher.HKCU, key, false, ScheduleStartupRefresh));
                regWatchers.Add(new RegistryWatcher(RegistryWatcher.HKLM, key, false, ScheduleStartupRefresh));
            }
            regWatchers.Add(new RegistryWatcher(RegistryWatcher.HKLM, WinlogonKey, true, ScheduleWinlogonRefresh));
            foreach (var dir in StartupFolders())
            {
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime };
                    FileSystemEventHandler h = (s, e) => ScheduleStartupRefresh();
                    w.Created += h; w.Deleted += h; w.Changed += h; w.Renamed += (s, e) => ScheduleStartupRefresh();
                    w.EnableRaisingEvents = true;
                    fsWatchers.Add(w);
                }
                catch (Exception ex) { Log("Не удалось следить за папкой " + dir + ": " + ex.Message); }
            }
        }

        void ScheduleStartupRefresh() => UiPost(() => { if (!live) return; startupDebounce.Stop(); startupDebounce.Start(); });
        void ScheduleWinlogonRefresh() => UiPost(() => { if (!live) return; winlogonDebounce.Stop(); winlogonDebounce.Start(); });

        static IEnumerable<string> StartupFolders()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
        }
        void ApplyTheme()
        {
            // Единая палитра: графитовый фон, приподнятые поверхности и синий акцент.
            Color background = Color.FromArgb(17, 22, 31);
            Color surface = Color.FromArgb(25, 32, 44);
            Color surfaceAlt = Color.FromArgb(34, 43, 59);
            Color foreground = Color.FromArgb(231, 237, 247);
            Color muted = Color.FromArgb(143, 158, 181);
            Color accent = Color.FromArgb(72, 145, 226);

            Font = new Font("Segoe UI", 9.5f);
            BackColor = background;
            ForeColor = foreground;

            tabs.Font = new Font("Segoe UI Semibold", 9f);
            tabs.BackColor = background;
            tabs.ForeColor = foreground;
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.SizeMode = TabSizeMode.Fixed;
            tabs.ItemSize = new Size(132, 40);
            tabs.Padding = new Point(14, 5);
            tabs.DrawItem -= DrawModernTab;
            tabs.DrawItem += DrawModernTab;

            StyleControls(this, background, surface, surfaceAlt, foreground, muted, accent);

            foreach (TabPage page in tabs.TabPages)
            {
                page.UseVisualStyleBackColor = false;
                page.BackColor = background;
                page.Padding = new Padding(8);
            }

            tabs.Invalidate();
        }

        // Единое оформление содержимого всех вкладок.
        // Не пересоздаёт элементы управления и не меняет логику существующих действий.
        void PolishAllTabs()
        {
            var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Обзор", "Состояние программы и основные инструменты диагностики Windows" },
                { "Процессы", "Текущие процессы, использование ресурсов и быстрые действия" },
                { "Автозагрузка", "Программы и команды, запускаемые вместе с Windows" },
                { "Службы", "Состояние системных служб и управление их запуском" },
                { "Планировщик", "Запланированные задачи Windows и их подробности" },
                { "Ограничения", "Проверка и настройка системных политик Windows" },
                { "Winlogon", "Критические параметры входа в систему и оболочки Windows" },
                { "Драйверы", "Установленные драйверы и устройства, к которым они относятся" },
                { "ПК", "Аппаратные характеристики компьютера и сведения об операционной системе" },
                { "Диски", "Локальные диски, файловые системы и свободное место" },
                { "Сеть", "Сетевые параметры, адреса и активные соединения" },
                { "Безопасность", "Состояние Microsoft Defender и брандмауэра Windows" },
                { "Восстановление", "Средства проверки и восстановления системных компонентов" },
                { "Точка восстановления", "Создание точки восстановления и запуск мастера Windows" },
                { "Резерв реестра", "Экспорт важных разделов реестра перед изменениями" },
                { "Журнал событий", "Просмотр последних системных ошибок и событий Windows" },
                { "Проводник", "Навигация по файлам, папкам и дискам компьютера" },
                { "Журнал", "История действий и сообщений самой программы" }
            };

            foreach (TabPage page in tabs.TabPages)
            {
                page.SuspendLayout();
                page.BackColor = Color.FromArgb(17, 22, 31);
                page.Padding = new Padding(12, 8, 12, 12);

                // Удобная верхняя шапка с названием и коротким пояснением.
                var header = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 58,
                    BackColor = Color.FromArgb(23, 31, 45),
                    Padding = new Padding(14, 7, 14, 5),
                    Margin = new Padding(0, 0, 0, 8)
                };
                var title = new Label
                {
                    Text = page.Text,
                    Dock = DockStyle.Top,
                    Height = 25,
                    ForeColor = Color.FromArgb(240, 245, 255),
                    Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold),
                    AutoEllipsis = true
                };
                string description;
                if (!descriptions.TryGetValue(page.Text, out description))
                    description = "Инструменты и сведения Windows";
                var subtitle = new Label
                {
                    Text = description,
                    Dock = DockStyle.Fill,
                    ForeColor = Color.FromArgb(143, 158, 181),
                    Font = new Font("Segoe UI", 8.5f),
                    AutoEllipsis = true
                };
                header.Controls.Add(subtitle);
                header.Controls.Add(title);
                page.Controls.Add(header);
                header.BringToFront();

                PolishControlsRecursive(page);
                title.ForeColor = Color.FromArgb(240, 245, 255);
                title.Font = new Font("Segoe UI Semibold", 12f, FontStyle.Bold);
                subtitle.ForeColor = Color.FromArgb(143, 158, 181);
                page.ResumeLayout(true);
            }
        }

        void PolishControlsRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is FlowLayoutPanel flow)
                {
                    flow.Padding = new Padding(2, 7, 2, 7);
                    flow.WrapContents = true;
                    flow.AutoScroll = true;
                    flow.BackColor = Color.FromArgb(17, 22, 31);
                }
                else if (c is ListView list)
                {
                    list.BackColor = Color.FromArgb(25, 32, 44);
                    list.ForeColor = Color.FromArgb(231, 237, 247);
                    list.BorderStyle = BorderStyle.None;
                    list.Font = new Font("Segoe UI", 9.2f);
                    list.GridLines = false;
                    list.FullRowSelect = true;
                    list.HideSelection = false;
                    list.OwnerDraw = true;
                    list.DrawColumnHeader -= DrawModernColumnHeader;
                    list.DrawColumnHeader += DrawModernColumnHeader;
                    list.DrawItem -= DrawDefaultListItem;
                    list.DrawItem += DrawDefaultListItem;
                    list.DrawSubItem -= DrawDefaultListSubItem;
                    list.DrawSubItem += DrawDefaultListSubItem;
                    foreach (ColumnHeader col in list.Columns)
                        if (col.Width < 110) col.Width = 130;
                }
                else if (c is TextBox textBox)
                {
                    textBox.BackColor = Color.FromArgb(25, 32, 44);
                    textBox.ForeColor = Color.FromArgb(220, 229, 242);
                    textBox.BorderStyle = BorderStyle.FixedSingle;
                    textBox.Font = new Font(textBox.Multiline ? "Consolas" : "Segoe UI", textBox.Multiline ? 9f : 9.5f);
                    if (textBox.Multiline)
                    {
                        textBox.WordWrap = false;
                        textBox.ScrollBars = ScrollBars.Both;
                    }
                }
                else if (c is Button button)
                {
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderSize = 1;
                    button.FlatAppearance.BorderColor = Color.FromArgb(54, 70, 92);
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(54, 105, 164);
                    button.BackColor = Color.FromArgb(34, 45, 62);
                    button.ForeColor = Color.FromArgb(235, 242, 252);
                    button.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
                    button.MinimumSize = new Size(0, 34);
                    button.Padding = new Padding(10, 5, 10, 5);
                    button.Margin = new Padding(4, 3, 6, 3);
                    button.Cursor = Cursors.Hand;
                    button.UseVisualStyleBackColor = false;
                }
                else if (c is Label label && !(c.Parent is Panel && label.Dock == DockStyle.Fill))
                {
                    label.ForeColor = Color.FromArgb(220, 229, 242);
                }

                if (c.HasChildren) PolishControlsRecursive(c);
            }
        }

        void DrawModernColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var brush = new SolidBrush(Color.FromArgb(34, 45, 62)))
                e.Graphics.FillRectangle(brush, e.Bounds);
            using (var pen = new Pen(Color.FromArgb(48, 64, 85)))
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header.Text,
                new Font("Segoe UI Semibold", 8.8f, FontStyle.Bold),
                Rectangle.Inflate(e.Bounds, -9, -2), Color.FromArgb(157, 190, 231),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        void DrawDefaultListItem(object sender, DrawListViewItemEventArgs e)
        {
            // Рисование подэлементов выполняется отдельно, чтобы сохранить цветовую индикацию строк.
            e.DrawDefault = true;
        }

        void DrawDefaultListSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        void DrawModernTab(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= tabs.TabPages.Count)
                return;

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Rectangle bounds = e.Bounds;
            Color back = selected
                ? Color.FromArgb(34, 48, 68)
                : Color.FromArgb(21, 27, 38);
            Color fore = selected
                ? Color.FromArgb(244, 248, 255)
                : Color.FromArgb(151, 165, 187);

            using (var brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, bounds);

            if (selected)
            {
                using (var brush = new SolidBrush(Color.FromArgb(72, 145, 226)))
                    e.Graphics.FillRectangle(brush, bounds.Left + 12, bounds.Bottom - 3,
                        Math.Max(18, bounds.Width - 24), 3);
            }

            string text = tabs.TabPages[e.Index].Text;
            TextRenderer.DrawText(
                e.Graphics,
                text,
                new Font("Segoe UI Semibold", 9f, FontStyle.Bold),
                Rectangle.Inflate(bounds, -5, -2),
                fore,
                TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis |
                TextFormatFlags.SingleLine);
        }

        void StyleControls(Control parent, Color background, Color surface, Color surfaceAlt,
            Color foreground, Color muted, Color accent)
        {
            foreach (Control c in parent.Controls)
            {
                if (c is TabControl)
                {
                    c.BackColor = background;
                    c.ForeColor = foreground;
                }
                else if (c is TabPage)
                {
                    c.BackColor = background;
                    c.ForeColor = foreground;
                }
                else if (c is ListView lv)
                {
                    lv.BackColor = surface;
                    lv.ForeColor = foreground;
                    lv.BorderStyle = BorderStyle.None;
                    lv.Font = new Font("Segoe UI", 9f);
                    lv.GridLines = false;
                    lv.HideSelection = false;
                }
                else if (c is TreeView tree)
                {
                    tree.BackColor = surface;
                    tree.ForeColor = foreground;
                    tree.BorderStyle = BorderStyle.None;
                    tree.LineColor = Color.FromArgb(76, 91, 112);
                    tree.Font = new Font("Segoe UI", 9f);
                    tree.HideSelection = false;
                }
                else if (c is TextBox tb)
                {
                    tb.BackColor = surface;
                    tb.ForeColor = foreground;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                    tb.Font = new Font("Segoe UI", 9.5f);
                }
                else if (c is RichTextBox rtb)
                {
                    rtb.BackColor = surface;
                    rtb.ForeColor = foreground;
                    rtb.BorderStyle = BorderStyle.None;
                    rtb.Font = new Font("Segoe UI", 9.5f);
                }
                else if (c is ComboBox combo)
                {
                    combo.BackColor = surfaceAlt;
                    combo.ForeColor = foreground;
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.Font = new Font("Segoe UI", 9f);
                }
                else if (c is Button b)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderSize = 0;
                    b.FlatAppearance.MouseOverBackColor = accent;
                    b.BackColor = surfaceAlt;
                    b.ForeColor = foreground;
                    b.Font = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
                    b.Padding = new Padding(11, 6, 11, 6);
                    b.Margin = new Padding(4, 4, 4, 4);
                    b.Cursor = Cursors.Hand;
                    b.UseVisualStyleBackColor = false;
                }
                else if (c is CheckBox || c is RadioButton)
                {
                    c.ForeColor = foreground;
                    c.BackColor = Color.Transparent;
                    c.Font = new Font("Segoe UI", 9f);
                }
                else if (c is Label label)
                {
                    label.ForeColor = foreground;
                    if (label.Font.Size >= 12)
                        label.Font = new Font("Segoe UI Semibold", label.Font.Size, FontStyle.Bold);
                }
                else if (c is Panel || c is FlowLayoutPanel ||
                         c is TableLayoutPanel || c is SplitContainer)
                {
                    c.BackColor = background;
                    c.ForeColor = foreground;
                }
                else
                {
                    c.BackColor = background;
                    c.ForeColor = foreground;
                }

                if (c.HasChildren)
                    StyleControls(c, background, surface, surfaceAlt, foreground, muted, accent);
            }
        }
        string CollectReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("SimpleUnlocker Remastered — диагностический отчёт");
            sb.AppendLine("Дата: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Администратор: " + IsAdmin);
            sb.AppendLine("\r\n=== SYSTEMINFO ===\r\n" + Cmd("systeminfo",30000));
            sb.AppendLine("\r\n=== PROCESS LIST ===\r\n" + Cmd("tasklist /v",30000));
            sb.AppendLine("\r\n=== SERVICES ===\r\n" + Cmd("sc query type= service state= all",30000));
            sb.AppendLine("\r\n=== STARTUP ===\r\n" + Cmd("wmic startup get caption,command,location",30000));
            sb.AppendLine("\r\n=== NETWORK ===\r\n" + Cmd("ipconfig /all",20000));
            sb.AppendLine("\r\n=== DISKS ===\r\n" + Cmd("wmic logicaldisk get caption,size,freespace",20000));
            sb.AppendLine("\r\n=== DEFENDER ===\r\n" + Cmd("powershell -NoProfile -Command \"Get-MpComputerStatus | Format-List\"",30000));
            return sb.ToString();
        }
        void AddInfoTab(string title, string text) { var p=new TabPage(title); p.Controls.Add(new Label{Text=text,Dock=DockStyle.Fill,Padding=new Padding(20),Font=new Font("Segoe UI",12)}); tabs.TabPages.Add(p); }
        TabPage NewTab(string title) { var p=new TabPage(title); tabs.TabPages.Add(p); return p; }
        void Log(string s) { if(logBox!=null) logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {s}\r\n"); }
        void AddButton(FlowLayoutPanel f,string text,Action a) { var b=new Button{Text=text,AutoSize=true,Padding=new Padding(8)}; b.Click+=(s,e)=>{try{a();}catch(Exception ex){Log(ex.Message);MessageBox.Show(ex.Message,"Ошибка");}}; f.Controls.Add(b); }
        ListView List(TabPage p, string[] cols) { var l=new ListView{Dock=DockStyle.Fill,View=View.Details,FullRowSelect=true,GridLines=true}; foreach(var c in cols)l.Columns.Add(c, c=="Путь"||c=="Команда"||c=="Описание"?360:150); p.Controls.Add(l); return l; }
        void Run(string file,string args,bool admin=false) { var psi=new ProcessStartInfo(file,args){UseShellExecute=true,Verb=admin?"runas":""}; Process.Start(psi); Log("Запуск: "+file+" "+args); }
        // cmd.exe and most legacy Windows utilities write in the active OEM code page.
        // Decode both streams explicitly; .NET's default UTF-8 decoder caused the mojibake
        // visible in the system/driver tabs on Russian Windows installations.
        string Cmd(string command, int timeout = 20000)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int oemCodePage = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            Encoding outputEncoding;
            try { outputEncoding = Encoding.GetEncoding(oemCodePage); }
            catch { outputEncoding = Encoding.Default; }

            var psi = new ProcessStartInfo("cmd.exe", "/d /c " + command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = outputEncoding,
                StandardErrorEncoding = outputEncoding,
                CreateNoWindow = true
            };
            using var pr = Process.Start(psi);
            if (pr == null) return "Не удалось запустить cmd.exe.";
            var stdout = pr.StandardOutput.ReadToEndAsync();
            var stderr = pr.StandardError.ReadToEndAsync();
            if (!pr.WaitForExit(timeout))
            {
                try { pr.Kill(true); } catch { }
                return "Команда прервана: превышено время ожидания (" + timeout + " мс).";
            }
            Task.WaitAll(stdout, stderr);
            string err = stderr.Result;
            return stdout.Result + (string.IsNullOrWhiteSpace(err) ? "" : Environment.NewLine + err);
        }
        bool Confirm(string msg)=>MessageBox.Show(msg,"Подтверждение",MessageBoxButtons.YesNo,MessageBoxIcon.Warning)==DialogResult.Yes;

        // ================= ПРОЦЕССЫ =================
        void AddProcesses(){
            var p=NewTab("Процессы");
            var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);
            var l=List(p,new[]{"PID","Имя","CPU %","Память МБ","Путь"});
            l.Columns[0].Width=75;l.Columns[1].Width=190;l.Columns[2].Width=80;l.Columns[3].Width=105;l.Columns[4].Width=500;
            l.BringToFront();f.BringToFront();
            procList=l;
            l.ColumnClick+=(s,e)=>{
                if(e.Column==procSorter.Column)procSorter.Descending=!procSorter.Descending;
                else{procSorter.Column=e.Column;procSorter.Descending=(e.Column==2||e.Column==3);}
                SortProcs();
            };
            AddButton(f,"Обновить вручную",()=>SyncProcesses(true));
            AddButton(f,"Открыть расположение",()=>{if(l.SelectedItems.Count>0){var path=l.SelectedItems[0].SubItems[4].Text;if(File.Exists(path))Run("explorer.exe","/select,\""+path+"\"");}});
            AddButton(f,"Завершить процесс",()=>{if(l.SelectedItems.Count>0&&Confirm("Принудительно завершить выбранный процесс?")){var pid=l.SelectedItems[0].Text;Process.GetProcessById(int.Parse(pid)).Kill();Log("Завершён PID "+pid);}});
        }

        void SortProcs(){
            if(procList==null)return;
            procList.ListViewItemSorter=procSorter;procList.Sort();procList.ListViewItemSorter=null;
        }

        ListViewItem CreateProcItem(Process x){
            string path="";try{path=x.MainModule?.FileName??"";}catch{}
            var it=new ListViewItem(x.Id.ToString()){Tag=new ProcState()};
            it.SubItems.Add(x.ProcessName);it.SubItems.Add("0.0");it.SubItems.Add("0.0");it.SubItems.Add(path);
            procItems[x.Id]=it;procList.Items.Add(it);
            return it;
        }

        void UpdateProcItem(ListViewItem it,Process x,DateTime now){
            var st=(ProcState)it.Tag!;
            st.MemMb=SafeMemory(x)/1048576.0;
            try{
                var cpu=x.TotalProcessorTime;
                if(st.LastTime!=default){
                    double dt=(now-st.LastTime).TotalSeconds;
                    if(dt>0)st.Cpu=Math.Clamp((cpu-st.LastCpu).TotalSeconds/(dt*Environment.ProcessorCount)*100.0,0,100);
                }
                st.LastCpu=cpu;st.LastTime=now;
            }catch{st.Cpu=0;}

            string cs=st.Cpu.ToString("F1"),ms=st.MemMb.ToString("F1");
            if(it.SubItems[2].Text!=cs)it.SubItems[2].Text=cs;
            if(it.SubItems[3].Text!=ms)it.SubItems[3].Text=ms;

            // Цвет показывает наибольшую из нагрузок CPU и RAM.
            double ramPercent=0;
            try{
                long total=GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                if(total>0)ramPercent=st.MemMb*1048576.0/total*100.0;
            }catch{}
            double load=Math.Max(st.Cpu,ramPercent);
            bool system=IsLikelySystem(x.ProcessName);
            Color back,fore;
            if(system){back=Color.FromArgb(218,245,225);fore=Color.FromArgb(20,105,48);}
            else if(load>=50){back=Color.FromArgb(255,215,215);fore=Color.FromArgb(150,25,25);}
            else if(load>=20){back=Color.FromArgb(255,229,204);fore=Color.FromArgb(150,75,15);}
            else if(load>=5){back=Color.FromArgb(255,247,204);fore=Color.FromArgb(115,90,10);}
            else{back=Color.FromArgb(235,242,250);fore=Color.FromArgb(35,48,65);}
            if(it.BackColor!=back)it.BackColor=back;
            if(it.ForeColor!=fore)it.ForeColor=fore;
        }

        void RemoveProc(int pid){
            if(procItems.TryGetValue(pid,out var it)){procItems.Remove(pid);procList.Items.Remove(it);}
        }

        // Полная сверка + обновление CPU/RAM. addMissing=true добавляет процессы, пропущенные событиями.
        void SyncProcesses(bool addMissing){
            if(procList==null||IsDisposed)return;
            var now=DateTime.UtcNow;
            var procs=Process.GetProcesses();
            var alive=new HashSet<int>();
            procList.BeginUpdate();
            try{
                foreach(var x in procs){
                    try{
                        alive.Add(x.Id);
                        if(!procItems.TryGetValue(x.Id,out var it)){if(!addMissing)continue;it=CreateProcItem(x);}
                        UpdateProcItem(it,x,now);
                    }catch{}
                    finally{x.Dispose();}
                }
                foreach(var pid in procItems.Keys.Where(k=>!alive.Contains(k)).ToList())RemoveProc(pid);
                // Не пересортировываем список на каждом LIVE-тике: это вызывает скачки строк и мерцание.
            }finally{procList.EndUpdate();}
        }

        void OnProcessStarted(int pid,string name){
            if(!live||procList==null||tabs.SelectedTab != procList.Parent)return;
            eventLabel.Text="＋ "+name+" (PID "+pid+") "+DateTime.Now.ToString("HH:mm:ss");
            try{
                using(var x=Process.GetProcessById(pid)){
                    if(procItems.ContainsKey(pid))return;
                    var it=CreateProcItem(x);UpdateProcItem(it,x,DateTime.UtcNow);
                }
                procList.BeginUpdate();try{SortProcs();}finally{procList.EndUpdate();}
            }catch{}
        }

        void OnProcessStopped(int pid,string name){
            if(!live||procList==null||tabs.SelectedTab != procList.Parent)return;
            eventLabel.Text="－ "+name+" (PID "+pid+") "+DateTime.Now.ToString("HH:mm:ss");
            RemoveProc(pid);
        }
        long SafeMemory(Process p){try{return p.WorkingSet64;}catch{return 0;}}
        bool IsLikelySystem(string name){return new[]{"System","Idle","svchost","csrss","wininit","winlogon","services","lsass","smss","explorer"}.Contains(name,StringComparer.OrdinalIgnoreCase);}
        // ================= АВТОЗАГРУЗКА =================
        List<StartupEntry> ReadStartup(){
            var res=new List<StartupEntry>();
            foreach(var pair in new[]{(Registry.CurrentUser,"HKCU"),(Registry.LocalMachine,"HKLM")})
                foreach(var key in StartupKeys)
                    try{
                        using(var k=pair.Item1.OpenSubKey(key)){
                            if(k==null)continue;
                            foreach(var n in k.GetValueNames())res.Add(new StartupEntry{Hive=pair.Item2,Key=key,Name=n,Command=Convert.ToString(k.GetValue(n))??""});
                        }
                    }catch{}
            foreach(var dir in StartupFolders())
                try{
                    if(!Directory.Exists(dir))continue;
                    foreach(var file in Directory.GetFiles(dir)){
                        var fn=Path.GetFileName(file);
                        if(fn.Equals("desktop.ini",StringComparison.OrdinalIgnoreCase))continue;
                        res.Add(new StartupEntry{Hive="FOLDER",Key=dir,Name=fn,Command=file});
                    }
                }catch{}
            return res;
        }

        static bool IsSuspiciousStartup(StartupEntry e){
            string c=e.Command.ToLowerInvariant();
            if(e.Hive=="FOLDER"){string ext=Path.GetExtension(c);return new[]{".vbs",".js",".ps1",".cmd",".bat",".wsf"}.Contains(ext);}
            return c.Contains("\\temp\\")||c.Contains("\\appdata\\")||c.Contains("powershell -")||c.Contains("wscript");
        }

        void RefreshStartup(){
            if(startupList==null)return;
            var entries=ReadStartup();
            var ids=new HashSet<string>(entries.Select(x=>x.Id));
            if(startupPrev!=null){
                foreach(var e in entries)if(!startupPrev.Contains(e.Id))Log("Автозагрузка: добавлено ["+e.Hive+"] "+e.Name+" → "+e.Command);
                foreach(var id in startupPrev)if(!ids.Contains(id))Log("Автозагрузка: удалено "+id);
            }
            startupPrev=ids;
            var selected=startupList.SelectedItems.Count>0?startupList.SelectedItems[0].Tag as string:null;
            startupList.BeginUpdate();
            try{
                var existing=startupList.Items.Cast<ListViewItem>()
                    .Where(i=>i.Tag is string).ToDictionary(i=>(string)i.Tag,StringComparer.Ordinal);
                var wanted=new HashSet<string>(entries.Select(e=>e.Id),StringComparer.Ordinal);
                foreach(var old in existing)
                    if(!wanted.Contains(old.Key))startupList.Items.Remove(old.Value);

                foreach(var e in entries){
                    ListViewItem it;
                    if(!existing.TryGetValue(e.Id,out it)){
                        it=new ListViewItem(e.Hive){Tag=e.Id};
                        it.SubItems.Add(e.Key);it.SubItems.Add(e.Name);it.SubItems.Add(e.Command);
                        startupList.Items.Add(it);
                    }
                    // Зелёный — штатная системная запись, жёлтый — обычная,
                    // оранжевый — неизвестная/нестандартная, красный — подозрительная.
                    bool sus=IsSuspiciousStartup(e);
                    bool system=e.Hive!="FOLDER" &&
                        (e.Key.IndexOf("Windows",StringComparison.OrdinalIgnoreCase)>=0 ||
                         e.Command.IndexOf(@"\Windows\",StringComparison.OrdinalIgnoreCase)>=0 ||
                         e.Command.IndexOf("System32",StringComparison.OrdinalIgnoreCase)>=0);
                    Color back=sus?Color.FromArgb(255,215,215):
                        system?Color.FromArgb(218,245,225):
                        (e.Hive=="FOLDER"?Color.FromArgb(255,235,205):Color.FromArgb(255,247,204));
                    Color fore=sus?Color.DarkRed:system?Color.DarkGreen:
                        (e.Hive=="FOLDER"?Color.DarkOrange:Color.FromArgb(115,90,10));
                    if(it.BackColor!=back)it.BackColor=back;
                    if(it.ForeColor!=fore)it.ForeColor=fore;
                    if(selected==e.Id)it.Selected=true;
                }
            }finally{startupList.EndUpdate();}
        }

        void AddStartup(){
            var p=NewTab("Автозагрузка");
            var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);
            var l=List(p,new[]{"Hive","Раздел","Имя","Команда"});
            l.Columns[0].Width=80;l.Columns[1].Width=330;l.Columns[2].Width=180;l.Columns[3].Width=450;
            l.BringToFront();f.BringToFront();startupList=l;
            startupDebounce=new System.Windows.Forms.Timer{Interval=400};
            startupDebounce.Tick+=(s,e)=>{startupDebounce.Stop();RefreshStartup();};
            AddButton(f,"Обновить вручную",RefreshStartup);
            AddButton(f,"Удалить запись",()=>{
                if(l.SelectedItems.Count==0||!Confirm("Удалить выбранную запись автозагрузки?"))return;
                var i=l.SelectedItems[0];
                if(i.Text=="FOLDER"){File.Delete(i.SubItems[3].Text);}
                else{
                    var root=i.Text=="HKCU"?Registry.CurrentUser:Registry.LocalMachine;
                    using(var k=root.OpenSubKey(i.SubItems[1].Text,true)){k?.DeleteValue(i.SubItems[2].Text,false);}
                }
                Log("Удалена запись автозагрузки: "+i.SubItems[2].Text);RefreshStartup();
            });
            AddButton(f,"Открыть папку Startup",()=>Run("explorer.exe","shell:startup"));
            RefreshStartup();
        }
        void AddServices(){var p=NewTab("Службы");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var l=List(p,new[]{"Имя","Статус","Тип запуска","Описание"});l.BringToFront();f.BringToFront();Action refresh=()=>{l.Items.Clear();foreach(var s in System.ServiceProcess.ServiceController.GetServices().OrderBy(x=>x.ServiceName)){using(s){string status=s.Status.ToString(),start="",desc="";try{using(var k=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+s.ServiceName)){start=Convert.ToString(k?.GetValue("Start"));desc=Convert.ToString(k?.GetValue("Description"));}}catch{}var it=new ListViewItem(s.ServiceName);it.SubItems.Add(status);it.SubItems.Add(start);it.SubItems.Add(desc);l.Items.Add(it);}}};AddButton(f,"Обновить",refresh);AddButton(f,"Открыть services.msc",()=>Run("services.msc",""));AddButton(f,"Остановить службу",()=>{if(l.SelectedItems.Count>0&&Confirm("Остановить службу? Это может нарушить работу Windows."))Run("sc.exe","stop "+l.SelectedItems[0].Text,true);});AddButton(f,"Запустить службу",()=>{if(l.SelectedItems.Count>0&&Confirm("Запустить службу?"))Run("sc.exe","start "+l.SelectedItems[0].Text,true);});}
        void AddTasks(){var p=NewTab("Планировщик");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var t=new TextBox{Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Both,Font=new Font("Consolas",9)};p.Controls.Add(t);t.BringToFront();f.BringToFront();AddButton(f,"Список задач",()=>{t.Text=Cmd("schtasks /query /fo LIST /v",30000);Log("Просмотр задач");});AddButton(f,"Открыть планировщик",()=>Run("taskschd.msc",""));}
        // ================= RESTRICTIONS =================
        private sealed class Restr
        {
            public bool Checked;
            public string Kind = "policy"; // policy | ifeo | usb
            public RegistryHive Hive;
            public string HiveName = "";
            public string Path = "";
            public string Name = "";
            public string Value = "";
            public string Desc = "";
            public string FullKey => HiveName + "\\" + Path;
        }

        private sealed class RegTarget
        {
            public RegistryHive Hive;
            public string HiveName;
            public string Path;
            public bool InfoOnly;

            public RegTarget(RegistryHive hive, string hiveName, string path, bool infoOnly)
            {
                Hive = hive;
                HiveName = hiveName;
                Path = path;
                InfoOnly = infoOnly;
            }
        }

        private static readonly Dictionary<string, string> KnownRestrictions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "DisableTaskMgr", "Диспетчер задач отключён" },
            { "DisableRegistryTools", "Редактор реестра отключён" },
            { "DisableCMD", "Командная строка отключена" },
            { "DisableLockWorkstation", "Блокировка компьютера отключена" },
            { "DisableChangePassword", "Смена пароля отключена" },
            { "NoControlPanel", "Панель управления и «Параметры» скрыты" },
            { "NoDispCPL", "Свойства экрана отключены" },
            { "NoDispBackgroundPage", "Вкладка «Фон рабочего стола» скрыта" },
            { "NoDispAppearancePage", "Вкладка «Оформление» скрыта" },
            { "NoDispScrSavPage", "Вкладка «Заставка» скрыта" },
            { "NoDispSettingsPage", "Вкладка «Параметры экрана» скрыта" },
            { "NoDrives", "Диски скрыты в Проводнике" },
            { "NoViewOnDrive", "Доступ к дискам запрещён" },
            { "NoRun", "Команда «Выполнить» убрана" },
            { "NoFind", "Поиск убран из меню «Пуск»" },
            { "NoFolderOptions", "Параметры папок недоступны" },
            { "NoClose", "Завершение работы убрано из «Пуск»" },
            { "NoLogoff", "Выход из системы убран" },
            { "NoDesktop", "Значки рабочего стола скрыты" },
            { "NoSetFolders", "Настройка папок меню «Пуск» заблокирована" },
            { "NoSetTaskbar", "Настройка панели задач заблокирована" },
            { "NoChangeStartMenu", "Изменение меню «Пуск» запрещено" },
            { "NoTrayContextMenu", "Меню панели задач отключено" },
            { "NoViewContextMenu", "Контекстное меню Проводника отключено" },
            { "NoWinKeys", "Сочетания с клавишей Win отключены" },
            { "NoPropertiesMyComputer", "Свойства «Компьютера» недоступны" },
            { "NoManageMyComputerVerb", "Пункт «Управление» убран" },
            { "NoNetConnectDisconnect", "Подключение сетевых дисков запрещено" },
            { "NoSecurityTab", "Вкладка «Безопасность» в свойствах скрыта" },
            { "DisallowRun", "Запрещён запуск перечисленных программ" },
            { "RestrictRun", "Разрешён запуск только перечисленных программ" },
            { "DisableAntiSpyware", "Защитник Windows отключён политикой" },
            { "DisableAntiVirus", "Антивирус Защитника отключён политикой" }
        };

        private const string IfeoPath =
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        private const string IfeoPath32 =
            @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        private const string UsbStorPath =
            @"SYSTEM\CurrentControlSet\Services\USBSTOR";

        private ListView restrList;
        private Label restrInfo;
        private bool restrShowAllValues;

        private static string BackupDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimpleUnlocker", "Backups");

        private static RegistryView RestrictionRegistryView =>
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32;

        private static RegistryKey OpenRestrictionBase(RegistryHive hive) =>
            RegistryKey.OpenBaseKey(hive, RestrictionRegistryView);

        private static bool IsRestrictionEnabled(string name, string value)
        {
            if (int.TryParse(value, out int number))
                return number != 0;

            return name.Equals("DisallowRun", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RestrictRun", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<RegTarget> PolicyTargets()
        {
            foreach (var hive in new[]
            {
                new KeyValuePair<RegistryHive, string>(RegistryHive.CurrentUser, "HKCU"),
                new KeyValuePair<RegistryHive, string>(RegistryHive.LocalMachine, "HKLM")
            })
            {
                yield return new RegTarget(hive.Key, hive.Value,
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\System", false);
                yield return new RegTarget(hive.Key, hive.Value,
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", false);
                yield return new RegTarget(hive.Key, hive.Value,
                    @"Software\Policies\Microsoft\Windows\System", false);
                yield return new RegTarget(hive.Key, hive.Value,
                    @"Software\Microsoft\Windows\CurrentVersion\Policies\Windows", true);
            }

            yield return new RegTarget(RegistryHive.LocalMachine, "HKLM",
                @"Software\Policies\Microsoft\Windows Defender", false);
        }

        void AddRestrictions()
        {
            var page = NewTab("Ограничения");
            var bar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                WrapContents = true,
                Padding = new Padding(4)
            };

            page.Controls.Add(bar);
            var list = List(page, new[] { "", "Источник", "Параметр", "Значение", "Описание" });
            list.CheckBoxes = true;
            list.Columns[0].Width = 42;
            list.Columns[1].Width = 340;
            list.Columns[2].Width = 190;
            list.Columns[3].Width = 190;
            list.Columns[4].Width = 430;
            list.BringToFront();
            bar.BringToFront();

            restrList = list;
            list.ItemChecked += (sender, e) =>
            {
                var restriction = e.Item.Tag as Restr;
                if (restriction != null)
                    restriction.Checked = e.Item.Checked;
                UpdateRestrInfo();
            };

            AddButton(bar, "Сканировать", ScanRestrictions);
            AddButton(bar, "Выбрать все", () =>
            {
                foreach (ListViewItem item in list.Items)
                    if (item.Tag is Restr) item.Checked = true;
            });
            AddButton(bar, "Снять выбор", () =>
            {
                foreach (ListViewItem item in list.Items)
                    item.Checked = false;
            });
            AddButton(bar, "Снять выбранные ограничения", RemoveSelectedRestrictions);

            AddButton(bar, "Показать все значения политик", () =>
            {
                restrShowAllValues = !restrShowAllValues;
                ScanRestrictions();
            });

            AddButton(bar, "Резервная копия .reg", ExportPoliciesBackup);
            AddButton(bar, "Восстановить из .reg", ImportPoliciesBackup);
            AddButton(bar, "Экспорт отчёта", ExportRestrictionsReport);
            AddButton(bar, "Групповые политики", () => Run("gpedit.msc", ""));
            AddButton(bar, "Редактор реестра", () => Run("regedit.exe", ""));

            restrInfo = new Label
            {
                AutoSize = true,
                ForeColor = Color.Gray,
                Margin = new Padding(5, 9, 0, 0),
                Text = "Нажмите «Сканировать»"
            };
            bar.Controls.Add(restrInfo);

            ScanRestrictions();
        }

        private void UpdateRestrInfo()
        {
            if (restrInfo == null || restrList == null) return;

            int total = 0;
            int selected = 0;

            foreach (ListViewItem item in restrList.Items)
            {
                if (!(item.Tag is Restr)) continue;
                total++;
                if (item.Checked) selected++;
            }

            restrInfo.Text = "Найдено ограничений: " + total + "   ·   выбрано: " + selected;
            restrInfo.ForeColor = total == 0 ? Color.SeaGreen :
                selected > 0 ? Color.DodgerBlue : Color.Gray;
        }

        private ListViewItem MakeRestrItem(Restr restriction, bool dangerous)
        {
            var item = new ListViewItem("");
            item.Tag = restriction;
            item.SubItems.Add(restriction.HiveName + "\\" + restriction.Path);
            item.SubItems.Add(restriction.Name);
            item.SubItems.Add(restriction.Value);
            item.SubItems.Add(restriction.Desc);

            if (dangerous)
            {
                item.BackColor = Color.FromArgb(255, 225, 225);
                item.ForeColor = Color.DarkRed;
            }

            return item;
        }

        private void ScanRestrictions()
        {
            if (restrList == null) return;

            var list = restrList;
            list.BeginUpdate();

            try
            {
                list.Items.Clear();

                foreach (var target in PolicyTargets())
                {
                    try
                    {
                        using (var baseKey = OpenRestrictionBase(target.Hive))
                        using (var key = baseKey.OpenSubKey(target.Path))
                        {
                            if (key == null) continue;

                            foreach (string name in key.GetValueNames())
                            {
                                string value = FmtReg(key.GetValue(name));
                                bool known = KnownRestrictions.TryGetValue(name, out string description);
                                bool active = known && IsRestrictionEnabled(name, value);

                                if (active)
                                {
                                    var restriction = new Restr
                                    {
                                        Kind = "policy",
                                        Hive = target.Hive,
                                        HiveName = target.HiveName,
                                        Path = target.Path,
                                        Name = name,
                                        Value = value,
                                        Desc = description
                                    };
                                    list.Items.Add(MakeRestrItem(restriction, false));
                                }
                                else if (restrShowAllValues)
                                {
                                    var item = new ListViewItem("");
                                    item.SubItems.Add(target.HiveName + "\\" + target.Path);
                                    item.SubItems.Add(name);
                                    item.SubItems.Add(value);
                                    item.SubItems.Add(known
                                        ? "Ограничение не активно"
                                        : "Прочий параметр политики (не изменяется)");
                                    item.ForeColor = Color.Gray;
                                    list.Items.Add(item);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("Сканирование политик: " + ex.Message);
                    }
                }

                var ifeoPaths = Environment.Is64BitOperatingSystem
                    ? new[] { IfeoPath, IfeoPath32 }
                    : new[] { IfeoPath };

                foreach (string ifeoPath in ifeoPaths)
                {
                    try
                    {
                        using (var baseKey = OpenRestrictionBase(RegistryHive.LocalMachine))
                        using (var root = baseKey.OpenSubKey(ifeoPath))
                        {
                            if (root == null) continue;

                            foreach (string subKeyName in root.GetSubKeyNames())
                            {
                                using (var subKey = root.OpenSubKey(subKeyName))
                                {
                                    string debugger = Convert.ToString(
                                        subKey == null ? null : subKey.GetValue("Debugger")) ?? "";

                                    if (string.IsNullOrWhiteSpace(debugger)) continue;

                                    var restriction = new Restr
                                    {
                                        Kind = "ifeo",
                                        Hive = RegistryHive.LocalMachine,
                                        HiveName = "HKLM",
                                        Path = ifeoPath + "\\" + subKeyName,
                                        Name = "Debugger",
                                        Value = debugger,
                                        Desc = "Запуск «" + subKeyName +
                                            "» перенаправлен на другую программу"
                                    };

                                    list.Items.Add(MakeRestrItem(restriction, true));
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("IFEO: " + ex.Message);
                    }
                }

                try
                {
                    using (var baseKey = OpenRestrictionBase(RegistryHive.LocalMachine))
                    using (var usbKey = baseKey.OpenSubKey(UsbStorPath))
                    {
                        string value = Convert.ToString(
                            usbKey == null ? null : usbKey.GetValue("Start")) ?? "";

                        if (value == "4")
                        {
                            var restriction = new Restr
                            {
                                Kind = "usb",
                                Hive = RegistryHive.LocalMachine,
                                HiveName = "HKLM",
                                Path = UsbStorPath,
                                Name = "Start",
                                Value = value,
                                Desc = "USB-накопители отключены (значение 3 обычно включает службу)"
                            };

                            list.Items.Add(MakeRestrItem(restriction, false));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("USBSTOR: " + ex.Message);
                }
            }
            finally
            {
                list.EndUpdate();
            }

            UpdateRestrInfo();
            Log("Сканирование ограничений завершено.");
        }

        private void RemoveSelectedRestrictions()
        {
            var selected = restrList.Items.Cast<ListViewItem>()
                .Where(item => item.Checked)
                .Select(item => item.Tag as Restr)
                .Where(item => item != null)
                .ToList();

            if (selected.Count == 0)
            {
                MessageBox.Show("Ничего не выбрано. Отметьте ограничения галочками.",
                    "Ограничения");
                return;
            }

            if (!IsAdmin && selected.Any(item => item.Hive == RegistryHive.LocalMachine))
            {
                MessageBox.Show(
                    "Для изменения HKLM нужны права администратора. " +
                    "Пункты HKLM будут пропущены.",
                    "Ограничения");
                selected = selected.Where(item => item.Hive != RegistryHive.LocalMachine).ToList();
                if (selected.Count == 0) return;
            }

            if (selected.Any(item => item.Kind == "ifeo") &&
                !Confirm("Выбраны IFEO Debugger-перенаправления. Они могут быть нужны " +
                    "легитимным программам. Удалять выбранные значения?"))
                return;

            if (!Confirm("Снять выбранные ограничения: " + selected.Count +
                "?\n\nПеред изменениями будет создана резервная копия .reg в папке:\n" +
                BackupDir))
                return;

            string backup = null;

            try
            {
                Directory.CreateDirectory(BackupDir);
                backup = Path.Combine(BackupDir,
                    "restrictions_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".reg");

                ExportRegKeys(selected.Select(item => item.FullKey).Distinct().ToList(), backup);
                Log("Создана резервная копия перед изменениями: " + backup);
            }
            catch (Exception ex)
            {
                Log("Ошибка резервного копирования: " + ex.Message);
                MessageBox.Show("Резервная копия не создана. Изменения отменены.\n\n" +
                    ex.Message, "Ошибка резервного копирования",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            int success = 0;
            int failed = 0;

            foreach (var restriction in selected)
            {
                try
                {
                    RemoveRestriction(restriction);
                    success++;
                    Log("Снято ограничение: " + restriction.FullKey + " → " + restriction.Name);
                }
                catch (Exception ex)
                {
                    failed++;
                    Log("Не удалось снять " + restriction.FullKey + " → " +
                        restriction.Name + ": " + ex.Message);
                }
            }

            string message = "Снято: " + success +
                (failed > 0 ? ", ошибок: " + failed + " (см. «Журнал»)." : ".");

            message += "\n\nРезервная копия:\n" + backup;
            message += "\n\nЧасть изменений вступит в силу после перезапуска Проводника " +
                "или повторного входа в систему. Групповая политика организации может " +
                "восстановить эти параметры.";

            MessageBox.Show(message, "Результат");
            ScanRestrictions();
        }

        private void RemoveRestriction(Restr restriction)
        {
            using (var baseKey = OpenRestrictionBase(restriction.Hive))
            using (var key = baseKey.OpenSubKey(restriction.Path, true))
            {
                if (key == null)
                    throw new InvalidOperationException("Раздел не найден: " + restriction.FullKey);

                if (restriction.Kind == "usb")
                    key.SetValue("Start", 3, RegistryValueKind.DWord);
                else
                    key.DeleteValue(restriction.Name, false);
            }
        }

        private void ExportPoliciesBackup()
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "REG-файл|*.reg",
                FileName = "SimpleUnlocker_policies_backup.reg"
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;

                var keys = new List<string>
                {
                    "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies",
                    "HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies",
                    "HKCU\\Software\\Policies\\Microsoft\\Windows\\System",
                    "HKLM\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options",
                    "HKLM\\SYSTEM\\CurrentControlSet\\Services\\USBSTOR",
                    "HKLM\\Software\\Policies\\Microsoft\\Windows Defender"
                };

                if (Environment.Is64BitOperatingSystem)
                    keys.Add("HKLM\\Software\\WOW6432Node\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options");

                try
                {
                    ExportRegKeys(keys, dialog.FileName);
                    Log("Создана резервная копия политик: " + dialog.FileName);
                    MessageBox.Show("Резервная копия успешно создана:\n" + dialog.FileName,
                        "Экспорт завершён");
                }
                catch (Exception ex)
                {
                    Log("Ошибка экспорта резервной копии: " + ex.Message);
                    MessageBox.Show(ex.Message, "Ошибка экспорта",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ImportPoliciesBackup()
        {
            using (var dialog = new OpenFileDialog
            {
                Filter = "REG-файл|*.reg",
                Title = "Выберите резервную копию реестра"
            })
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;

                if (!Confirm("Импортировать выбранный REG-файл? Это изменит реестр Windows. " +
                    "Перед продолжением убедитесь, что файл получен из доверенного источника."))
                    return;

                try
                {
                    var startInfo = new ProcessStartInfo("reg.exe",
                        "import \"" + dialog.FileName + "\"")
                    {
                        UseShellExecute = true,
                        Verb = IsAdmin ? "" : "runas"
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process == null)
                            throw new InvalidOperationException("Не удалось запустить reg.exe.");

                        process.WaitForExit();

                        if (process.ExitCode != 0)
                            throw new InvalidOperationException(
                                "reg.exe завершился с кодом " + process.ExitCode + ".");
                    }

                    Log("REG-файл успешно импортирован: " + dialog.FileName);
                    MessageBox.Show("Импорт завершён.", "Готово");
                    ScanRestrictions();
                }
                catch (Exception ex)
                {
                    Log("Ошибка импорта REG: " + ex.Message);
                    MessageBox.Show(ex.Message, "Ошибка импорта",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ExportRestrictionsReport()
        {
            var report = string.Join(Environment.NewLine,
                restrList.Items.Cast<ListViewItem>().Select(item =>
                    string.Join(" | ", item.SubItems.Cast<ListViewItem.ListViewSubItem>()
                        .Select(subItem => subItem.Text))));

            using (var dialog = new SaveFileDialog
            {
                Filter = "TXT|*.txt",
                FileName = "restrictions.txt"
            })
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    File.WriteAllText(dialog.FileName, report, Encoding.UTF8);
                    Log("Экспортирован отчёт ограничений: " + dialog.FileName);
                }
            }
        }

        private bool RegistryPathExists(string registryPath)
        {
            int split = registryPath.IndexOf('\\');
            if (split <= 0) return false;

            string root = registryPath.Substring(0, split).ToUpperInvariant();
            string subPath = registryPath.Substring(split + 1);
            RegistryHive hive;

            switch (root)
            {
                case "HKCU": hive = RegistryHive.CurrentUser; break;
                case "HKLM": hive = RegistryHive.LocalMachine; break;
                default: return false;
            }

            try
            {
                using (var baseKey = OpenRestrictionBase(hive))
                using (var key = baseKey.OpenSubKey(subPath))
                    return key != null;
            }
            catch
            {
                return false;
            }
        }

        // Создаёт единый REG-файл. Если хотя бы один ключ не экспортирован,
        // операция считается неуспешной — нельзя продолжать снятие ограничений.
        private void ExportRegKeys(IEnumerable<string> keys, string file)
        {
            var output = new StringBuilder(
                "Windows Registry Editor Version 5.00\r\n\r\n");
            var errors = new List<string>();
            int exported = 0;

            foreach (string key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string temp = Path.Combine(Path.GetTempPath(),
                    Guid.NewGuid().ToString("N") + ".reg");

                try
                {
                    var startInfo = new ProcessStartInfo("reg.exe",
                        "export \"" + key + "\" \"" + temp + "\" /y")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    using (var process = Process.Start(startInfo))
                    {
                        if (process == null)
                            throw new InvalidOperationException("Не удалось запустить reg.exe.");

                        if (!process.WaitForExit(20000))
                        {
                            try { process.Kill(); } catch { }
                            throw new System.TimeoutException("Истекло время ожидания экспорта.");
                        }

                        if (process.ExitCode != 0)
                        {
                            // Отсутствующие необязательные разделы пропускаем;
                            // существующий, но недоступный ключ считаем ошибкой.
                            if (!RegistryPathExists(key))
                            {
                                continue;
                            }

                            throw new InvalidOperationException(
                                "reg.exe завершился с кодом " + process.ExitCode + ".");
                        }
                    }

                    if (!File.Exists(temp))
                    {
                        if (!RegistryPathExists(key))
                            continue;
                        throw new FileNotFoundException("reg.exe не создал файл экспорта.");
                    }

                    string data = File.ReadAllText(temp, Encoding.Unicode);
                    int first = data.IndexOf("\r\n\r\n", StringComparison.Ordinal);

                    if (first < 0)
                        throw new InvalidDataException("Некорректный REG-файл для ключа " + key);

                    string body = data.Substring(first + 4).Trim();
                    if (body.Length > 0)
                    {
                        output.AppendLine(body);
                        output.AppendLine();
                    }

                    exported++;
                }
                catch (Exception ex)
                {
                    errors.Add(key + ": " + ex.Message);
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
            }

            if (errors.Count > 0 || exported == 0)
                throw new InvalidOperationException(
                    "Резервная копия неполная. Файл не будет считаться успешной копией.\n\n" +
                    string.Join("\n", errors));

            string destination = Path.GetFullPath(file);
            string staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                File.WriteAllText(staging, output.ToString(), Encoding.Unicode);

                // Проверяем, что записан непустой REG-файл с заголовком.
                string check = File.ReadAllText(staging, Encoding.Unicode);
                if (!check.StartsWith("Windows Registry Editor Version 5.00",
                    StringComparison.Ordinal))
                    throw new InvalidDataException("Проверка созданного REG-файла не пройдена.");

                if (File.Exists(destination))
                    File.Delete(destination);

                File.Move(staging, destination);
            }
            finally
            {
                try { if (File.Exists(staging)) File.Delete(staging); } catch { }
            }
        }
        // ================= WINLOGON =================
        static string FmtReg(object v){
            if(v==null)return "";
            if(v is byte[] b)return BitConverter.ToString(b);
            if(v is string[] sa)return string.Join(" ; ",sa);
            return Convert.ToString(v)??"";
        }

        void RefreshWinlogon(){
            if(winlogonList==null)return;
            var cur=new Dictionary<string,string>();
            try{
                using(var k=Registry.LocalMachine.OpenSubKey(WinlogonKey)){
                    if(k!=null){
                        foreach(var n in k.GetValueNames())cur[n==""?"(по умолчанию)":n]=FmtReg(k.GetValue(n));
                        foreach(var sub in k.GetSubKeyNames())cur["[ПОДКЛЮЧ] "+sub]="Открыть regedit для просмотра";
                    }
                }
            }catch(Exception ex){Log("Winlogon: "+ex.Message);}
            if(winlogonPrev!=null){
                string stamp="ИЗМЕНЕНО "+DateTime.Now.ToString("HH:mm:ss");
                foreach(var kv in cur){
                    if(!winlogonPrev.TryGetValue(kv.Key,out var old)){Log("Winlogon: добавлен параметр "+kv.Key+" = "+kv.Value);winlogonMarks[kv.Key]="ДОБАВЛЕНО "+DateTime.Now.ToString("HH:mm:ss");}
                    else if(old!=kv.Value){Log("Winlogon: "+kv.Key+": '"+old+"' → '"+kv.Value+"'");winlogonMarks[kv.Key]=stamp;}
                }
                foreach(var kv in winlogonPrev)if(!cur.ContainsKey(kv.Key))Log("Winlogon: удалён параметр "+kv.Key+" (было '"+kv.Value+"')");
            }
            winlogonPrev=cur;
            winlogonList.BeginUpdate();
            try{
                winlogonList.Items.Clear();
                foreach(var kv in cur){
                    var it=new ListViewItem(kv.Key);it.SubItems.Add(kv.Value);
                    winlogonMarks.TryGetValue(kv.Key,out var mark);it.SubItems.Add(mark??"");
                    bool bad=(kv.Key=="Shell"&&!kv.Value.Equals("explorer.exe",StringComparison.OrdinalIgnoreCase))
                          ||(kv.Key=="Userinit"&&!kv.Value.EndsWith("userinit.exe,",StringComparison.OrdinalIgnoreCase));
                    if(bad){it.BackColor=Color.FromArgb(255,225,225);it.ForeColor=Color.DarkRed;}
                    else if(mark!=null){it.BackColor=Color.FromArgb(255,245,200);}
                    else if(kv.Key.StartsWith("[ПОДКЛЮЧ]"))it.BackColor=Color.FromArgb(225,235,250);
                    winlogonList.Items.Add(it);
                }
            }finally{winlogonList.EndUpdate();}
        }

        void AddWinlogon(){
            var p=NewTab("Winlogon");
            var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);
            var l=List(p,new[]{"Параметр","Значение","Статус"});
            l.Columns[0].Width=220;l.Columns[1].Width=520;l.Columns[2].Width=180;
            l.BringToFront();f.BringToFront();winlogonList=l;
            winlogonDebounce=new System.Windows.Forms.Timer{Interval=300};
            winlogonDebounce.Tick+=(s,e)=>{winlogonDebounce.Stop();RefreshWinlogon();};
            AddButton(f,"Обновить вручную",RefreshWinlogon);
            AddButton(f,"Восстановить Shell/Userinit",()=>{
                if(!IsAdmin){MessageBox.Show("Нужны права администратора.");return;}
                if(!Confirm("Восстановить стандартные Shell и Userinit?"))return;
                using(var k=Registry.LocalMachine.OpenSubKey(WinlogonKey,true)){
                    k.SetValue("Shell","explorer.exe");
                    k.SetValue("Userinit",Environment.GetFolderPath(Environment.SpecialFolder.Windows)+@"\system32\userinit.exe,");
                }
                Log("Winlogon восстановлен");RefreshWinlogon();
            });
            RefreshWinlogon();
        }
        void AddDrivers(){
            var p=NewTab("Драйверы");
            var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};
            p.Controls.Add(f);
            var l=List(p,new[]{"Драйвер","Устройство"});
            l.Columns[0].Width=300;
            l.Columns[1].Width=650;
            l.BringToFront();f.BringToFront();

            Action refresh=()=>{
                l.BeginUpdate();
                try{
                    l.Items.Clear();
                    using(var searcher=new System.Management.ManagementObjectSearcher(
                        "SELECT DeviceName, DriverName FROM Win32_PnPSignedDriver"))
                    using(var results=searcher.Get()){
                        foreach(System.Management.ManagementObject item in results){
                            string driver=Convert.ToString(item["DriverName"])??"";
                            string device=Convert.ToString(item["DeviceName"])??"";
                            if(string.IsNullOrWhiteSpace(driver))driver="Неизвестный драйвер";
                            if(string.IsNullOrWhiteSpace(device))device="Устройство не определено";
                            var row=new ListViewItem(driver);
                            row.SubItems.Add(device);
                            l.Items.Add(row);
                        }
                    }
                }catch(Exception ex){
                    Log("Не удалось получить список драйверов: "+ex.Message);
                    MessageBox.Show("Не удалось получить список драйверов.\n"+ex.Message,"Драйверы",
                        MessageBoxButtons.OK,MessageBoxIcon.Warning);
                }finally{l.EndUpdate();}
            };
            AddButton(f,"Обновить список",refresh);
            AddButton(f,"Открыть диспетчер устройств",()=>Run("devmgmt.msc",""));
            refresh();
        }
        void AddSystemInfo()
        {
            var p = NewTab("ПК");
            var top = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.FromArgb(23, 30, 43) };
            var refreshButton = new Button { Text = "Обновить характеристики", Left = 14, Top = 8, Width = 190, Height = 30 };
            top.Controls.Add(refreshButton);
            p.Controls.Add(top);

            var cards = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                WrapContents = true,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(14),
                BackColor = Color.FromArgb(17, 23, 34)
            };
            p.Controls.Add(cards);
            cards.BringToFront();
            top.BringToFront();

            Func<string, string, string> wmi = (className, property) =>
            {
                try
                {
                    using (var searcher = new ManagementObjectSearcher("SELECT " + property + " FROM " + className))
                    using (var results = searcher.Get())
                    {
                        foreach (ManagementObject item in results)
                        {
                            var value = Convert.ToString(item[property]);
                            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                        }
                    }
                }
                catch { }
                return "Не удалось определить";
            };

            Action<string, string, string[]> addCard = (heading, icon, lines) =>
            {
                var card = new Panel
                {
                    Width = 440, Height = 190, Margin = new Padding(8),
                    BackColor = Color.FromArgb(29, 39, 55),
                    Padding = new Padding(14)
                };
                var accent = new Panel { Dock = DockStyle.Left, Width = 4, BackColor = Color.FromArgb(65, 137, 220) };
                card.Controls.Add(accent);
                var title = new Label
                {
                    Text = icon + "  " + heading, Left = 18, Top = 12, Width = 400, Height = 28,
                    ForeColor = Color.FromArgb(115, 177, 255), Font = new Font("Segoe UI Semibold", 12F),
                    AutoEllipsis = true
                };
                card.Controls.Add(title);
                var body = new Label
                {
                    Text = string.Join(Environment.NewLine, lines), Left = 20, Top = 48,
                    Width = 398, Height = 126, ForeColor = Color.FromArgb(225, 232, 242),
                    Font = new Font("Segoe UI", 9.5F), AutoEllipsis = false
                };
                card.Controls.Add(body);
                cards.Controls.Add(card);
            };

            Action refresh = () =>
            {
                cards.SuspendLayout();
                cards.Controls.Clear();
                string manufacturer = wmi("Win32_ComputerSystem", "Manufacturer");
                string model = wmi("Win32_ComputerSystem", "Model");
                string board = wmi("Win32_BaseBoard", "Product");
                string boardMaker = wmi("Win32_BaseBoard", "Manufacturer");

                string gpu = wmi("Win32_VideoController", "Name");
                string gpuMemory = wmi("Win32_VideoController", "AdapterRAM");
                string gpuDriver = wmi("Win32_VideoController", "DriverVersion");
                double gpuBytes;
                string gpuMemoryText = double.TryParse(gpuMemory, NumberStyles.Any, CultureInfo.InvariantCulture, out gpuBytes) && gpuBytes > 0
                    ? (gpuBytes / 1073741824.0).ToString("0.0") + " ГБ" : "Не сообщается драйвером";

                string cpu = wmi("Win32_Processor", "Name");
                string cores = wmi("Win32_Processor", "NumberOfCores");
                string threads = wmi("Win32_Processor", "NumberOfLogicalProcessors");
                string cpuSpeed = wmi("Win32_Processor", "MaxClockSpeed");
                string speedText;
                double mhz;
                speedText = double.TryParse(cpuSpeed, NumberStyles.Any, CultureInfo.InvariantCulture, out mhz)
                    ? (mhz / 1000.0).ToString("0.00") + " ГГц" : cpuSpeed;

                string ramBytes = wmi("Win32_ComputerSystem", "TotalPhysicalMemory");
                double ram;
                string ramText = double.TryParse(ramBytes, NumberStyles.Any, CultureInfo.InvariantCulture, out ram)
                    ? (ram / 1073741824.0).ToString("0.0") + " ГБ" : "Не удалось определить";
                string ramSpeed = wmi("Win32_PhysicalMemory", "Speed");
                string ramType = wmi("Win32_PhysicalMemory", "SMBIOSMemoryType");
                string ramSpeedText = ramSpeed == "Не удалось определить" ? "Не сообщается" : ramSpeed + " МГц";

                string osName = wmi("Win32_OperatingSystem", "Caption");
                string osVersion = wmi("Win32_OperatingSystem", "Version");
                string build = wmi("Win32_OperatingSystem", "BuildNumber");
                string arch = wmi("Win32_OperatingSystem", "OSArchitecture");
                string installRaw = wmi("Win32_OperatingSystem", "InstallDate");
                string installDate = installRaw;
                try { if (installRaw.Length >= 8) installDate = DateTime.ParseExact(installRaw.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture).ToString("dd.MM.yyyy"); } catch { }

                addCard("Характеристики", "▣", new[] {
                    "Производитель: " + manufacturer,
                    "Модель: " + model,
                    "Материнская плата: " + boardMaker + " " + board
                });
                addCard("Видеокарта", "▰", new[] {
                    gpu,
                    "Видеопамять: " + gpuMemoryText,
                    "Версия драйвера: " + gpuDriver
                });
                addCard("Процессор", "◈", new[] {
                    cpu,
                    "Ядра: " + cores + "    Потоки: " + threads,
                    "Максимальная частота: " + speedText
                });
                addCard("ОЗУ", "▤", new[] {
                    "Установлено: " + ramText,
                    "Скорость: " + ramSpeedText,
                    "Тип памяти (SMBIOS): " + ramType
                });
                addCard("Система", "▦", new[] {
                    osName,
                    "Версия: " + osVersion + "    Сборка: " + build,
                    "Разрядность: " + arch,
                    "Установлена: " + installDate
                });
                cards.ResumeLayout();
            };
            refreshButton.Click += (s, e) => refresh();
            refresh();
        }
        void AddDisk()
        {
            var p = NewTab("Диски");
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            p.Controls.Add(bar);
            var l = List(p, new[] { "Диск", "Файловая система", "Всего ГБ", "Свободно ГБ" });
            l.BringToFront(); bar.BringToFront();
            Action refresh = () =>
            {
                l.Items.Clear();
                foreach (var d in DriveInfo.GetDrives())
                {
                    try { if (!d.IsReady) continue;
                        var it = new ListViewItem(d.Name); it.SubItems.Add(d.DriveFormat);
                        it.SubItems.Add((d.TotalSize / 1073741824.0).ToString("F1"));
                        it.SubItems.Add((d.AvailableFreeSpace / 1073741824.0).ToString("F1")); l.Items.Add(it);
                    } catch { }
                }
            };
            AddButton(bar, "Обновить список дисков", refresh);
        }
        void AddNetwork(){var p=NewTab("Сеть");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var t=new TextBox{Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Both};p.Controls.Add(t);t.BringToFront();f.BringToFront();AddButton(f,"IP-конфигурация",()=>t.Text=Cmd("ipconfig /all"));AddButton(f,"Сетевые соединения",()=>t.Text=Cmd("netstat -ano"));AddButton(f,"Открыть настройки сети",()=>Run("ms-settings:network",""));}
        void AddSecurity(){var p=NewTab("Безопасность");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var t=new TextBox{Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Both};p.Controls.Add(t);t.BringToFront();f.BringToFront();AddButton(f,"Статус Defender",()=>t.Text=Cmd("powershell -NoProfile -Command \"Get-MpComputerStatus | Format-List\""));AddButton(f,"Брандмауэр",()=>t.Text=Cmd("netsh advfirewall show allprofiles"));AddButton(f,"Открыть безопасность Windows",()=>Run("windowsdefender:",""));}
        void AddRepair(){var p=NewTab("Восстановление");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var t=new TextBox{Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Both};p.Controls.Add(t);t.BringToFront();f.BringToFront();AddButton(f,"SFC /scannow",()=>{if(Confirm("Запустить проверку системных файлов?"))Run("cmd.exe","/k sfc /scannow",true);});AddButton(f,"DISM RestoreHealth",()=>{if(Confirm("Запустить восстановление образа Windows?"))Run("cmd.exe","/k DISM /Online /Cleanup-Image /RestoreHealth",true);});AddButton(f,"Проверка DISM",()=>Run("cmd.exe","/k DISM /Online /Cleanup-Image /CheckHealth",true));}
        void AddRestorePoint(){var p=NewTab("Точка восстановления");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var t=new TextBox{Dock=DockStyle.Fill,Multiline=true};p.Controls.Add(t);t.BringToFront();f.BringToFront();AddButton(f,"Создать точку",()=>{if(!IsAdmin){MessageBox.Show("Запустите программу от администратора.");return;}if(Confirm("Создать точку восстановления системы?"))Run("powershell.exe","-NoProfile -Command \"Checkpoint-Computer -Description 'SimpleUnlocker backup' -RestorePointType 'MODIFY_SETTINGS'\"",true);});AddButton(f,"Открыть восстановление",()=>Run("rstrui.exe",""));}
        void AddRegistryBackup(){var p=NewTab("Резерв реестра");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var t=new TextBox{Dock=DockStyle.Fill,Multiline=true};p.Controls.Add(t);t.BringToFront();f.BringToFront();AddButton(f,"Экспорт ветки Winlogon",()=>{using(var d=new SaveFileDialog{Filter="REG|*.reg",FileName="Winlogon_backup.reg"})if(d.ShowDialog()==DialogResult.OK)Run("reg.exe","export \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" \""+d.FileName+"\" /y",true);});AddButton(f,"Экспорт Policies",()=>{using(var d=new SaveFileDialog{Filter="REG|*.reg",FileName="Policies_backup.reg"})if(d.ShowDialog()==DialogResult.OK)Run("reg.exe","export \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies\" \""+d.FileName+"\" /y");});AddButton(f,"Открыть regedit",()=>Run("regedit.exe",""));}
        void AddEventLog(){var p=NewTab("Журнал событий");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var t=new TextBox{Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Both,Font=new Font("Consolas",9)};p.Controls.Add(t);t.BringToFront();f.BringToFront();AddButton(f,"Последние системные ошибки",()=>t.Text=Cmd("powershell -NoProfile -Command \"Get-WinEvent -LogName System -MaxEvents 40 | Format-Table TimeCreated,Id,LevelDisplayName,Message -Wrap\""));AddButton(f,"Открыть eventvwr",()=>Run("eventvwr.msc",""));}

        static string PromptText(string message, string title, string initial)
        {
            using var dialog = new Form { Text = title, Width = 420, Height = 145, StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
            var label = new Label { Text = message, Left = 12, Top = 12, AutoSize = true };
            var input = new TextBox { Text = initial, Left = 12, Top = 38, Width = 380 };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 230, Top = 72, Width = 75 };
            var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Left = 317, Top = 72, Width = 75 };
            dialog.Controls.AddRange(new Control[] { label, input, ok, cancel });
            dialog.AcceptButton = ok; dialog.CancelButton = cancel;
            return dialog.ShowDialog() == DialogResult.OK ? input.Text : "";
        }

        // ================= WINDOWS-STYLE FILE EXPLORER =================
        void AddExplorer()
        {
            var page = NewTab("Проводник");
            page.Padding = new Padding(0);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = Color.FromArgb(18, 25, 38),
                Padding = new Padding(8)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            page.Controls.Add(root);

            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = true,
                Padding = new Padding(3),
                BackColor = Color.FromArgb(24, 33, 49)
            };
            root.Controls.Add(toolbar, 0, 0);

            Button Tool(string caption, Action action)
            {
                var b = new Button
                {
                    Text = caption,
                    AutoSize = true,
                    Height = 34,
                    Margin = new Padding(3),
                    Padding = new Padding(10, 4, 10, 4),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.FromArgb(39, 54, 77),
                    ForeColor = Color.FromArgb(231, 239, 250),
                    Cursor = Cursors.Hand
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(57, 78, 108);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(39, 91, 153);
                b.Click += (s, e) =>
                {
                    try { action(); }
                    catch (Exception ex) { Log("Проводник: " + ex.Message); MessageBox.Show(ex.Message, "Проводник", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                };
                toolbar.Controls.Add(b);
                return b;
            }

            var addressPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1,
                BackColor = Color.FromArgb(24, 33, 49),
                Padding = new Padding(8, 6, 8, 6),
                Margin = new Padding(0, 5, 0, 5),
                Height = 42
            };
            addressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            addressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            addressPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var addressLabel = new Label
            {
                Text = "Адрес:",
                AutoSize = true,
                ForeColor = Color.FromArgb(177, 193, 216),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(2, 7, 8, 0)
            };
            var address = new TextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(13, 19, 30),
                ForeColor = Color.FromArgb(235, 242, 252),
                Font = new Font("Segoe UI", 9.5f),
                Margin = new Padding(0, 0, 8, 0)
            };
            var search = new TextBox
            {
                Width = 220,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(13, 19, 30),
                ForeColor = Color.FromArgb(235, 242, 252),
                Font = new Font("Segoe UI", 9.5f),
                Margin = new Padding(0, 0, 0, 0)
            };
            search.Text = "Поиск в текущей папке";
            addressPanel.Controls.Add(addressLabel, 0, 0);
            addressPanel.Controls.Add(address, 1, 0);
            addressPanel.Controls.Add(search, 2, 0);
            root.Controls.Add(addressPanel, 0, 1);

            var content = new SplitContainer
            {
                Dock = DockStyle.Fill,
                // At construction time the control may still have its default
                // width (150 px). Keep the initial splitter position valid.
                SplitterDistance = 100,
                BackColor = Color.FromArgb(18, 25, 38),
                BorderStyle = BorderStyle.FixedSingle,
                Panel1MinSize = 0,
                Panel2MinSize = 0
            };

            // Move the splitter to the preferred position only after the
            // SplitContainer receives its real layout size. Clamp the value
            // so it always stays within the available width.
            content.SizeChanged += (_, __) =>
            {
                int maxDistance = content.Width - content.SplitterWidth - content.Panel2MinSize;
                if (maxDistance < 1)
                    return;

                int targetDistance = Math.Min(235, maxDistance);
                targetDistance = Math.Max(content.Panel1MinSize, targetDistance);

                if (content.SplitterDistance != targetDistance)
                    content.SplitterDistance = targetDistance;
            };

            root.Controls.Add(content, 0, 2);

            var navigation = new TreeView
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(21, 29, 43),
                ForeColor = Color.FromArgb(222, 232, 246),
                HideSelection = false,
                FullRowSelect = true,
                ShowLines = false,
                ShowRootLines = false,
                ShowPlusMinus = true,
                ItemHeight = 27,
                Font = new Font("Segoe UI", 9.2f)
            };
            content.Panel1.Controls.Add(navigation);

            var files = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HideSelection = false,
                MultiSelect = true,
                BorderStyle = BorderStyle.None,
                BackColor = Color.FromArgb(17, 24, 37),
                ForeColor = Color.FromArgb(229, 237, 248),
                Font = new Font("Segoe UI", 9.2f)
            };
            files.Columns.Add("Имя", 330);
            files.Columns.Add("Дата изменения", 155);
            files.Columns.Add("Тип", 135);
            files.Columns.Add("Размер", 110);
            content.Panel2.Controls.Add(files);

            var footer = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                Padding = new Padding(7, 6, 0, 2),
                ForeColor = Color.FromArgb(143, 160, 184),
                BackColor = Color.FromArgb(24, 33, 49),
                Text = "Готово"
            };
            root.Controls.Add(footer, 0, 3);

            var context = new ContextMenuStrip();
            var openItem = context.Items.Add("Открыть");
            var copyItem = context.Items.Add("Копировать");
            var cutItem = context.Items.Add("Вырезать");
            var pasteItem = context.Items.Add("Вставить");
            context.Items.Add(new ToolStripSeparator());
            var renameItem = context.Items.Add("Переименовать");
            var newFolderItem = context.Items.Add("Создать папку");
            var deleteItem = context.Items.Add("Удалить");
            context.Items.Add(new ToolStripSeparator());
            var propertiesItem = context.Items.Add("Свойства");
            files.ContextMenuStrip = context;

            string current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var backHistory = new Stack<string>();
            var forwardHistory = new Stack<string>();
            bool changingTree = false;
            string clipboardPath = null;
            bool clipboardCut = false;

            string FormatSize(long size)
            {
                string[] units = { "Б", "КБ", "МБ", "ГБ", "ТБ" };
                double value = size;
                int unit = 0;
                while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
                return unit == 0 ? value.ToString("N0") + " " + units[unit] : value.ToString("N1") + " " + units[unit];
            }

            void UpdateFooter()
            {
                int selected = files.SelectedItems.Count;
                footer.Text = selected > 0
                    ? $"{files.Items.Count} объектов   •   Выбрано: {selected}"
                    : $"{files.Items.Count} объектов";
            }

            void SelectTreePath(string path)
            {
                changingTree = true;
                try
                {
                    foreach (TreeNode rootNode in navigation.Nodes)
                    {
                        string rootPath = Convert.ToString(rootNode.Tag) ?? "";
                        if (path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                        {
                            TreeNode node = rootNode;
                            string remainder = path.Substring(rootPath.Length).Trim(Path.DirectorySeparatorChar);
                            if (remainder.Length > 0)
                            {
                                foreach (string part in remainder.Split(Path.DirectorySeparatorChar))
                                {
                                    if (!node.IsExpanded) node.Expand();
                                    TreeNode next = node.Nodes.Cast<TreeNode>().FirstOrDefault(n =>
                                        string.Equals(n.Text, part, StringComparison.OrdinalIgnoreCase));
                                    if (next == null) break;
                                    node = next;
                                }
                            }
                            navigation.SelectedNode = node;
                            node.EnsureVisible();
                            break;
                        }
                    }
                }
                catch { }
                finally { changingTree = false; }
            }

            void Navigate(string path, bool addHistory)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                string full;
                try { full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'))); }
                catch { MessageBox.Show("Некорректный путь.", "Проводник"); return; }
                if (!Directory.Exists(full))
                {
                    if (File.Exists(full)) { Run("explorer.exe", "/select,\"" + full + "\""); return; }
                    MessageBox.Show("Папка не существует или недоступна.", "Проводник");
                    return;
                }

                if (addHistory && !string.Equals(current, full, StringComparison.OrdinalIgnoreCase))
                {
                    backHistory.Push(current);
                    forwardHistory.Clear();
                }
                current = full;
                address.Text = current;
                files.BeginUpdate();
                files.Items.Clear();
                try
                {
                    var dirs = Directory.EnumerateDirectories(current)
                        .Select(x => new DirectoryInfo(x))
                        .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
                    foreach (var info in dirs)
                    {
                        var item = new ListViewItem(info.Name) { Tag = info.FullName };
                        item.SubItems.Add(info.LastWriteTime.ToString("dd.MM.yyyy HH:mm"));
                        item.SubItems.Add("Папка с файлами");
                        item.SubItems.Add("");
                        files.Items.Add(item);
                    }

                    var entries = Directory.EnumerateFiles(current)
                        .Select(x => new FileInfo(x))
                        .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
                    foreach (var info in entries)
                    {
                        var item = new ListViewItem(info.Name) { Tag = info.FullName };
                        item.SubItems.Add(info.LastWriteTime.ToString("dd.MM.yyyy HH:mm"));
                        item.SubItems.Add(string.IsNullOrEmpty(info.Extension) ? "Файл" : info.Extension + " файл");
                        item.SubItems.Add(FormatSize(info.Length));
                        files.Items.Add(item);
                    }
                    footer.Text = $"{files.Items.Count} объектов";
                }
                catch (UnauthorizedAccessException) { footer.Text = "Нет доступа к этой папке"; }
                catch (Exception ex) { footer.Text = "Ошибка чтения папки: " + ex.Message; }
                finally { files.EndUpdate(); }
                SelectTreePath(current);
                UpdateFooter();
            }

            void AddLazyNode(TreeNode node)
            {
                node.Nodes.Clear();
                string path = Convert.ToString(node.Tag) ?? "";
                try
                {
                    foreach (string dir in Directory.EnumerateDirectories(path).Take(250))
                    {
                        var child = new TreeNode(Path.GetFileName(dir)) { Tag = dir };
                        child.Nodes.Add(new TreeNode("…"));
                        node.Nodes.Add(child);
                    }
                }
                catch { }
            }

            void BuildNavigation()
            {
                navigation.Nodes.Clear();
                var quick = new TreeNode("Быстрый доступ") { Tag = current };
                navigation.Nodes.Add(quick);
                string[] known =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
                };
                string[] names = { "Рабочий стол", "Документы", "Изображения", "Музыка", "Видео", "Загрузки" };
                for (int i = 0; i < known.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(known[i]) && Directory.Exists(known[i]))
                    {
                        var n = new TreeNode(names[i]) { Tag = known[i] };
                        n.Nodes.Add(new TreeNode("…"));
                        navigation.Nodes.Add(n);
                    }
                }
                var computer = new TreeNode("Этот компьютер") { Tag = "COMPUTER" };
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    string label;
                    try { label = drive.IsReady ? drive.VolumeLabel : "Недоступен"; }
                    catch { label = "Недоступен"; }
                    var d = new TreeNode($"{drive.Name} ({label})") { Tag = drive.Name };
                    d.Nodes.Add(new TreeNode("…"));
                    computer.Nodes.Add(d);
                }
                navigation.Nodes.Add(computer);
                quick.Nodes.Add(new TreeNode("…"));
                quick.Expand();
            }

            void OpenSelected()
            {
                if (files.SelectedItems.Count == 0) return;
                string target = Convert.ToString(files.SelectedItems[0].Tag) ?? "";
                if (Directory.Exists(target)) Navigate(target, true);
                else if (File.Exists(target)) Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }

            void CreateFolder()
            {
                string name = PromptText("Имя новой папки:", "Создать папку", "Новая папка");
                if (string.IsNullOrWhiteSpace(name)) return;
                string destination = Path.Combine(current, name.Trim());
                if (Directory.Exists(destination) || File.Exists(destination))
                { MessageBox.Show("Объект с таким именем уже существует."); return; }
                Directory.CreateDirectory(destination);
                Navigate(current, false);
            }

            void RenameSelected()
            {
                if (files.SelectedItems.Count != 1) return;
                string oldPath = Convert.ToString(files.SelectedItems[0].Tag) ?? "";
                string oldName = Path.GetFileName(oldPath);
                string newName = PromptText("Новое имя:", "Переименовать", oldName);
                if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;
                string newPath = Path.Combine(current, newName.Trim());
                if (Directory.Exists(newPath) || File.Exists(newPath))
                { MessageBox.Show("Объект с таким именем уже существует."); return; }
                if (Directory.Exists(oldPath)) Directory.Move(oldPath, newPath);
                else File.Move(oldPath, newPath);
                Navigate(current, false);
            }

            void DeleteSelected()
            {
                if (files.SelectedItems.Count == 0) return;
                var paths = files.SelectedItems.Cast<ListViewItem>()
                    .Select(i => Convert.ToString(i.Tag) ?? "").Where(x => x.Length > 0).ToList();
                if (!Confirm($"Удалить выбранные объекты ({paths.Count})?")) return;
                foreach (string path in paths)
                {
                    string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
                    string rootPath = (Path.GetPathRoot(full) ?? "").TrimEnd(Path.DirectorySeparatorChar);
                    string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
                    string system = Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd(Path.DirectorySeparatorChar);
                    if (full.Equals(rootPath, StringComparison.OrdinalIgnoreCase) ||
                        full.Equals(windows, StringComparison.OrdinalIgnoreCase) ||
                        full.Equals(system, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Удаление корня диска и системных каталогов запрещено.");
                    if (Directory.Exists(path)) Directory.Delete(path, true);
                    else if (File.Exists(path)) File.Delete(path);
                }
                Navigate(current, false);
            }

            Tool("←", () =>
            {
                if (backHistory.Count == 0) return;
                forwardHistory.Push(current);
                string target = backHistory.Pop();
                Navigate(target, false);
            });
            Tool("→", () =>
            {
                if (forwardHistory.Count == 0) return;
                backHistory.Push(current);
                string target = forwardHistory.Pop();
                Navigate(target, false);
            });
            Tool("↑", () =>
            {
                string parent = Directory.GetParent(current)?.FullName;
                if (!string.IsNullOrEmpty(parent)) Navigate(parent, true);
            });
            Tool("Обновить", () => Navigate(current, false));
            Tool("Создать папку", CreateFolder);
            Tool("Переименовать", RenameSelected);
            Tool("Удалить", DeleteSelected);
            Tool("Свойства", () =>
            {
                if (files.SelectedItems.Count == 0) return;
                string target = Convert.ToString(files.SelectedItems[0].Tag) ?? "";
                if (File.Exists(target))
                {
                    var f = new FileInfo(target);
                    MessageBox.Show($"Имя: {f.Name}\nРасположение: {f.DirectoryName}\nРазмер: {FormatSize(f.Length)}\nИзменён: {f.LastWriteTime}", "Свойства файла");
                }
                else if (Directory.Exists(target))
                {
                    var d = new DirectoryInfo(target);
                    MessageBox.Show($"Имя: {d.Name}\nРасположение: {d.Parent?.FullName}\nИзменён: {d.LastWriteTime}", "Свойства папки");
                }
            });

            Tool("Открыть", OpenSelected);
            address.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { Navigate(address.Text, true); e.SuppressKeyPress = true; }
            };
            search.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    string query = search.Text.Trim();
                    if (query.Length > 0 && query != "Поиск в текущей папке")
                    {
                        try
                        {
                            files.Items.Clear();
                            foreach (string path in Directory.EnumerateFiles(current, "*" + query + "*", SearchOption.TopDirectoryOnly)
                                .Concat(Directory.EnumerateDirectories(current, "*" + query + "*", SearchOption.TopDirectoryOnly)))
                            {
                                bool dir = Directory.Exists(path);
                                var item = new ListViewItem(Path.GetFileName(path)) { Tag = path };
                                item.SubItems.Add(File.GetLastWriteTime(path).ToString("dd.MM.yyyy HH:mm"));
                                item.SubItems.Add(dir ? "Папка с файлами" : Path.GetExtension(path) + " файл");
                                item.SubItems.Add(dir ? "" : FormatSize(new FileInfo(path).Length));
                                files.Items.Add(item);
                            }
                            footer.Text = $"Результаты поиска: {files.Items.Count}";
                        }
                        catch (Exception ex) { footer.Text = "Ошибка поиска: " + ex.Message; }
                    }
                    e.SuppressKeyPress = true;
                }
            };
            search.GotFocus += (s, e) => { if (search.Text == "Поиск в текущей папке") search.Clear(); };
            search.LostFocus += (s, e) => { if (string.IsNullOrWhiteSpace(search.Text)) search.Text = "Поиск в текущей папке"; };
            files.DoubleClick += (s, e) => OpenSelected();
            files.SelectedIndexChanged += (s, e) => UpdateFooter();
            files.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; }
                if (e.KeyCode == Keys.Back)
                {
                    string parent = Directory.GetParent(current)?.FullName;
                    if (!string.IsNullOrEmpty(parent)) Navigate(parent, true);
                }
                if (e.KeyCode == Keys.Delete) DeleteSelected();
            };
            navigation.BeforeExpand += (s, e) =>
            {
                if (e.Node.Nodes.Count == 1 && e.Node.Nodes[0].Text == "…") AddLazyNode(e.Node);
            };
            navigation.AfterSelect += (s, e) =>
            {
                if (!changingTree)
                {
                    string target = Convert.ToString(e.Node.Tag) ?? "";
                    if (target != "COMPUTER" && Directory.Exists(target)) Navigate(target, true);
                }
            };

            openItem.Click += (s, e) => OpenSelected();
            newFolderItem.Click += (s, e) => CreateFolder();
            renameItem.Click += (s, e) => RenameSelected();
            deleteItem.Click += (s, e) => DeleteSelected();
            propertiesItem.Click += (s, e) =>
            {
                if (files.SelectedItems.Count > 0)
                {
                    string target = Convert.ToString(files.SelectedItems[0].Tag) ?? "";
                    if (File.Exists(target)) Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
                    else if (Directory.Exists(target)) Navigate(target, true);
                }
            };
            copyItem.Click += (s, e) =>
            {
                if (files.SelectedItems.Count > 0)
                {
                    clipboardPath = Convert.ToString(files.SelectedItems[0].Tag);
                    clipboardCut = false;
                    try { Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { clipboardPath }); } catch { }
                }
            };
            cutItem.Click += (s, e) =>
            {
                if (files.SelectedItems.Count > 0)
                {
                    clipboardPath = Convert.ToString(files.SelectedItems[0].Tag);
                    clipboardCut = true;
                }
            };
            pasteItem.Click += (s, e) =>
            {
                if (string.IsNullOrEmpty(clipboardPath) || (!File.Exists(clipboardPath) && !Directory.Exists(clipboardPath))) return;
                string destination = Path.Combine(current, Path.GetFileName(clipboardPath));
                if (File.Exists(destination) || Directory.Exists(destination))
                { MessageBox.Show("Объект с таким именем уже существует."); return; }
                if (Directory.Exists(clipboardPath))
                {
                    if (clipboardCut) Directory.Move(clipboardPath, destination);
                    else throw new InvalidOperationException("Копирование папок через это меню пока не поддерживается.");
                }
                else
                {
                    if (clipboardCut) File.Move(clipboardPath, destination);
                    else File.Copy(clipboardPath, destination);
                }
                if (clipboardCut) { clipboardPath = null; clipboardCut = false; }
                Navigate(current, false);
            };
            context.Opening += (s, e) =>
            {
                bool selected = files.SelectedItems.Count > 0;
                openItem.Enabled = selected;
                copyItem.Enabled = selected;
                cutItem.Enabled = selected;
                renameItem.Enabled = files.SelectedItems.Count == 1;
                deleteItem.Enabled = selected;
                propertiesItem.Enabled = files.SelectedItems.Count == 1;
                pasteItem.Enabled = !string.IsNullOrEmpty(clipboardPath) &&
                    (File.Exists(clipboardPath) || Directory.Exists(clipboardPath));
            };

            BuildNavigation();
            Navigate(current, false);
        }
    }
}
