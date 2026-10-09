using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("NOVA VPN Setup")]
[assembly: AssemblyProduct("NOVA VPN")]
[assembly: AssemblyVersion("2.3.17.0")]
[assembly: AssemblyFileVersion("2.3.17.0")]

internal sealed class NovaInstaller : Form
{
    private new const string ProductName = "NOVA VPN";
    private const string DisplayVersion = "2.3.17";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NOVA VPN";
    private readonly Color background = Color.FromArgb(233, 239, 246);
    private readonly Color panel = Color.FromArgb(248, 250, 253);
    private readonly Color panelLight = Color.FromArgb(237, 243, 249);
    private readonly Color accent = Color.FromArgb(18, 102, 165);
    private readonly Color muted = Color.FromArgb(82, 103, 124);
    private readonly Panel sidebar;
    private readonly Panel body;
    private readonly Panel footer;
    private readonly Label heading;
    private readonly Label subheading;
    private readonly Label status;
    private readonly TextBox pathBox;
    private readonly CheckBox desktopShortcut;
    private readonly CheckBox launchAfterInstall;
    private readonly ProgressBar progress;
    private readonly Button primary;
    private readonly Button secondary;
    private readonly BackgroundWorker worker;
    private string installPath;
    private bool launchApp;
    private readonly bool deleteInstallerOnExit;
    private readonly bool updateMode;
    private readonly int waitForProcessId;
    private int page;
    private string pendingError;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingName, string newName, int flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(uint eventId, uint flags, string item1, string item2);

    private NovaInstaller(string updatePath = null, int parentProcessId = 0, bool deleteSetupOnExit = false)
    {
        updateMode = !String.IsNullOrWhiteSpace(updatePath);
        deleteInstallerOnExit = deleteSetupOnExit;
        waitForProcessId = parentProcessId;
        Text = ProductName + " — установка";
        ClientSize = new Size(900, 590);
        MinimumSize = new Size(900, 590);
        MaximumSize = new Size(900, 590);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = background;
        ForeColor = Color.FromArgb(38, 56, 75);
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        // Position the content panel explicitly to the right of the sidebar.
        // A Fill-docked panel can retain an origin at x=0 depending on sibling
        // z-order, which makes its controls render underneath the sidebar.
        body = new Panel
        {
            Location = new Point(255, 0),
            Size = new Size(Math.Max(0, ClientSize.Width - 255), ClientSize.Height),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = background
        };
        Controls.Add(body);

        sidebar = new Panel { Dock = DockStyle.Left, Width = 255, BackColor = Color.FromArgb(229, 235, 243) };
        sidebar.Paint += PaintSidebar;
        Controls.Add(sidebar);
        try {
            using (Icon brandIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)) {
                sidebar.Controls.Add(new PictureBox { Location = new Point(25, 43), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom, Image = brandIcon.ToBitmap(), BackColor = Color.Transparent });
            }
        } catch { }
        AddSidebarText("NOVA", 28F, FontStyle.Bold, 82, 42, Color.FromArgb(38, 56, 75));
        AddSidebarText("PRIVATE NETWORK", 8F, FontStyle.Bold, 84, 80, muted);
        AddSidebarText("УСТАНОВКА", 8F, FontStyle.Bold, 32, 450, muted);
        AddSidebarText("Версия " + DisplayVersion, 9F, FontStyle.Regular, 32, 472, Color.FromArgb(38, 56, 75));
        AddSidebarText("VPN  ·  Маршрутизация  ·  Zapret", 8F, FontStyle.Regular, 32, 508, muted);

        footer = new Panel { Dock = DockStyle.Bottom, Height = 82, BackColor = background };
        body.Controls.Add(footer);
        footer.Paint += delegate(object sender, PaintEventArgs e) { using (Pen pen = new Pen(Color.FromArgb(196, 210, 224))) e.Graphics.DrawLine(pen, 26, 0, footer.Width - 26, 0); };

        heading = new Label { AutoSize = false, Location = new Point(35, 40), Size = new Size(580, 42), Font = new Font("Segoe UI Semibold", 21F), ForeColor = Color.FromArgb(38, 56, 75) };
        subheading = new Label { AutoSize = false, Location = new Point(37, 85), Size = new Size(580, 42), Font = new Font("Segoe UI", 9.5F), ForeColor = muted };
        body.Controls.Add(heading);
        body.Controls.Add(subheading);

        status = new Label { AutoSize = false, Location = new Point(38, 422), Size = new Size(575, 27), Font = new Font("Segoe UI", 9F), ForeColor = muted, Visible = false };
        progress = new ProgressBar { Location = new Point(38, 453), Size = new Size(575, 6), Style = ProgressBarStyle.Continuous, Visible = false, ForeColor = accent, BackColor = panelLight };
        body.Controls.Add(status);
        body.Controls.Add(progress);

        primary = MakeButton("Установить", true);
        primary.Size = new Size(154, 42);
        primary.Location = new Point(443, 20);
        primary.Click += PrimaryClick;
        secondary = MakeButton("Выход", false);
        secondary.Size = new Size(112, 42);
        secondary.Location = new Point(317, 20);
        secondary.Click += delegate { Close(); };
        footer.Controls.Add(primary);
        footer.Controls.Add(secondary);

        pathBox = new TextBox { Location = new Point(39, 193), Size = new Size(445, 31), Font = new Font("Segoe UI", 10F), BorderStyle = BorderStyle.FixedSingle, BackColor = panelLight, ForeColor = Color.FromArgb(38, 56, 75), Text = updateMode ? updatePath : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ProductName) };
        Button browse = MakeButton("Обзор…", false);
        browse.Location = new Point(495, 190);
        browse.Size = new Size(118, 36);
        browse.Name = "browse-button";
        browse.Click += BrowseClick;
        body.Controls.Add(pathBox);
        body.Controls.Add(browse);

        desktopShortcut = MakeCheckBox("Создать ярлык на рабочем столе", true);
        desktopShortcut.Location = new Point(40, 256);
        launchAfterInstall = MakeCheckBox("Запустить NOVA VPN после установки", false);
        if (updateMode) launchAfterInstall.Checked = true;
        launchAfterInstall.Location = new Point(40, 290);
        body.Controls.Add(desktopShortcut);
        body.Controls.Add(launchAfterInstall);

        worker = new BackgroundWorker { WorkerReportsProgress = true };
        worker.DoWork += InstallWorker;
        worker.ProgressChanged += WorkerProgressChanged;
        worker.RunWorkerCompleted += WorkerCompleted;
        FormClosing += delegate(object sender, FormClosingEventArgs e)
        {
            if (worker.IsBusy)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Дождитесь завершения установки.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        };
        ShowWelcome();
    }

    private void AddSidebarText(string value, float size, FontStyle style, int x, int y, Color color)
    {
        Label label = new Label { AutoSize = true, Text = value, Font = new Font("Segoe UI", size, style), ForeColor = color, Location = new Point(x, y), BackColor = Color.Transparent };
        sidebar.Controls.Add(label);
        label.BringToFront();
    }

    private void PaintSidebar(object sender, PaintEventArgs e)
    {
        using (LinearGradientBrush brush = new LinearGradientBrush(sidebar.ClientRectangle, Color.FromArgb(236, 241, 247), Color.FromArgb(224, 233, 243), 90F)) e.Graphics.FillRectangle(brush, sidebar.ClientRectangle);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (Pen ring = new Pen(Color.FromArgb(65, accent), 1.2F))
        {
            e.Graphics.DrawEllipse(ring, 142, 250, 225, 225);
            e.Graphics.DrawEllipse(ring, 164, 272, 181, 181);
            e.Graphics.DrawEllipse(ring, 190, 298, 129, 129);
        }
        using (SolidBrush glow = new SolidBrush(Color.FromArgb(25, accent))) e.Graphics.FillEllipse(glow, 211, 319, 87, 87);
        using (Pen divider = new Pen(Color.FromArgb(37, 45, 67))) e.Graphics.DrawLine(divider, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
    }

    private Button MakeButton(string text, bool filled)
    {
        Button button = new Button { Text = text, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", 9F), ForeColor = filled ? Color.White : Color.FromArgb(38, 56, 75), BackColor = filled ? accent : panelLight, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
        button.FlatAppearance.BorderSize = filled ? 0 : 1;
        button.FlatAppearance.BorderColor = Color.FromArgb(59, 69, 96);
        button.FlatAppearance.MouseOverBackColor = filled ? Color.FromArgb(34, 120, 184) : Color.FromArgb(220, 235, 247);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(194, 221, 241);
        return button;
    }

    private CheckBox MakeCheckBox(string text, bool isChecked)
    {
        return new CheckBox { Text = text, Checked = isChecked, AutoSize = true, Font = new Font("Segoe UI", 9.5F), ForeColor = Color.FromArgb(38, 56, 75), BackColor = background, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand };
    }

    private void ShowWelcome()
    {
        page = 0;
        heading.Text = updateMode ? "Обновление NOVA VPN" : "Установка NOVA VPN";
        subheading.Text = updateMode ? "Проверьте папку текущей установки и продолжите." : "Выберите папку и настройте параметры установки.";
        SetVisibility(pathBox, true);
        foreach (Control control in body.Controls) if (control.Name == "browse-button") control.Visible = true;
        SetVisibility(desktopShortcut, true);
        SetVisibility(launchAfterInstall, true);
        SetVisibility(status, false);
        SetVisibility(progress, false);
        SetButton(primary, updateMode ? "Обновить" : "Установить", true);
        SetButton(secondary, updateMode ? "Отмена" : "Выход", true);
        AddOrUpdateCard("package-card", new Rectangle(39, 350, 574, 56), "В комплекте  NOVA VPN " + DisplayVersion + "   ·   sing-box   ·   Zapret 1.10.3", "5 профилей оформления · строгий прямой маршрут сайтов и DNS. VPN-ключи в установщик не входят.");
    }

    private void ShowProgress()
    {
        page = 1;
        heading.Text = "Устанавливаем";
        subheading.Text = "Копируем файлы и регистрируем приложение в Windows.";
        SetVisibility(pathBox, false);
        foreach (Control control in body.Controls) if (control.Name == "browse-button") control.Visible = false;
        SetVisibility(desktopShortcut, false);
        SetVisibility(launchAfterInstall, false);
        SetVisibility(status, true);
        SetVisibility(progress, true);
        progress.Value = 0;
        status.Text = "Подготовка…";
        SetButton(primary, "Установка…", false);
        SetButton(secondary, "", false);
        secondary.Visible = false;
        primary.Enabled = false;
    }

    private void ShowFinished(string error)
    {
        page = 2;
        secondary.Visible = false;
        primary.Visible = true;
        primary.Enabled = true;
        SetButton(primary, error == null ? "Готово" : "Назад", true);
        primary.Click -= PrimaryClick;
        primary.Click += FinishClick;
        if (error == null)
        {
            heading.Text = "Готово!";
            subheading.Text = "NOVA VPN установлена и готова к работе.";
            status.Text = "Zapret 1.10.3 включён в комплект. Пользовательские настройки сохранены.";
            AddOrUpdateCard("package-card", new Rectangle(39, 238, 574, 86), "Установка завершена", "Для запуска VPN Windows может запросить подтверждение администратора.");
        }
        else
        {
            heading.Text = "Не удалось установить";
            subheading.Text = "Установщик не изменил данные профиля Windows.";
            status.Text = error;
        }
    }

    private void AddOrUpdateCard(string name, Rectangle bounds, string title, string detail)
    {
        Panel card = body.Controls.Cast<Control>().OfType<Panel>().FirstOrDefault(c => c.Name == name);
        if (card == null)
        {
            card = new Panel { Name = name, BackColor = panel, BorderStyle = BorderStyle.FixedSingle };
            Label titleLabel = new Label { Name = "title", AutoSize = false, Location = new Point(15, 9), Size = new Size(545, 20), Font = new Font("Segoe UI Semibold", 9F), ForeColor = Color.FromArgb(38, 56, 75), BackColor = panel };
            Label detailLabel = new Label { Name = "detail", AutoSize = false, Location = new Point(15, 31), Size = new Size(545, 20), Font = new Font("Segoe UI", 8F), ForeColor = muted, BackColor = panel };
            card.Controls.Add(titleLabel);
            card.Controls.Add(detailLabel);
            body.Controls.Add(card);
            card.BringToFront();
            heading.BringToFront();
            subheading.BringToFront();
            pathBox.BringToFront();
            foreach (Control control in body.Controls) if (control.Name == "browse-button") control.BringToFront();
            desktopShortcut.BringToFront();
            launchAfterInstall.BringToFront();
            status.BringToFront();
            progress.BringToFront();
        }
        card.Bounds = bounds;
        card.Controls["title"].Text = title;
        card.Controls["detail"].Text = detail;
    }

    private void SetVisibility(Control control, bool visible) { control.Visible = visible; }
    private void SetButton(Button button, string text, bool enabled) { button.Text = text; button.Enabled = enabled; }

    private void BrowseClick(object sender, EventArgs e)
    {
        using (FolderBrowserDialog dialog = new FolderBrowserDialog())
        {
            dialog.Description = "Выберите папку установки NOVA VPN";
            dialog.ShowNewFolderButton = true;
            string current = pathBox.Text.Trim();
            if (Directory.Exists(current)) dialog.SelectedPath = current;
            else if (Directory.Exists(Path.GetDirectoryName(current))) dialog.SelectedPath = Path.GetDirectoryName(current);
            if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Text = dialog.SelectedPath;
        }
    }

    private void PrimaryClick(object sender, EventArgs e)
    {
        if (page == 2) { Close(); return; }
        string target;
        try { target = ValidateTarget(pathBox.Text); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (updateMode && !WaitForPreviousProcess()) return;
        if (Process.GetProcessesByName("NOVA VPN").Any())
        {
            MessageBox.Show(this, "Перед обновлением закройте NOVA VPN и повторите установку.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        installPath = target;
        launchApp = launchAfterInstall.Checked;
        ShowProgress();
        worker.RunWorkerAsync(new InstallOptions { Target = installPath, DesktopShortcut = desktopShortcut.Checked });
    }

    private bool WaitForPreviousProcess()
    {
        if (waitForProcessId <= 0) return true;
        try
        {
            using (Process previous = Process.GetProcessById(waitForProcessId))
            {
                if (!previous.WaitForExit(30000))
                {
                    MessageBox.Show(this, "NOVA VPN ещё завершает работу. Повторите обновление через несколько секунд.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        return true;
    }

    private void FinishClick(object sender, EventArgs e)
    {
        if (pendingError != null) { pendingError = null; primary.Click -= FinishClick; primary.Click += PrimaryClick; secondary.Visible = true; ShowWelcome(); return; }
        if (launchApp)
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(installPath, ProductName + ".exe")) { UseShellExecute = true, Verb = "runas", WorkingDirectory = installPath }); }
            catch (Exception ex) { MessageBox.Show(this, "Приложение установлено, но не запустилось: " + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information); }
        }
        Close();
    }

    private string ValidateTarget(string value)
    {
        if (String.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Укажите папку установки.");
        string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string root = Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (String.Equals(full, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Выберите папку внутри диска, например C:\\Program Files\\NOVA VPN.");
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
        if (full.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Не устанавливайте приложение внутрь папки Windows.");
        return full;
    }

    private void InstallWorker(object sender, DoWorkEventArgs e)
    {
        InstallOptions options = (InstallOptions)e.Argument;
        Directory.CreateDirectory(options.Target);
        List<string> installed = new List<string>();
        string prefix = Path.GetFullPath(options.Target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using (Stream file = Assembly.GetExecutingAssembly().GetManifestResourceStream("NOVA.Payload.zip"))
        {
        if (file == null) throw new FileNotFoundException("В установщике не найден архив компонентов.");
        using (ZipArchive archive = new ZipArchive(file, ZipArchiveMode.Read))
        {
            long total = Math.Max(1, archive.Entries.Sum(x => x.Length));
            long complete = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 || relative.IndexOf('\0') >= 0) throw new InvalidDataException("Архив содержит небезопасный путь.");
                string destination = Path.GetFullPath(Path.Combine(options.Target, relative));
                if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Архив содержит путь вне папки установки.");
                if (String.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                using (Stream input = entry.Open())
                using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[65536];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        complete += read;
                        int percent = (int)Math.Min(100, complete * 100 / total);
                        worker.ReportProgress(percent, "Установка компонентов… " + percent + "%");
                    }
                }
                installed.Add(relative);
            }
        }
        }

        string installerDestination = Path.Combine(options.Target, "NOVA VPN Uninstaller.exe");
        using (Stream uninstaller = Assembly.GetExecutingAssembly().GetManifestResourceStream("NOVA.Uninstaller.exe"))
        {
            if (uninstaller == null) throw new FileNotFoundException("В установщике не найден компонент удаления.");
            using (FileStream output = new FileStream(installerDestination, FileMode.Create, FileAccess.Write, FileShare.None)) uninstaller.CopyTo(output);
        }
        installed.Add("NOVA VPN Uninstaller.exe");
        string manifest = Path.Combine(options.Target, "install-manifest.txt");
        installed.Add("install-manifest.txt");
        File.WriteAllLines(manifest, installed.Distinct(StringComparer.OrdinalIgnoreCase), new UTF8Encoding(false));

        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName);
        Directory.CreateDirectory(startMenu);
        CreateShortcut(Path.Combine(startMenu, ProductName + ".lnk"), Path.Combine(options.Target, ProductName + ".exe"), options.Target);
        CreateShortcut(Path.Combine(startMenu, "Удалить " + ProductName + ".lnk"), installerDestination, options.Target, "--uninstall");
        string desktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), ProductName + ".lnk");
        if (options.DesktopShortcut || File.Exists(desktopLink)) CreateShortcut(desktopLink, Path.Combine(options.Target, ProductName + ".exe"), options.Target);
        string personalDesktopLink = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProductName + ".lnk");
        if (File.Exists(personalDesktopLink)) CreateShortcut(personalDesktopLink, Path.Combine(options.Target, ProductName + ".exe"), options.Target);

        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", ProductName, RegistryValueKind.String);
            key.SetValue("DisplayVersion", DisplayVersion, RegistryValueKind.String);
            key.SetValue("Publisher", "NOVA Project", RegistryValueKind.String);
            key.SetValue("InstallLocation", options.Target, RegistryValueKind.String);
            key.SetValue("DisplayIcon", Path.Combine(options.Target, ProductName + ".exe"), RegistryValueKind.String);
            key.SetValue("UninstallString", "\"" + installerDestination + "\" --uninstall", RegistryValueKind.String);
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("DesktopShortcut", options.DesktopShortcut ? 1 : 0, RegistryValueKind.DWord);
            long installedBytes = installed.Where(File.Exists).Sum(relative => new FileInfo(Path.Combine(options.Target, relative)).Length);
            key.SetValue("EstimatedSize", (int)Math.Min(Int32.MaxValue, installedBytes / 1024), RegistryValueKind.DWord);
        }
    }

    private void WorkerProgressChanged(object sender, ProgressChangedEventArgs e)
    {
        progress.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
        status.Text = e.UserState as string ?? "Установка компонентов…";
    }

    private void WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
    {
        if (e.Error != null)
        {
            pendingError = e.Error.GetBaseException().Message;
            ShowFinished(pendingError);
            return;
        }
        ShowFinished(null);
    }

    private static void CreateShortcut(string path, string target, string workingDirectory, string arguments = "")
    {
        Type shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) throw new InvalidOperationException("В Windows недоступно создание ярлыков.");
        object shell = Activator.CreateInstance(shellType);
        object shortcut = null;
        try
        {
            shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
            Type type = shortcut.GetType();
            type.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { target });
            type.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDirectory });
            string brandIconPath = Path.Combine(workingDirectory, "Assets", "nova-v2.ico");
            string iconPath = File.Exists(brandIconPath) ? brandIconPath : target;
            // IconLocation is a shell property, not a command line: literal quotes
            // become part of the filename and make Explorer display a blank icon.
            type.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { iconPath + ",0" });
            type.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, new object[] { arguments });
            type.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            SHChangeNotify(0x00002000, 0x0005, path, null); // UPDATEITEM / PATHW
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static void Uninstall()
    {
        string current = Application.ExecutablePath;
        string install = Path.GetDirectoryName(current);
        DialogResult confirm = MessageBox.Show("Удалить NOVA VPN и её компоненты?" + Environment.NewLine + Environment.NewLine +
            "Личные настройки в профиле Windows сохранятся.", ProductName, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;
        if (Process.GetProcessesByName("NOVA VPN").Any())
        {
            MessageBox.Show("Сначала закройте NOVA VPN, затем повторите удаление.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            bool removeDesktopShortcut = false;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(UninstallKey, true))
            {
                if (key != null)
                {
                    removeDesktopShortcut = Convert.ToInt32(key.GetValue("DesktopShortcut", 0)) == 1;
                    Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false);
                }
            }
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProductName);
            TryDeleteFile(Path.Combine(startMenu, ProductName + ".lnk"));
            TryDeleteFile(Path.Combine(startMenu, "Удалить " + ProductName + ".lnk"));
            try { Directory.Delete(startMenu, false); } catch { }
            if (removeDesktopShortcut) TryDeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), ProductName + ".lnk"));

            string manifest = Path.Combine(install, "install-manifest.txt");
            if (!File.Exists(manifest)) throw new FileNotFoundException("Не найден список файлов установки; для безопасности файлы не удалены.");
            if (File.Exists(manifest))
            {
                string[] installed = File.ReadAllLines(manifest, Encoding.UTF8);
                foreach (string relative in installed)
                {
                    string path = SafeInstallPath(install, relative);
                    if (File.Exists(path)) TryDeleteFile(path);
                }
                string[] directories = installed.Select(relative => Path.GetDirectoryName(SafeInstallPath(install, relative)))
                    .Where(path => !String.IsNullOrEmpty(path)).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(path => path.Length).ToArray();
                foreach (string directory in directories) { try { Directory.Delete(directory, false); } catch { } }
            }
            try { Directory.Delete(install, false); } catch { }
            MessageBox.Show("NOVA VPN удалена. Настройки профиля Windows оставлены на месте.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (File.Exists(current)) MoveFileEx(current, null, 4);
            if (Directory.Exists(install)) MoveFileEx(install, null, 4);
        }
        catch (Exception ex) { MessageBox.Show("Не удалось полностью удалить NOVA VPN: " + ex.Message, ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private static string SafeInstallPath(string root, string relative)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, relative.TrimStart('\\', '/')));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Манифест удаления содержит путь за пределами приложения.");
        return candidate;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        args = NormalizeLegacyUpdateArguments(args);
        bool uninstall = args.Any(x => String.Equals(x, "--uninstall", StringComparison.OrdinalIgnoreCase));
        if (uninstall) { Uninstall(); return; }
        string updatePath = ArgumentValue(args, "--update-path");
        int parentProcessId = 0;
        Int32.TryParse(ArgumentValue(args, "--wait-pid"), out parentProcessId);
        bool deleteSetupOnExit = args.Any(x => String.Equals(x, "--delete-self-on-reboot", StringComparison.OrdinalIgnoreCase));
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            MessageBox.Show("Для установки компонентов NOVA VPN требуется подтверждение администратора.", ProductName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        NovaInstaller window = new NovaInstaller(updatePath, parentProcessId, deleteSetupOnExit);
        Application.Run(window);
        if (window.deleteInstallerOnExit) MoveFileEx(Application.ExecutablePath, null, 4);
    }

    private static string ArgumentValue(string[] args, string name)
    {
        for (int i = 0; args != null && i + 1 < args.Length; i++)
            if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1].Trim('"');
        return null;
    }

    private static string[] NormalizeLegacyUpdateArguments(string[] args)
    {
        // Older clients quoted a directory ending in a single backslash.
        // Windows therefore absorbed the closing quote and the following flags.
        // Recover only this exact known update invocation, never arbitrary input.
        if (args == null || args.Length != 2 || args[0] != "--update-path") return args;
        var match = System.Text.RegularExpressions.Regex.Match(args[1],
            "^(?<path>[^\\\"]+)\\\" --wait-pid (?<pid>[0-9]+) --delete-self-on-reboot$");
        int pid;
        if (!match.Success || !Int32.TryParse(match.Groups["pid"].Value, out pid) || pid <= 0) return args;
        string path = match.Groups["path"].Value;
        if (!Path.IsPathRooted(path)) return args;
        return new[] { "--update-path", path, "--wait-pid", pid.ToString(), "--delete-self-on-reboot" };
    }
}

internal sealed class InstallOptions
{
    public string Target;
    public bool DesktopShortcut;
}
