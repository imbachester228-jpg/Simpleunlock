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
            Width = 1120; Height = 760; MinimumSize = new Size(900, 600);
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
            AddDrivers(); AddSystemInfo(); AddDisk(); AddNetwork(); AddSecurity(); AddRepair(); AddRestorePoint(); AddRegistryBackup(); AddEventLog(); AddDebugger(); AddExplorer();
            var logTab = new TabPage("Журнал"); logBox = new TextBox { Multiline=true, ReadOnly=true, Dock=DockStyle.Fill, ScrollBars=ScrollBars.Both, Font=new Font("Consolas",9) }; logTab.Controls.Add(logBox); tabs.TabPages.Add(logTab);
            ApplyTheme();
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
            var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 4) };
            var bold = new Font("Segoe UI", 10.5f, FontStyle.Bold);
            liveLabel = new Label { AutoSize = true, Font = bold, Margin = new Padding(0, 6, 18, 0), Text = "● LIVE" };
            cpuLabel = new Label { AutoSize = true, Font = bold, Margin = new Padding(0, 6, 18, 0), Text = "CPU: —" };
            ramLabel = new Label { AutoSize = true, Font = bold, Margin = new Padding(0, 6, 18, 0), Text = "RAM: —" };
            eventLabel = new Label { AutoSize = true, Margin = new Padding(0, 7, 18, 0), ForeColor = Color.DimGray, Text = "Событий пока нет" };
            pauseButton = new Button { AutoSize = true, Text = "Пауза LIVE", Padding = new Padding(6, 1, 6, 1) };
            pauseButton.Click += (s, e) => SetLive(!live);
            bar.Controls.AddRange(new Control[] { liveLabel, cpuLabel, ramLabel, eventLabel, pauseButton });
            return bar;
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
            // Современная тёмная тема: единая палитра, плоские кнопки и читаемые таблицы.
            Color background = Color.FromArgb(18, 22, 30);
            Color surface = Color.FromArgb(27, 33, 44);
            Color surfaceAlt = Color.FromArgb(34, 42, 56);
            Color foreground = Color.FromArgb(232, 237, 245);
            Color muted = Color.FromArgb(156, 169, 188);
            Color accent = Color.FromArgb(74, 144, 226);

            Font = new Font("Segoe UI", 9.5f);
            BackColor = background;
            ForeColor = foreground;
            tabs.Font = new Font("Segoe UI", 9.5f);
            tabs.BackColor = background;
            tabs.ForeColor = foreground;
            StyleControls(this, background, surface, surfaceAlt, foreground, muted, accent);
        }

        void StyleControls(Control parent, Color background, Color surface, Color surfaceAlt,
            Color foreground, Color muted, Color accent)
        {
            foreach (Control c in parent.Controls)
            {
                c.Font = c is Button ? new Font("Segoe UI Semibold", 9.5f) : new Font("Segoe UI", 9.5f);
                if (c is TabPage)
                {
                    c.BackColor = background; c.ForeColor = foreground;
                }
                else if (c is ListView lv)
                {
                    lv.BackColor = surface; lv.ForeColor = foreground;
                    lv.BorderStyle = BorderStyle.None;
                    lv.Font = new Font("Segoe UI", 9f);
                }
                else if (c is TextBox tb)
                {
                    tb.BackColor = surface; tb.ForeColor = foreground;
                    tb.BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is Button b)
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderSize = 0;
                    b.FlatAppearance.MouseOverBackColor = accent;
                    b.BackColor = surfaceAlt; b.ForeColor = foreground;
                    b.Padding = new Padding(10, 6, 10, 6);
                    b.Cursor = Cursors.Hand;
                }
                else if (c is Label label)
                {
                    label.ForeColor = foreground;
                    if (label.Font.Size >= 12) label.Font = new Font("Segoe UI Semibold", label.Font.Size);
                }
                else if (c is Panel || c is FlowLayoutPanel || c is TableLayoutPanel || c is SplitContainer)
                {
                    c.BackColor = background; c.ForeColor = foreground;
                }
                else
                {
                    c.BackColor = background; c.ForeColor = foreground;
                }
                if (c.HasChildren) StyleControls(c, background, surface, surfaceAlt, foreground, muted, accent);
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
            bool system=IsLikelySystem(x.ProcessName)&&st.MemMb<250;
            bool heavy=st.MemMb>=500;
            bool busy=st.Cpu>=50;
            it.BackColor=heavy?Color.FromArgb(255,220,220):(busy?Color.FromArgb(255,238,205):(system?Color.FromArgb(218,245,225):Color.White));
            it.ForeColor=heavy?Color.DarkRed:(busy?Color.DarkOrange:(system?Color.DarkGreen:Color.FromArgb(35,42,52)));
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
                SortProcs();
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
                startupList.Items.Clear();
                foreach(var e in entries){
                    var it=new ListViewItem(e.Hive){Tag=e.Id};
                    it.SubItems.Add(e.Key);it.SubItems.Add(e.Name);it.SubItems.Add(e.Command);
                    bool sus=IsSuspiciousStartup(e);
                    it.BackColor=sus?Color.FromArgb(255,225,225):Color.FromArgb(225,245,230);
                    it.ForeColor=sus?Color.DarkRed:Color.DarkGreen;
                    startupList.Items.Add(it);
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
        void AddRestrictions(){var p=NewTab("Политики");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var l=List(p,new[]{"Область","Параметр","Значение"});l.BringToFront();f.BringToFront();string[] keys={"DisableTaskMgr","DisableRegistryTools","NoControlPanel","DisableCMD","NoDrives","DisallowRun"};Action refresh=()=>{l.Items.Clear();foreach(var n in keys)try{using(var k=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System")){var v=k?.GetValue(n);var it=new ListViewItem("HKCU Policies\\System");it.SubItems.Add(n);it.SubItems.Add(v==null?"не задан":v.ToString());l.Items.Add(it);}}catch{}};AddButton(f,"Проверить",refresh);AddButton(f,"Открыть редактор групповых политик",()=>Run("gpedit.msc",""));AddButton(f,"Экспорт отчёта",()=>{var s=string.Join(Environment.NewLine,l.Items.Cast<ListViewItem>().Select(x=>x.Text+" | "+x.SubItems[1].Text+" | "+x.SubItems[2].Text));using(var d=new SaveFileDialog{Filter="TXT|*.txt",FileName="policies.txt"})if(d.ShowDialog()==DialogResult.OK)File.WriteAllText(d.FileName,s);});refresh();}
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
        void AddDrivers(){var p=NewTab("Драйверы");var f=new FlowLayoutPanel{Dock=DockStyle.Top,AutoSize=true};p.Controls.Add(f);var t=new TextBox{Dock=DockStyle.Fill,Multiline=true,ScrollBars=ScrollBars.Both,Font=new Font("Consolas",9)};p.Controls.Add(t);t.BringToFront();f.BringToFront();AddButton(f,"Список драйверов",()=>t.Text=Cmd("driverquery /v /fo table",30000));AddButton(f,"Открыть диспетчер устройств",()=>Run("devmgmt.msc",""));}
        void AddSystemInfo()
        {
            var p = NewTab("Система");
            var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            p.Controls.Add(f);
            var t = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9) };
            p.Controls.Add(t); t.BringToFront(); f.BringToFront();
            AddButton(f, "Собрать сведения", () =>
            {
                t.Text = Cmd("systeminfo", 30000) + Environment.NewLine + Environment.NewLine
                    + "=== CPU ===" + Environment.NewLine + Cmd("powershell -NoProfile -Command \"Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name\"")
                    + "=== GPU ===" + Environment.NewLine + Cmd("powershell -NoProfile -Command \"Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name\"")
                    + "=== ДИСКИ ===" + Environment.NewLine + Cmd("powershell -NoProfile -Command \"Get-CimInstance Win32_LogicalDisk | Format-Table DeviceID,Size,FreeSpace -AutoSize\"");
            });
            AddButton(f, "Диспетчер задач", () => Run("taskmgr.exe", ""));
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

        // ================= DEBUGGER / RESTRICTIONS =================
        void AddDebugger()
        {
            var p = NewTab("Debugger");
            var f = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true };
            p.Controls.Add(f);
            var l = List(p, new[] { "Источник", "Параметр", "Значение", "Оценка" });
            l.Columns[0].Width = 260; l.Columns[1].Width = 180; l.Columns[2].Width = 520; l.Columns[3].Width = 150;
            l.BringToFront(); f.BringToFront();

            Action scan = () =>
            {
                l.Items.Clear();
                var roots = new[] {
                    (Registry.CurrentUser, "HKCU"),
                    (Registry.LocalMachine, "HKLM")
                };
                foreach (var root in roots)
                {
                    foreach (var path in new[] {
                        @"Software\Microsoft\Windows\CurrentVersion\Policies\System",
                        @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer",
                        @"Software\Microsoft\Windows\CurrentVersion\Policies\Windows"
                    })
                    {
                        try
                        {
                            using var key = root.Item1.OpenSubKey(path);
                            if (key == null) continue;
                            foreach (var name in key.GetValueNames())
                            {
                                string value = FmtReg(key.GetValue(name));
                                var it = new ListViewItem(root.Item2 + @"\" + path);
                                it.SubItems.Add(name); it.SubItems.Add(value);
                                it.SubItems.Add(IsRestrictionEnabled(name, value) ? "Ограничение включено" : "Проверить");
                                l.Items.Add(it);
                            }
                        }
                        catch (Exception ex) { Log("Сканирование политик: " + ex.Message); }
                    }
                }

                // Image File Execution Options: a Debugger value can redirect an executable.
                try
                {
                    using var ifeo = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options");
                    if (ifeo != null)
                    foreach (var subName in ifeo.GetSubKeyNames())
                    {
                        using var sub = ifeo.OpenSubKey(subName);
                        string debugger = Convert.ToString(sub?.GetValue("Debugger")) ?? "";
                        if (debugger.Length == 0) continue;
                        var it = new ListViewItem(@"HKLM\...\Image File Execution Options\" + subName);
                        it.SubItems.Add("Debugger"); it.SubItems.Add(debugger); it.SubItems.Add("Есть перенаправление — проверить");
                        it.BackColor = Color.FromArgb(255, 225, 225); it.ForeColor = Color.DarkRed; l.Items.Add(it);
                    }
                }
                catch (Exception ex) { Log("IFEO: " + ex.Message); }

                try
                {
                    using var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\USBSTOR");
                    string value = Convert.ToString(usb?.GetValue("Start")) ?? "не задан";
                    var it = new ListViewItem(@"HKLM\SYSTEM\CurrentControlSet\Services\USBSTOR");
                    it.SubItems.Add("Start"); it.SubItems.Add(value);
                    it.SubItems.Add(value == "4" ? "USB-накопители отключены" : "Обычно включено");
                    l.Items.Add(it);
                }
                catch { }
                Log("Сканирование политик, IFEO и USBSTOR завершено.");
            };

            AddButton(f, "Сканировать политики и блокировки", scan);
            AddButton(f, "Экспорт резервной копии .reg", () =>
            {
                using var d = new SaveFileDialog { Filter = "REG-файл|*.reg", FileName = "SimpleUnlocker_policies_backup.reg" };
                if (d.ShowDialog() != DialogResult.OK) return;
                string[] keys = {
                    "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies",
                    "HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies",
                    "HKLM\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options",
                    "HKLM\\SYSTEM\\CurrentControlSet\\Services\\USBSTOR",
                    "HKLM\\Software\\Policies\\Microsoft\\Windows Defender"
                };
                var output = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
                foreach (string key in keys)
                {
                    string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".reg");
                    try
                    {
                        using var proc = Process.Start(new ProcessStartInfo("reg.exe", "export \"" + key + "\" \"" + temp + "\" /y")
                        { UseShellExecute = false, CreateNoWindow = true });
                        proc?.WaitForExit(15000);
                        if (File.Exists(temp))
                        {
                            string data = File.ReadAllText(temp, Encoding.Unicode);
                            int first = data.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                            if (first >= 0) output.AppendLine(data.Substring(first + 4));
                        }
                    }
                    catch (Exception ex) { Log("Резервная копия " + key + ": " + ex.Message); }
                    finally { try { File.Delete(temp); } catch { } }
                }
                File.WriteAllText(d.FileName, output.ToString(), Encoding.Unicode);
                Log("Создана резервная копия политик: " + d.FileName);
            });
            AddButton(f, "Восстановить .reg", () =>
            {
                using var d = new OpenFileDialog { Filter = "REG-файл|*.reg", Title = "Выберите резервную копию реестра" };
                if (d.ShowDialog() != DialogResult.OK) return;
                if (!IsAdmin) { MessageBox.Show("Для восстановления может потребоваться запуск от администратора."); }
                if (Confirm("Импортировать выбранный REG-файл? Это изменит реестр Windows. Продолжить?"))
                    Run("reg.exe", "import \"" + d.FileName + "\"", true);
            });
            AddButton(f, "Открыть редактор реестра", () => Run("regedit.exe", ""));
            scan();
        }

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

        static bool IsRestrictionEnabled(string name, string value)
        {
            if (int.TryParse(value, out int n)) return n != 0;
            return name.Equals("DisallowRun", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RestrictRun", StringComparison.OrdinalIgnoreCase);
        }

        // ================= FILE EXPLORER =================
        void AddExplorer()
        {
            var p = NewTab("Проводник");
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            p.Controls.Add(bar);
            var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 260 };
            p.Controls.Add(split); split.BringToFront(); bar.BringToFront();
            var tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
            var files = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
            files.Columns.Add("Имя", 300); files.Columns.Add("Тип", 100); files.Columns.Add("Размер", 120); files.Columns.Add("Изменён", 150);
            split.Panel1.Controls.Add(tree); split.Panel2.Controls.Add(files);
            var pathBox = new TextBox { Width = 360 };
            bar.Controls.Add(new Label { Text = "Путь:", AutoSize = true, Padding = new Padding(0, 7, 0, 0) });
            bar.Controls.Add(pathBox);
            string current = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Action<string> load = path =>
            {
                if (!Directory.Exists(path)) { MessageBox.Show("Папка недоступна или не существует."); return; }
                current = Path.GetFullPath(path); pathBox.Text = current; files.Items.Clear();
                try
                {
                    foreach (var d in Directory.EnumerateDirectories(current).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var info = new DirectoryInfo(d); var it = new ListViewItem(info.Name) { Tag = d };
                        it.SubItems.Add("Папка"); it.SubItems.Add(""); it.SubItems.Add(info.LastWriteTime.ToString()); files.Items.Add(it);
                    }
                    foreach (var file in Directory.EnumerateFiles(current).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    {
                        var info = new FileInfo(file); var it = new ListViewItem(info.Name) { Tag = file };
                        it.SubItems.Add(info.Extension); it.SubItems.Add(info.Length.ToString("N0") + " Б"); it.SubItems.Add(info.LastWriteTime.ToString()); files.Items.Add(it);
                    }
                }
                catch (Exception ex) { Log("Проводник: " + ex.Message); }
            };
            Action addRoots = () =>
            {
                tree.Nodes.Clear();
                foreach (var drive in DriveInfo.GetDrives())
                {
                    var node = new TreeNode(drive.Name) { Tag = drive.Name };
                    node.Nodes.Add("…"); tree.Nodes.Add(node);
                }
                var home = new TreeNode("Профиль пользователя") { Tag = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
                home.Nodes.Add("…"); tree.Nodes.Add(home);
            };
            tree.BeforeExpand += (sender, e) =>
            {
                var node = e.Node; if (node.Nodes.Count == 1 && node.Nodes[0].Text == "…")
                {
                    node.Nodes.Clear();
                    try { foreach (var d in Directory.EnumerateDirectories(Convert.ToString(node.Tag) ?? "").Take(300))
                    { var child = new TreeNode(Path.GetFileName(d)) { Tag = d }; child.Nodes.Add("…"); node.Nodes.Add(child); } }
                    catch { }
                }
            };
            tree.AfterSelect += (sender, e) => load(Convert.ToString(e.Node.Tag) ?? "");
            files.DoubleClick += (sender, e) =>
            {
                if (files.SelectedItems.Count == 0) return;
                string target = Convert.ToString(files.SelectedItems[0].Tag) ?? "";
                if (Directory.Exists(target)) load(target);
                else if (File.Exists(target)) Run("explorer.exe", "\"" + target + "\"");
            };
            AddButton(bar, "Открыть", () => load(pathBox.Text));
            AddButton(bar, "Назад", () => { try { load(Directory.GetParent(current)?.FullName ?? current); } catch { } });
            AddButton(bar, "Обновить", () => load(current));
            AddButton(bar, "Создать папку", () =>
            {
                string name = PromptText("Имя новой папки:", "Создать папку", "Новая папка");
                if (string.IsNullOrWhiteSpace(name)) return;
                string dest = Path.Combine(current, name);
                if (Directory.Exists(dest) || File.Exists(dest)) { MessageBox.Show("Объект с таким именем уже существует."); return; }
                Directory.CreateDirectory(dest); load(current);
            });
            AddButton(bar, "Удалить", () =>
            {
                if (files.SelectedItems.Count == 0) return;
                string target = Convert.ToString(files.SelectedItems[0].Tag) ?? "";
                string fullTarget = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar);
                string windowsRoot = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows)).TrimEnd(Path.DirectorySeparatorChar);
                string systemRoot = Path.GetPathRoot(fullTarget) ?? "";
                if (fullTarget.Equals(windowsRoot, StringComparison.OrdinalIgnoreCase)
                    || fullTarget.Equals(systemRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
                    || fullTarget.Equals(Environment.GetFolderPath(Environment.SpecialFolder.System).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                { MessageBox.Show("Удаление корня диска и системных каталогов запрещено."); return; }
                if (!Confirm("Удалить выбранный объект? Операция может быть необратимой.")) return;
                if (Directory.Exists(target)) Directory.Delete(target, true); else if (File.Exists(target)) File.Delete(target);
                load(current);
            });
            AddButton(bar, "Открыть расположение", () => Run("explorer.exe", "\"" + current + "\""));
            addRoots(); pathBox.Text = current;
        }
    }
}