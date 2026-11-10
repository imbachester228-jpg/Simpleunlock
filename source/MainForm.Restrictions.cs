using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SimpleUnlockerRemastered
{
    // Вкладка «Ограничения»: объединяет бывшие вкладки «Политики» и «Debugger».
    public partial class MainForm
    {
        private sealed class Restr : ISuCheck
        {
            public bool Checked { get; set; }
            public string Kind = "policy";          // policy | ifeo | usb
            public RegistryHive Hive;
            public string HiveName = "", Path = "", Name = "", Value = "", Desc = "";
            public string FullKey { get { return HiveName + "\\" + Path; } }
        }

        // Параметры, которые считаются блокировками. Остальные значения в Policies не трогаем
        // (например, EnableLUA или ConsentPromptBehaviorAdmin — это настройки безопасности, а не ограничения).
        private static readonly Dictionary<string, string> KnownRestrictions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
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

        private sealed class RegTarget
        {
            public RegistryHive Hive; public string HiveName, Path; public bool InfoOnly;
            public RegTarget(RegistryHive h, string hn, string p, bool info) { Hive = h; HiveName = hn; Path = p; InfoOnly = info; }
        }

        private const string IfeoPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        private const string IfeoPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        private const string UsbStorPath = @"SYSTEM\CurrentControlSet\Services\USBSTOR";

        private SuListView restrList;
        private Label restrInfo;
        private Button restrShowAll;
        private bool restrShowAllValues;

        static string BackupDir
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleUnlocker", "Backups"); }
        }

        static bool IsRestrictionEnabled(string name, string value)
        {
            if (int.TryParse(value, out int n)) return n != 0;
            return name.Equals("DisallowRun", StringComparison.OrdinalIgnoreCase)
                || name.Equals("RestrictRun", StringComparison.OrdinalIgnoreCase);
        }

        static IEnumerable<RegTarget> PolicyTargets()
        {
            foreach (var h in new[] { new KeyValuePair<RegistryHive, string>(RegistryHive.CurrentUser, "HKCU"), new KeyValuePair<RegistryHive, string>(RegistryHive.LocalMachine, "HKLM") })
            {
                yield return new RegTarget(h.Key, h.Value, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", false);
                yield return new RegTarget(h.Key, h.Value, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", false);
                yield return new RegTarget(h.Key, h.Value, @"Software\Policies\Microsoft\Windows\System", false);
                yield return new RegTarget(h.Key, h.Value, @"Software\Microsoft\Windows\CurrentVersion\Policies\Windows", true);
            }
            yield return new RegTarget(RegistryHive.LocalMachine, "HKLM", @"Software\Policies\Microsoft\Windows Defender", false);
        }

        void AddRestrictions()
        {
            var p = NewTab("Ограничения");
            var bar = Bar();
            var l = MakeList(new[] { "", "Источник", "Параметр", "Значение", "Описание" });
            l.CheckColumn = true;
            l.Columns[0].Width = 46; l.Columns[1].Width = 340; l.Columns[2].Width = 190; l.Columns[3].Width = 190;
            l.FillColumn = 4;
            restrList = l;
            l.CheckChanged += (s, e) => UpdateRestrInfo();
            l.SelectedIndexChanged += (s, e) => UpdateRestrInfo();

            AddButton(bar, "Сканировать", ScanRestrictions, true);
            AddButton(bar, "Выбрать все", () => { l.SetAllChecked(true); });
            AddButton(bar, "Снять выбор", () => { l.SetAllChecked(false); });
            AddButton(bar, "Снять выбранные ограничения", RemoveSelectedRestrictions);
            restrShowAll = AddButton(bar, "Показать все значения политик", () =>
            {
                restrShowAllValues = !restrShowAllValues;
                restrShowAll.Text = restrShowAllValues ? "Только ограничения" : "Показать все значения политик";
                ScanRestrictions();
            });
            AddButton(bar, "Резервная копия .reg", ExportPoliciesBackup);
            AddButton(bar, "Восстановить из .reg", () =>
            {
                using (var d = new OpenFileDialog { Filter = "REG-файл|*.reg", Title = "Выберите резервную копию реестра" })
                {
                    if (d.ShowDialog() != DialogResult.OK) return;
                    if (!IsAdmin) MessageBox.Show("Для восстановления может потребоваться запуск от администратора.");
                    if (Confirm("Импортировать выбранный REG-файл? Это изменит реестр Windows. Продолжить?"))
                        Run("reg.exe", "import \"" + d.FileName + "\"", true);
                }
            });
            AddButton(bar, "Экспорт отчёта", () =>
            {
                var s = string.Join(Environment.NewLine, l.Items.Cast<ListViewItem>().Select(x => x.SubItems[1].Text + " | " + x.SubItems[2].Text + " | " + x.SubItems[3].Text + " | " + x.SubItems[4].Text));
                using (var d = new SaveFileDialog { Filter = "TXT|*.txt", FileName = "restrictions.txt" })
                    if (d.ShowDialog() == DialogResult.OK) File.WriteAllText(d.FileName, s, Encoding.UTF8);
            });
            AddButton(bar, "Групповые политики", () => Run("gpedit.msc", ""));
            AddButton(bar, "Редактор реестра", () => Run("regedit.exe", ""));

            restrInfo = new Label { AutoSize = true, ForeColor = Pal.Muted, Margin = new Padding(4, 8, 0, 0), Text = "Нажмите «Сканировать»" };
            bar.Controls.Add(restrInfo);

            Compose(p, bar, l);
            Lazy(p, ScanRestrictions);
        }

        void UpdateRestrInfo()
        {
            if (restrInfo == null || restrList == null) return;
            int total = 0, sel = 0;
            foreach (ListViewItem it in restrList.Items)
            {
                var r = it.Tag as Restr;
                if (r == null) continue;
                total++; if (r.Checked) sel++;
            }
            restrInfo.Text = "Найдено ограничений: " + total + "   ·   выбрано: " + sel;
            restrInfo.ForeColor = total == 0 ? Pal.Ok : (sel > 0 ? Pal.Accent : Pal.Muted);
        }

        void ScanRestrictions()
        {
            var l = restrList;
            l.BeginUpdate();
            try
            {
                l.Items.Clear();

                foreach (var t in PolicyTargets())
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(t.Hive, RegistryView.Registry64))
                        using (var key = baseKey.OpenSubKey(t.Path))
                        {
                            if (key == null) continue;
                            foreach (var name in key.GetValueNames())
                            {
                                string value = FmtReg(key.GetValue(name));
                                string desc;
                                bool known = KnownRestrictions.TryGetValue(name, out desc);
                                bool active = known && IsRestrictionEnabled(name, value);
                                if (active)
                                {
                                    var r = new Restr { Kind = "policy", Hive = t.Hive, HiveName = t.HiveName, Path = t.Path, Name = name, Value = value, Desc = desc };
                                    l.Items.Add(MakeRestrItem(r, false));
                                }
                                else if (restrShowAllValues)
                                {
                                    var it = new ListViewItem("");
                                    it.SubItems.Add(t.HiveName + "\\" + t.Path); it.SubItems.Add(name); it.SubItems.Add(value);
                                    it.SubItems.Add(known ? "Ограничение не активно" : "Прочий параметр политики (не трогаем)");
                                    it.ForeColor = Pal.Muted;
                                    l.Items.Add(it);
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log("Сканирование политик: " + ex.Message); }
                }

                // Image File Execution Options: значение Debugger перенаправляет запуск программы.
                foreach (var ifeo in new[] { IfeoPath, IfeoPath32 })
                {
                    try
                    {
                        using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                        using (var root = baseKey.OpenSubKey(ifeo))
                        {
                            if (root == null) continue;
                            foreach (var sub in root.GetSubKeyNames())
                            {
                                using (var k = root.OpenSubKey(sub))
                                {
                                    string dbg = Convert.ToString(k == null ? null : k.GetValue("Debugger")) ?? "";
                                    if (dbg.Length == 0) continue;
                                    var r = new Restr { Kind = "ifeo", Hive = RegistryHive.LocalMachine, HiveName = "HKLM", Path = ifeo + "\\" + sub, Name = "Debugger", Value = dbg,
                                        Desc = "Запуск «" + sub + "» перенаправлен на другую программу" };
                                    l.Items.Add(MakeRestrItem(r, true));
                                }
                            }
                        }
                    }
                    catch (Exception ex) { Log("IFEO: " + ex.Message); }
                }

                // USB-накопители отключены (Start = 4)
                try
                {
                    using (var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var usb = baseKey.OpenSubKey(UsbStorPath))
                    {
                        string v = Convert.ToString(usb == null ? null : usb.GetValue("Start")) ?? "";
                        if (v == "4")
                        {
                            var r = new Restr { Kind = "usb", Hive = RegistryHive.LocalMachine, HiveName = "HKLM", Path = UsbStorPath, Name = "Start", Value = v,
                                Desc = "USB-накопители отключены (будет значение 3 — включено)" };
                            l.Items.Add(MakeRestrItem(r, false));
                        }
                    }
                }
                catch (Exception ex) { Log("USBSTOR: " + ex.Message); }
            }
            finally { l.EndUpdate(); }

            UpdateRestrInfo();
            Log("Сканирование ограничений завершено.");
        }

        ListViewItem MakeRestrItem(Restr r, bool danger)
        {
            var it = new ListViewItem("") { Tag = r };
            it.SubItems.Add(r.HiveName + "\\" + r.Path);
            it.SubItems.Add(r.Name);
            it.SubItems.Add(r.Value);
            it.SubItems.Add(r.Desc);
            if (danger) { it.BackColor = Pal.BadBg; it.ForeColor = Pal.Bad; }
            return it;
        }

        void RemoveSelectedRestrictions()
        {
            var sel = restrList.Items.Cast<ListViewItem>().Select(i => i.Tag as Restr).Where(r => r != null && r.Checked).ToList();
            if (sel.Count == 0)
            {
                MessageBox.Show("Ничего не выбрано. Отметьте нужные ограничения галочками или нажмите «Выбрать все».", "Ограничения");
                return;
            }
            if (!IsAdmin && sel.Any(r => r.Hive == RegistryHive.LocalMachine))
            {
                MessageBox.Show("Для изменения HKLM нужны права администратора. Эти пункты будут пропущены, ограничения HKCU будут сняты.", "Ограничения");
                sel = sel.Where(r => r.Hive != RegistryHive.LocalMachine).ToList();
                if (sel.Count == 0) return;
            }
            if (sel.Any(r => r.Kind == "ifeo") &&
                !Confirm("Среди выбранных есть перенаправления запуска (IFEO Debugger). Некоторые из них могут быть нужны легитимным программам (например, замена диспетчера задач). Продолжить?"))
                return;
            if (!Confirm("Снять ограничений: " + sel.Count + "?\n\nПеред изменением в папке\n" + BackupDir + "\nбудет сохранена резервная копия .reg."))
                return;

            string backup = null;
            try
            {
                Directory.CreateDirectory(BackupDir);
                backup = System.IO.Path.Combine(BackupDir, "restrictions_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".reg");
                ExportRegKeys(sel.Select(r => r.FullKey).Distinct().ToList(), backup);
                Log("Резервная копия перед снятием ограничений: " + backup);
            }
            catch (Exception ex)
            {
                Log("Не удалось создать резервную копию: " + ex.Message);
                if (!Confirm("Резервную копию создать не удалось (" + ex.Message + "). Всё равно продолжить?")) return;
                backup = null;
            }

            int ok = 0, fail = 0;
            foreach (var r in sel)
            {
                try { RemoveRestriction(r); ok++; Log("Снято ограничение: " + r.FullKey + " → " + r.Name); }
                catch (Exception ex) { fail++; Log("Не удалось снять " + r.FullKey + " → " + r.Name + ": " + ex.Message); }
            }

            string msg = "Снято: " + ok + (fail > 0 ? ", ошибок: " + fail + " (см. «Журнал»)" : "") + ".";
            if (backup != null) msg += "\n\nРезервная копия:\n" + backup;
            msg += "\n\nЧасть изменений вступит в силу после перезапуска Проводника или повторного входа в систему. " +
                   "Если ограничение появляется снова, его возвращает групповая политика организации или другая программа.";
            MessageBox.Show(msg, "Готово");
            ScanRestrictions();
        }

        void RemoveRestriction(Restr r)
        {
            using (var baseKey = RegistryKey.OpenBaseKey(r.Hive, RegistryView.Registry64))
            using (var key = baseKey.OpenSubKey(r.Path, true))
            {
                if (key == null) throw new InvalidOperationException("Раздел не найден: " + r.FullKey);
                if (r.Kind == "usb") key.SetValue("Start", 3, RegistryValueKind.DWord);
                else key.DeleteValue(r.Name, false);
            }
        }

        void ExportPoliciesBackup()
        {
            using (var d = new SaveFileDialog { Filter = "REG-файл|*.reg", FileName = "SimpleUnlocker_policies_backup.reg" })
            {
                if (d.ShowDialog() != DialogResult.OK) return;
                string[] keys = {
                    "HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies",
                    "HKLM\\Software\\Microsoft\\Windows\\CurrentVersion\\Policies",
                    "HKCU\\Software\\Policies\\Microsoft\\Windows\\System",
                    "HKLM\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Image File Execution Options",
                    "HKLM\\SYSTEM\\CurrentControlSet\\Services\\USBSTOR",
                    "HKLM\\Software\\Policies\\Microsoft\\Windows Defender"
                };
                ExportRegKeys(keys, d.FileName);
                Log("Создана резервная копия политик: " + d.FileName);
            }
        }

        /// <summary>Экспортирует разделы через reg.exe и склеивает в один .reg-файл.</summary>
        void ExportRegKeys(IEnumerable<string> keys, string file)
        {
            var output = new StringBuilder("Windows Registry Editor Version 5.00\r\n\r\n");
            foreach (string key in keys)
            {
                string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".reg");
                try
                {
                    using (var proc = Process.Start(new ProcessStartInfo("reg.exe", "export \"" + key + "\" \"" + temp + "\" /y") { UseShellExecute = false, CreateNoWindow = true }))
                        if (proc != null) proc.WaitForExit(15000);
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
            File.WriteAllText(file, output.ToString(), Encoding.Unicode);
        }
    }
}
