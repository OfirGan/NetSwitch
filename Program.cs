using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Microsoft.Win32;

namespace NetSwitch;

internal static class Program
{
    internal static string AppVersion { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
    internal static Icon AppIcon { get; } = new(Path.Combine(AppContext.BaseDirectory, "NetSwitch-icon01.ico"));

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApplicationContext());
    }
}

// Keep the process alive from the notification area and create the main window only when requested.
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private MainForm? _mainForm;
    private List<NetworkInterface>? _adapterSnapshot;
    private string? _adapterDiscoveryError;
    private bool _adapterRefreshInProgress;

    public TrayApplicationContext()
    {
        _trayMenu = new ContextMenuStrip { AutoClose = true };
        _trayMenu.Opening += (_, _) => OpenTrayMenu();
        _trayIcon = new NotifyIcon
        {
            Icon = Program.AppIcon,
            Text = "NetSwitch - network profiles",
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == MouseButtons.Left)
            {
                ToggleMainWindow();
            }
        };

        try
        {
            if (!ProfileStorage.LoadAdapterSettings().StartMinimizedToTray)
            {
                OpenMainWindow();
            }
        }
        catch
        {
            OpenMainWindow();
        }
    }

    private void OpenTrayMenu()
    {
        PopulateTrayMenu(_adapterSnapshot, _adapterDiscoveryError);
        if (!_adapterRefreshInProgress)
        {
            _ = RefreshTrayAdaptersAsync();
        }
    }

    private void ToggleMainWindow()
    {
        if (_mainForm is { IsDisposed: false } form)
        {
            if (form.Visible)
            {
                form.Hide();
                return;
            }
        }

        OpenMainWindow();
    }

    private void PopulateTrayMenu(IReadOnlyList<NetworkInterface>? adapters, string? adapterError)
    {
        _trayMenu.Items.Clear();

        try
        {
            var profiles = ProfileStorage.Load();
            var hiddenIds = ProfileStorage.LoadAdapterSettings().HiddenAdapterIds;
            var visibleAdapters = (adapters ?? Array.Empty<NetworkInterface>())
                .Where(adapter => !hiddenIds.Contains(adapter.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (profiles.Count == 0)
            {
                _trayMenu.Items.Add(new ToolStripMenuItem("No saved profiles") { Enabled = false });
            }

            foreach (var profile in profiles.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var profileItem = new ToolStripMenuItem(FormatProfileMenuName(profile));
                if (adapterError is not null)
                {
                    profileItem.DropDownItems.Add(new ToolStripMenuItem("Could not load IPv4 adapters") { Enabled = false });
                }
                else if (adapters is null)
                {
                    profileItem.DropDownItems.Add(new ToolStripMenuItem("Loading IPv4 adapters...") { Enabled = false });
                }
                else if (visibleAdapters.Count == 0)
                {
                    profileItem.DropDownItems.Add(new ToolStripMenuItem("No visible IPv4 adapters") { Enabled = false });
                }
                else
                {
                    foreach (var adapter in visibleAdapters)
                    {
                        var targetItem = new ToolStripMenuItem(new AdapterOption(adapter).ToString());
                        targetItem.Click += async (_, _) => await ApplyFromTrayAsync(profile, adapter);
                        profileItem.DropDownItems.Add(targetItem);
                    }
                }
                _trayMenu.Items.Add(profileItem);
            }
        }
        catch (Exception ex)
        {
            _trayMenu.Items.Add(new ToolStripMenuItem("Could not load profiles") { Enabled = false });
            MessageBox.Show($"Could not load saved profiles: {ex.Message}", "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        if (_trayMenu.Items.Count > 0)
        {
            _trayMenu.Items.Add(new ToolStripSeparator());
        }
        _trayMenu.Items.Add(new ToolStripMenuItem("Open NetSwitch", null, (_, _) => OpenMainWindow()));
        _trayMenu.Items.Add(new ToolStripMenuItem("Choose visible adapters...", null, async (_, _) => await ConfigureAdapterFilterAsync(null)));
        _trayMenu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
    }

    private static string FormatProfileMenuName(NetworkProfile profile)
    {
        if (profile.UseDhcpAddress)
        {
            return $"{profile.Name} (DHCP)";
        }

        var prefixLength = GetPrefixLength(profile.SubnetMask);
        var addressSummary = prefixLength.HasValue
            ? $"{profile.IpAddress} / {prefixLength.Value}"
            : profile.IpAddress;
        var additionalIpCount = Math.Max(0, global::NetSwitch.MainForm.GetProfileIpAddresses(profile).Count - 1);
        var additionalSummary = additionalIpCount > 0 ? $" (+{additionalIpCount})" : string.Empty;
        return $"{profile.Name} ({addressSummary}){additionalSummary}";
    }

    private static int? GetPrefixLength(string subnetMask)
    {
        if (!IPAddress.TryParse(subnetMask, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var prefixLength = 0;
        var zeroBitFound = false;
        foreach (var octet in address.GetAddressBytes())
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var isSet = (octet & (1 << bit)) != 0;
                if (isSet && zeroBitFound)
                {
                    return null;
                }
                if (isSet)
                {
                    prefixLength++;
                }
                else
                {
                    zeroBitFound = true;
                }
            }
        }

        return prefixLength;
    }

    private async Task RefreshTrayAdaptersAsync()
    {
        _adapterRefreshInProgress = true;
        try
        {
            _adapterSnapshot = await Task.Run(GetTargetAdapters);
            _adapterDiscoveryError = null;
        }
        catch (Exception ex)
        {
            _adapterDiscoveryError = ex.Message;
        }
        finally
        {
            _adapterRefreshInProgress = false;
            if (!_trayMenu.IsDisposed && _trayMenu.IsHandleCreated)
            {
                _trayMenu.BeginInvoke((Action)(() =>
                {
                    if (!_trayMenu.IsDisposed && _trayMenu.Visible)
                    {
                        PopulateTrayMenu(_adapterSnapshot, _adapterDiscoveryError);
                    }
                }));
            }
        }
    }

    internal static List<NetworkInterface> GetTargetAdapters()
    {
        var adapters = new List<NetworkInterface>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }
            try
            {
                if (adapter.GetIPProperties().GetIPv4Properties() is not null)
                {
                    adapters.Add(adapter);
                }
            }
            catch (NetworkInformationException)
            {
                // Skip an adapter that Windows cannot query right now.
            }
        }
        return adapters;
    }

    internal static async Task ConfigureAdapterFilterAsync(IWin32Window? owner)
    {
        try
        {
            var adapters = await Task.Run(GetTargetAdapters);
            var settings = ProfileStorage.LoadAdapterSettings();
            using var dialog = new AdapterVisibilityForm(adapters, settings.HiddenAdapterIds);
            if (dialog.ShowDialog(owner) == DialogResult.OK)
            {
                settings.HiddenAdapterIds = dialog.HiddenAdapterIds;
                ProfileStorage.SaveAdapterSettings(settings);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, $"Could not update tray adapter visibility: {ex.Message}", "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task ApplyFromTrayAsync(NetworkProfile profile, NetworkInterface adapter)
    {
        if (!global::NetSwitch.MainForm.TryValidateProfile(profile, out var validationMessage))
        {
            MessageBox.Show(validationMessage, "Invalid profile", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var addressSummary = profile.UseDhcpAddress
            ? "IP address: DHCP"
            : $"IP addresses: {string.Join(", ", global::NetSwitch.MainForm.GetProfileIpAddresses(profile))}";
        var dnsSummary = profile.UseDhcpDns ? "DNS: DHCP" : $"DNS: {string.Join(", ", profile.DnsServers)}";
        var suffixSummary = string.IsNullOrWhiteSpace(profile.DnsSuffix) ? string.Empty : $"\nDNS suffix: {profile.DnsSuffix}";
        var answer = MessageBox.Show(
            $"Apply '{profile.Name}' to '{adapter.Name}'?\n\n{addressSummary}\n{dnsSummary}{suffixSummary}\n\nThe adapter may disconnect temporarily.",
            "Apply network profile",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            await global::NetSwitch.MainForm.ApplyProfileAsync(profile, adapter.Name);
            _trayIcon.ShowBalloonTip(2500, "NetSwitch", $"'{profile.Name}' applied to {adapter.Name}.", ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not apply '{profile.Name}' to '{adapter.Name}':\n{ex.Message}", "NetSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenMainWindow()
    {
        if (_mainForm is null || _mainForm.IsDisposed)
        {
            _mainForm = new MainForm();
            _mainForm.FormClosed += (_, _) => _mainForm = null;
        }
        if (!_mainForm.Visible)
        {
            _mainForm.Show();
        }
        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }
        _mainForm.Activate();
    }

    protected override void ExitThreadCore()
    {
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        var mainForm = _mainForm;
        _mainForm = null;
        if (mainForm is not null)
        {
            mainForm.CloseForExit();
            mainForm.Dispose();
        }
        base.ExitThreadCore();
    }
}

// One screen owns the profile editor, local JSON file, adapter discovery, and apply actions.
internal sealed class MainForm : Form
{
    private const string StartupRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "NetSwitch";
    private const string StartupTaskName = "NetSwitch Startup";
    private readonly ComboBox _adapterBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ListBox _profileList = new();
    private readonly TextBox _profileNameBox = new();
    private readonly CheckBox _startupCheck = new() { Text = "Start with Windows", ForeColor = Color.White, Dock = DockStyle.Fill };
    private readonly CheckBox _startMinimizedCheck = new() { Text = "Start minimized to tray", ForeColor = Color.White, Dock = DockStyle.Fill };
    private readonly CheckBox _dhcpAddressCheck = new() { Text = "Obtain IP address automatically (DHCP)" };
    private readonly CheckBox _dhcpDnsCheck = new() { Text = "Obtain DNS server automatically" };
    private readonly Ipv4AddressBox _ipBox = new();
    private Button _optionalIpButton = new();
    private readonly List<OptionalIpConfiguration> _optionalIpAddresses = new();
    private readonly Ipv4AddressBox _maskBox = new();
    private readonly Ipv4AddressBox _gatewayBox = new();
    private readonly Ipv4AddressBox _primaryDnsBox = new();
    private readonly Ipv4AddressBox _alternateDnsBox = new();
    private readonly TextBox _dnsSuffixBox = new();
    private readonly Label _statusLabel = new();
    private readonly ToolTip _startupToolTip = new();
    private readonly List<NetworkProfile> _profiles = new();
    private bool _updatingStartupCheck;
    private bool _updatingStartMinimizedCheck;
    private NetworkProfile? _renamingProfile;
    private bool _exitRequested;

    public MainForm()
    {
        Text = $"NetSwitch {Program.AppVersion}";
        Icon = Program.AppIcon;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 640);
        Size = new Size(1080, 640);
        BackColor = Color.FromArgb(239, 244, 241);
        Font = new Font("Segoe UI", 9F);

        BuildInterface();
        try
        {
            var taskRegistered = IsScheduledStartupRegistered();
            var legacyRunEntry = IsLegacyStartupRegistered();
            _startupCheck.Checked = taskRegistered || legacyRunEntry;
            if (!taskRegistered && legacyRunEntry)
            {
                _ = UpdateStartupSettingAsync();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read startup setting: {ex.Message}", true);
        }
        _startupCheck.CheckedChanged += async (_, _) => await UpdateStartupSettingAsync();
        _startupToolTip.SetToolTip(_startupCheck, "Runs at sign-in with highest privileges using Task Scheduler.");
        try
        {
            _startMinimizedCheck.Checked = ProfileStorage.LoadAdapterSettings().StartMinimizedToTray;
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read startup display setting: {ex.Message}", true);
        }
        _startMinimizedCheck.CheckedChanged += (_, _) => SaveStartMinimizedSetting();
        _startupToolTip.SetToolTip(_startMinimizedCheck, "When checked, launch with only the tray icon. Otherwise, open the main window.");
        _dhcpAddressCheck.CheckedChanged += (_, _) => UpdateFieldAvailability();
        _dhcpDnsCheck.CheckedChanged += (_, _) => UpdateFieldAvailability();
        FormClosed += (_, _) => _startupToolTip.Dispose();
        Load += (_, _) => InitializeApp();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    internal void CloseForExit()
    {
        _exitRequested = true;
        Close();
    }

    // Build the header, profile column, settings area, and status line in one window.
    private void BuildInterface()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(18),
            BackColor = BackColor
        };
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Color.FromArgb(23, 73, 65), Padding = new Padding(20, 12, 16, 8) };
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 560));
        var headerTitle = new Panel { Dock = DockStyle.Fill };
        headerTitle.Controls.Add(new Label
        {
            Text = "NetSwitch",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 19F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(18, 8)
        });
        headerTitle.Controls.Add(new Label
        {
            Text = "Network adapter profiles",
            ForeColor = Color.FromArgb(206, 226, 218),
            AutoSize = true,
            Location = new Point(21, 48)
        });
        header.Controls.Add(headerTitle, 0, 0);
        header.SetRowSpan(headerTitle, 3);
        var headerActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 182));
        headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        headerActions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        headerActions.Controls.Add(_startupCheck, 0, 0);
        headerActions.Controls.Add(_startMinimizedCheck, 1, 0);
        var filterButton = MakeButton("Tray adapters...", Color.FromArgb(231, 239, 235), Color.FromArgb(28, 73, 63));
        filterButton.Margin = new Padding(0);
        filterButton.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
        filterButton.Click += async (_, _) => await TrayApplicationContext.ConfigureAdapterFilterAsync(this);
        headerActions.Controls.Add(filterButton, 2, 0);
        header.Controls.Add(headerActions, 1, 1);
        shell.Controls.Add(header, 0, 0);

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = BackColor, Margin = new Padding(0, 14, 0, 8) };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 286));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.Controls.Add(BuildProfilePane(), 0, 0);
        content.Controls.Add(BuildSettingsPane(), 1, 0);
        shell.Controls.Add(content, 0, 1);

        _statusLabel.Text = "Ready";
        _statusLabel.ForeColor = Color.FromArgb(70, 85, 79);
        _statusLabel.Dock = DockStyle.Fill;
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        shell.Controls.Add(_statusLabel, 0, 2);
        Controls.Add(shell);
    }

    private Control BuildProfilePane()
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(14), Margin = new Padding(0, 0, 12, 0) };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));

        layout.Controls.Add(SectionLabel("SAVED PROFILES"), 0, 0);
        _profileList.Dock = DockStyle.Fill;
        _profileList.IntegralHeight = false;
        _profileList.BorderStyle = BorderStyle.FixedSingle;
        _profileList.Font = new Font("Segoe UI", 10F);
        _profileList.MouseDoubleClick += (_, eventArgs) =>
        {
            var itemIndex = _profileList.IndexFromPoint(eventArgs.Location);
            if (itemIndex >= 0)
            {
                _profileList.SelectedIndex = itemIndex;
                BeginRenamingSelectedProfile();
            }
        };
        layout.Controls.Add(_profileList, 0, 1);
        layout.Controls.Add(new Label { Text = "Profile name", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, ForeColor = Color.FromArgb(70, 85, 79) }, 0, 2);
        _profileNameBox.Dock = DockStyle.Fill;
        _profileNameBox.Margin = new Padding(0, 3, 0, 5);
        layout.Controls.Add(_profileNameBox, 0, 3);

        var loadButton = MakeButton("Load selected", Color.FromArgb(231, 239, 235), Color.FromArgb(28, 73, 63));
        loadButton.Click += (_, _) => LoadSelectedProfile();
        layout.Controls.Add(loadButton, 0, 4);
        var saveButton = MakeButton("Save profile", Color.FromArgb(23, 104, 85), Color.White);
        saveButton.Click += (_, _) => SaveProfile();
        layout.Controls.Add(saveButton, 0, 5);
        var deleteButton = MakeButton("Delete selected", Color.FromArgb(247, 235, 231), Color.FromArgb(132, 54, 43));
        deleteButton.Click += (_, _) => DeleteSelectedProfile();
        layout.Controls.Add(deleteButton, 0, 6);
        var clearButton = MakeButton("Clear fields", Color.FromArgb(231, 239, 235), Color.FromArgb(28, 73, 63));
        clearButton.Click += (_, _) => ClearEditorFields();
        layout.Controls.Add(clearButton, 0, 7);

        panel.Controls.Add(layout);
        return panel;
    }

    private static bool IsScheduledStartupRegistered()
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("/Query");
        process.StartInfo.ArgumentList.Add("/TN");
        process.StartInfo.ArgumentList.Add(StartupTaskName);
        return process.Start() && WaitForExitSuccessfully(process);
    }

    private static bool WaitForExitSuccessfully(Process process)
    {
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static bool IsLegacyStartupRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey);
        var command = key?.GetValue(StartupValueName) as string;
        return string.Equals(command?.Trim().Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
    }

    private async Task UpdateStartupSettingAsync()
    {
        if (_updatingStartupCheck)
        {
            return;
        }

        var enableStartup = _startupCheck.Checked;
        try
        {
            if (enableStartup)
            {
                await RunStartupTaskCommandAsync(
                    "/Create", "/TN", StartupTaskName,
                    "/TR", $"\"{Application.ExecutablePath}\"",
                    "/SC", "ONLOGON", "/RL", "HIGHEST", "/F", "/IT");
                RemoveLegacyStartupEntry();
                SetStatus("Start with Windows enabled using an elevated logon task.");
            }
            else
            {
                if (IsScheduledStartupRegistered())
                {
                    await RunStartupTaskCommandAsync("/Delete", "/TN", StartupTaskName, "/F");
                }
                RemoveLegacyStartupEntry();
                SetStatus("Start with Windows disabled.");
            }
        }
        catch (Exception ex)
        {
            _updatingStartupCheck = true;
            _startupCheck.Checked = !enableStartup;
            _updatingStartupCheck = false;
            SetStatus($"Could not update startup setting: {ex.Message}", true);
        }
    }
    private void SaveStartMinimizedSetting()
    {
        if (_updatingStartMinimizedCheck)
        {
            return;
        }

        var startMinimized = _startMinimizedCheck.Checked;
        try
        {
            var settings = ProfileStorage.LoadAdapterSettings();
            settings.StartMinimizedToTray = startMinimized;
            ProfileStorage.SaveAdapterSettings(settings);
            SetStatus(startMinimized
                ? "The app will start minimized to the tray next time."
                : "The main window will open automatically next time.");
        }
        catch (Exception ex)
        {
            _updatingStartMinimizedCheck = true;
            _startMinimizedCheck.Checked = !startMinimized;
            _updatingStartMinimizedCheck = false;
            SetStatus($"Could not save startup display setting: {ex.Message}", true);
        }
    }

    private static void RemoveLegacyStartupEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
        var command = key?.GetValue(StartupValueName) as string;
        if (string.Equals(command?.Trim().Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            key!.DeleteValue(StartupValueName, throwOnMissingValue: false);
        }
    }

    private static async Task RunStartupTaskCommandAsync(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Windows could not start Task Scheduler.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var details = string.Join(" ", new[] { error.Trim(), output.Trim() }.Where(value => value.Length > 0));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? $"Task Scheduler exited with code {process.ExitCode}." : details);
        }
    }

    private void ClearEditorFields()
    {
        _renamingProfile = null;
        _profileList.ClearSelected();
        _profileNameBox.Clear();
        _dhcpAddressCheck.Checked = true;
        _ipBox.Text = string.Empty;
        _maskBox.Text = string.Empty;
        _gatewayBox.Text = string.Empty;
        _optionalIpAddresses.Clear();
        UpdateOptionalIpButtonText();
        _dhcpDnsCheck.Checked = true;
        _primaryDnsBox.Text = string.Empty;
        _alternateDnsBox.Text = string.Empty;
        _dnsSuffixBox.Text = string.Empty;
        UpdateFieldAvailability();
        SetStatus("Editor fields cleared. Saved profiles and target adapter are unchanged.");
    }

    private Control BuildSettingsPane()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = BackColor, Padding = new Padding(2, 0, 0, 0) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 128));

        var settings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = BackColor, Margin = new Padding(0) };
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var addressGroup = BuildAddressGroup();
        addressGroup.Dock = DockStyle.Top;
        addressGroup.Height = 300;
        settings.Controls.Add(addressGroup, 0, 0);
        var dnsGroup = BuildDnsGroup();
        dnsGroup.Dock = DockStyle.Top;
        dnsGroup.Height = 300;
        settings.Controls.Add(dnsGroup, 1, 0);
        layout.Controls.Add(settings, 0, 0);

        var actionRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(0) };
        actionRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        actionRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actionRow.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));

        var helperLabel = new Label
        {
            Text = "Profiles store the network values. The selected adapter below is the target for Apply.",
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(83, 96, 90),
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0)
        };
        actionRow.Controls.Add(helperLabel, 0, 0);
        actionRow.SetColumnSpan(helperLabel, 2);

        var target = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, BackColor = Color.White, Padding = new Padding(12, 5, 12, 5), Margin = new Padding(0, 0, 0, 0) };
        target.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        target.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        target.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        target.RowStyles.Add(new RowStyle(SizeType.Absolute, 10));
        target.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        target.Controls.Add(new Label { Text = "TARGET ADAPTER", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(70, 85, 79), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var readButton = MakeButton("Read current", Color.FromArgb(231, 239, 235), Color.FromArgb(28, 73, 63));
        readButton.Margin = new Padding(0);
        readButton.Click += (_, _) => ReadCurrentAdapter();
        target.Controls.Add(readButton, 1, 0);
        _adapterBox.Dock = DockStyle.Fill;
        _adapterBox.Margin = new Padding(0, 3, 6, 0);
        target.Controls.Add(_adapterBox, 0, 2);
        var refreshButton = MakeButton("Refresh", Color.FromArgb(231, 239, 235), Color.FromArgb(28, 73, 63));
        refreshButton.Margin = new Padding(0);
        refreshButton.Click += async (_, _) => await RefreshAdaptersAsync();
        target.Controls.Add(refreshButton, 1, 2);
        actionRow.Controls.Add(target, 0, 1);

        var applyButton = MakeButton("Apply settings", Color.FromArgb(23, 104, 85), Color.White);
        applyButton.Click += async (_, _) => await ApplySettingsAsync();
        actionRow.Controls.Add(applyButton, 1, 1);

        var actionNote = new Label
        {
            Text = "Applying settings may briefly disconnect this adapter.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(83, 96, 90),
            Margin = new Padding(0, 2, 0, 0)
        };
        actionRow.Controls.Add(actionNote, 0, 2);
        actionRow.SetColumnSpan(actionNote, 2);
        layout.Controls.Add(actionRow, 0, 1);
        return layout;
    }

    private Control BuildAddressGroup()
    {
        var group = new GroupBox { Text = "IPv4 address", Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12), Margin = new Padding(0, 0, 8, 0) };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(4, 8, 4, 4) };
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        fields.Controls.Add(_dhcpAddressCheck, 0, 0);
        var ipField = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 3, 0, 5) };
        ipField.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        ipField.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ipField.Controls.Add(new Label { Text = "IP address", Dock = DockStyle.Fill, ForeColor = Color.FromArgb(70, 85, 79), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _ipBox.Dock = DockStyle.Fill;
        _ipBox.Margin = new Padding(0, 1, 0, 0);
        ipField.Controls.Add(_ipBox, 0, 1);
        fields.Controls.Add(ipField, 0, 1);
        fields.Controls.Add(CreateField("Subnet mask", _maskBox), 0, 2);
        fields.Controls.Add(CreateField("Default gateway (optional)", _gatewayBox), 0, 3);
        _optionalIpButton = MakeButton("Optional IPs...", Color.FromArgb(231, 239, 235), Color.FromArgb(28, 73, 63));
        _optionalIpButton.Dock = DockStyle.Right;
        _optionalIpButton.Width = 160;
        _optionalIpButton.Click += (_, _) => ConfigureOptionalIpAddresses();
        fields.Controls.Add(_optionalIpButton, 0, 4);
        group.Controls.Add(fields);
        return group;
    }

    private Control BuildDnsGroup()
    {
        var group = new GroupBox { Text = "DNS servers", Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12), Margin = new Padding(8, 0, 0, 0) };
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(4, 8, 4, 4) };
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 34));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 33));
        fields.Controls.Add(_dhcpDnsCheck, 0, 0);
        fields.Controls.Add(CreateField("Preferred DNS", _primaryDnsBox), 0, 1);
        fields.Controls.Add(CreateField("Alternate DNS (optional)", _alternateDnsBox), 0, 2);
        fields.Controls.Add(CreateField("Connection-specific DNS suffix", _dnsSuffixBox), 0, 3);
        group.Controls.Add(fields);
        return group;
    }

    private static Control CreateField(string labelText, Control inputControl)
    {
        var field = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 3, 0, 5) };
        field.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        field.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        field.Controls.Add(new Label { Text = labelText, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(70, 85, 79), TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        inputControl.Dock = DockStyle.Fill;
        inputControl.Margin = new Padding(0, 1, 0, 0);
        field.Controls.Add(inputControl, 0, 1);
        return field;
    }

    private static Label SectionLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(70, 85, 79),
        Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft
    };

    private static Button MakeButton(string text, Color backColor, Color foreColor) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        FlatStyle = FlatStyle.Flat,
        FlatAppearance = { BorderSize = 0 },
        BackColor = backColor,
        ForeColor = foreColor,
        Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
        Cursor = Cursors.Hand,
        Margin = new Padding(0, 4, 0, 4)
    };

    // Load saved profiles and enumerate IPv4-capable adapters when the window opens.
    private async void InitializeApp()
    {
        LoadProfiles();
        await RefreshAdaptersAsync();
        UpdateFieldAvailability();
    }

    private void LoadProfiles()
    {
        _profiles.Clear();
        try
        {
            _profiles.AddRange(ProfileStorage.Load());
            RefreshProfileList();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load profiles: {ex.Message}", true);
        }
    }

    private void RefreshProfileList(NetworkProfile? select = null)
    {
        _profileList.Items.Clear();
        foreach (var profile in _profiles.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            _profileList.Items.Add(profile);
        }
        if (select is not null)
        {
            _profileList.SelectedItem = select;
        }
    }

    private async Task RefreshAdaptersAsync()
    {
        var previousId = (_adapterBox.SelectedItem as AdapterOption)?.Adapter.Id;
        _adapterBox.Enabled = false;
        _adapterBox.Items.Clear();
        SetStatus("Discovering network adapters...");
        try
        {
            var adapters = await Task.Run(TrayApplicationContext.GetTargetAdapters);
            foreach (var adapter in adapters)
            {
                _adapterBox.Items.Add(new AdapterOption(adapter));
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Could not enumerate adapters: {ex.Message}", true);
            return;
        }
        finally
        {
            _adapterBox.Enabled = true;
        }

        if (_adapterBox.Items.Count == 0)
        {
            SetStatus("No IPv4 network adapters were found.", true);
            return;
        }

        var previousIndex = -1;
        for (var index = 0; index < _adapterBox.Items.Count; index++)
        {
            if (((AdapterOption)_adapterBox.Items[index]!).Adapter.Id == previousId)
            {
                previousIndex = index;
                break;
            }
        }
        _adapterBox.SelectedIndex = previousIndex >= 0 ? previousIndex : 0;
        SetStatus("Adapters refreshed. Read current settings or load a profile.");
    }

    // Read the selected adapter's active IPv4 values and distinguish manually assigned DNS from DHCP DNS.
    private void ReadCurrentAdapter()
    {
        if (_adapterBox.SelectedItem is not AdapterOption option)
        {
            SetStatus("Select a network adapter first.", true);
            return;
        }

        try
        {
            var adapter = option.Adapter;
            var properties = adapter.GetIPProperties();
            var ipv4 = properties.GetIPv4Properties();
            if (ipv4 is null)
            {
                SetStatus("The selected adapter does not have IPv4 enabled.", true);
                return;
            }

            var address = properties.UnicastAddresses.FirstOrDefault(item =>
                item.Address.AddressFamily == AddressFamily.InterNetwork &&
                !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal));
            _dhcpAddressCheck.Checked = ipv4.IsDhcpEnabled;
            var addresses = properties.UnicastAddresses
                .Where(item => item.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !item.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                .ToList();
            _ipBox.Text = addresses.FirstOrDefault()?.Address.ToString() ?? string.Empty;
            _optionalIpAddresses.Clear();
            _optionalIpAddresses.AddRange(addresses.Skip(1).Select(item => new OptionalIpConfiguration
            {
                IpAddress = item.Address.ToString(),
                SubnetMask = PrefixToMask(item.PrefixLength)
            }));
            UpdateOptionalIpButtonText();
            _maskBox.Text = address is null ? string.Empty : PrefixToMask(address.PrefixLength);
            _gatewayBox.Text = properties.GatewayAddresses
                .Select(item => item.Address)
                .FirstOrDefault(item => item.AddressFamily == AddressFamily.InterNetwork && !item.Equals(IPAddress.Any))?.ToString() ?? string.Empty;
            _dnsSuffixBox.Text = properties.DnsSuffix;

            var manualDns = ReadManualDns(adapter.Id);
            _dhcpDnsCheck.Checked = string.IsNullOrWhiteSpace(manualDns);
            var dnsServers = !string.IsNullOrWhiteSpace(manualDns)
                ? ParseIpv4List(manualDns)
                : properties.DnsAddresses.Where(item => item.AddressFamily == AddressFamily.InterNetwork).ToList();
            _primaryDnsBox.Text = dnsServers.ElementAtOrDefault(0)?.ToString() ?? string.Empty;
            _alternateDnsBox.Text = dnsServers.ElementAtOrDefault(1)?.ToString() ?? string.Empty;
            UpdateFieldAvailability();
            SetStatus($"Read current settings from {adapter.Name}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read adapter settings: {ex.Message}", true);
        }
    }

    private static string? ReadManualDns(string adapterId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterId}");
            return key?.GetValue("NameServer") as string;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static List<IPAddress> ParseIpv4List(string value) => value
        .Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(item => IPAddress.TryParse(item, out var address) ? address : null)
        .Where(address => address?.AddressFamily == AddressFamily.InterNetwork)
        .Cast<IPAddress>()
        .ToList();

    private void ConfigureOptionalIpAddresses()
    {
        using var dialog = new OptionalIpAddressesForm(_optionalIpAddresses);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _optionalIpAddresses.Clear();
        _optionalIpAddresses.AddRange(dialog.Addresses);
        UpdateOptionalIpButtonText();
    }

    private void UpdateOptionalIpButtonText()
    {
        _optionalIpButton.Text = _optionalIpAddresses.Count == 0
            ? "Optional IPs..."
            : $"Optional IPs ({_optionalIpAddresses.Count})...";
    }

    private void SaveProfile()
    {
        var name = _profileNameBox.Text.Trim();
        if (name.Length == 0)
        {
            SetStatus("Enter a profile name before saving.", true);
            _profileNameBox.Focus();
            return;
        }

        var profile = CreateProfileFromForm(name);
        if (!TryValidateProfile(profile, out var message))
        {
            SetStatus(message, true);
            return;
        }

        var existing = _profiles.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && !ReferenceEquals(existing, _renamingProfile))
        {
            var answer = MessageBox.Show(this, $"A profile named '{name}' already exists. Replace it?", "Replace profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                return;
            }
        }

        var updatedProfiles = _profiles
            .Where(item => !ReferenceEquals(item, existing) && !ReferenceEquals(item, _renamingProfile))
            .ToList();
        updatedProfiles.Add(profile);
        try
        {
            WriteProfiles(updatedProfiles);
            _renamingProfile = null;
            RefreshProfileList(profile);
            SetStatus($"Saved profile '{name}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save profiles: {ex.Message}", true);
        }
    }

    private NetworkProfile CreateProfileFromForm(string name)
    {
        var selectedAdapter = (_adapterBox.SelectedItem as AdapterOption)?.Adapter.Name ?? "Unspecified adapter";
        var dnsServers = _dhcpDnsCheck.Checked
            ? new List<string>()
            : new[] { _primaryDnsBox.Text.Trim(), _alternateDnsBox.Text.Trim() }.Where(value => value.Length > 0).ToList();

        return new NetworkProfile
        {
            Name = name,
            SourceAdapter = selectedAdapter,
            UseDhcpAddress = _dhcpAddressCheck.Checked,
            IpAddress = _ipBox.Text.Trim(),
            OptionalIpAddresses = _optionalIpAddresses.Select(item => new OptionalIpConfiguration
            {
                IpAddress = item.IpAddress,
                SubnetMask = item.SubnetMask
            }).ToList(),
            SubnetMask = _maskBox.Text.Trim(),
            Gateway = _gatewayBox.Text.Trim(),
            UseDhcpDns = _dhcpDnsCheck.Checked,
            DnsServers = dnsServers,
            DnsSuffix = _dnsSuffixBox.Text.Trim().TrimEnd('.')
        };
    }

    private void LoadSelectedProfile()
    {
        if (_profileList.SelectedItem is not NetworkProfile profile)
        {
            SetStatus("Select a saved profile to load.", true);
            return;
        }

        _renamingProfile = null;
        _profileNameBox.Text = profile.Name;
        _dhcpAddressCheck.Checked = profile.UseDhcpAddress;
        _ipBox.Text = profile.IpAddress;
        _optionalIpAddresses.Clear();
        _optionalIpAddresses.AddRange(GetOptionalProfileAddresses(profile).Select(item => new OptionalIpConfiguration
        {
            IpAddress = item.IpAddress,
            SubnetMask = item.SubnetMask
        }));
        UpdateOptionalIpButtonText();
        _maskBox.Text = profile.SubnetMask;
        _gatewayBox.Text = profile.Gateway;
        _dhcpDnsCheck.Checked = profile.UseDhcpDns;
        _primaryDnsBox.Text = profile.DnsServers.ElementAtOrDefault(0) ?? string.Empty;
        _alternateDnsBox.Text = profile.DnsServers.ElementAtOrDefault(1) ?? string.Empty;
        _dnsSuffixBox.Text = profile.DnsSuffix;
        UpdateFieldAvailability();
        SetStatus($"Loaded '{profile.Name}' (captured from {profile.SourceAdapter}). Choose a target adapter and apply.");
    }

    private void BeginRenamingSelectedProfile()
    {
        if (_profileList.SelectedItem is not NetworkProfile profile)
        {
            return;
        }

        LoadSelectedProfile();
        _renamingProfile = profile;
        _profileNameBox.Focus();
        _profileNameBox.SelectAll();
        SetStatus($"Enter a new name for '{profile.Name}' and save the profile.");
    }

    private void DeleteSelectedProfile()
    {
        if (_profileList.SelectedItem is not NetworkProfile profile)
        {
            SetStatus("Select a saved profile to delete.", true);
            return;
        }
        if (MessageBox.Show(this, $"Delete profile '{profile.Name}'?", "Delete profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }

        var updatedProfiles = _profiles.Where(item => !ReferenceEquals(item, profile)).ToList();
        try
        {
            WriteProfiles(updatedProfiles);
            if (ReferenceEquals(_renamingProfile, profile))
            {
                _renamingProfile = null;
            }
            RefreshProfileList();
            SetStatus($"Deleted '{profile.Name}'.");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not update profiles: {ex.Message}", true);
        }
    }

    private async Task ApplySettingsAsync()
    {
        if (_adapterBox.SelectedItem is not AdapterOption option)
        {
            SetStatus("Select a network adapter first.", true);
            return;
        }

        var profile = CreateProfileFromForm(_profileNameBox.Text.Trim());
        if (!TryValidateProfile(profile, out var validationMessage))
        {
            SetStatus(validationMessage, true);
            return;
        }

        var addressSummary = profile.UseDhcpAddress ? "IP address: DHCP" : $"IP addresses: {string.Join(", ", GetProfileIpAddresses(profile))}";
        var dnsSummary = profile.UseDhcpDns ? "DNS: DHCP" : $"DNS: {string.Join(", ", profile.DnsServers)}";
        var suffixSummary = string.IsNullOrWhiteSpace(profile.DnsSuffix) ? string.Empty : $"\nDNS suffix: {profile.DnsSuffix}";
        var answer = MessageBox.Show(
            this,
            $"Apply these settings to '{option.Adapter.Name}'?\n\n{addressSummary}\n{dnsSummary}{suffixSummary}\n\nThe adapter may disconnect temporarily.",
            "Apply network settings",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        try
        {
            SetStatus("Applying settings. Please wait...");
            await ApplyProfileAsync(profile, option.Adapter.Name);
            SetStatus($"Settings applied to {option.Adapter.Name}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Applying settings failed: {ex.Message}", true);
        }
    }

    private static List<OptionalIpConfiguration> GetOptionalProfileAddresses(NetworkProfile profile)
    {
        if (profile.OptionalIpAddresses.Count > 0)
        {
            return profile.OptionalIpAddresses;
        }

        // Older profiles stored optional addresses as strings sharing the primary subnet mask.
        return (profile.AdditionalIpAddresses ?? new List<string>())
            .Select(address => new OptionalIpConfiguration { IpAddress = address, SubnetMask = profile.SubnetMask })
            .ToList();
    }

    internal static List<string> GetProfileIpAddresses(NetworkProfile profile)
    {
        var addresses = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.IpAddress))
        {
            addresses.Add(profile.IpAddress);
        }
        addresses.AddRange(GetOptionalProfileAddresses(profile).Select(item => item.IpAddress).Where(value => !string.IsNullOrWhiteSpace(value)));
        return addresses;
    }

    // Apply IPv4 addresses and DNS independently so profiles can mix DHCP and static settings.
    internal static async Task ApplyProfileAsync(NetworkProfile profile, string adapterName)
    {
        if (profile.UseDhcpAddress)
        {
            await RunNetshAsync("interface", "ipv4", "set", "address", $"name={adapterName}", "source=dhcp");
        }
        else
        {
            var optionalAddresses = GetOptionalProfileAddresses(profile);
            var addressArguments = new List<string>
            {
                "interface", "ipv4", "set", "address", $"name={adapterName}", "source=static",
                $"address={profile.IpAddress}", $"mask={profile.SubnetMask}"
            };
            if (!string.IsNullOrWhiteSpace(profile.Gateway))
            {
                addressArguments.Add($"gateway={profile.Gateway}");
                addressArguments.Add("gwmetric=1");
            }
            await RunNetshAsync(addressArguments.ToArray());
            foreach (var address in optionalAddresses)
            {
                await RunNetshAsync(
                    "interface", "ipv4", "add", "address", $"name={adapterName}",
                    $"address={address.IpAddress}", $"mask={address.SubnetMask}");
            }
        }

        if (profile.UseDhcpDns)
        {
            await RunNetshAsync("interface", "ipv4", "set", "dnsservers", $"name={adapterName}", "source=dhcp");
        }
        else
        {
            var primary = profile.DnsServers.ElementAtOrDefault(0) ?? "none";
            await RunNetshAsync("interface", "ipv4", "set", "dnsservers", $"name={adapterName}", "source=static", $"address={primary}", "validate=no");
            for (var index = 1; index < profile.DnsServers.Count; index++)
            {
                await RunNetshAsync("interface", "ipv4", "add", "dnsservers", $"name={adapterName}", $"address={profile.DnsServers[index]}", $"index={index + 1}", "validate=no");
            }
        }

        await RunPowerShellAsync(
            $"$ErrorActionPreference = 'Stop'; try {{ Set-DnsClient -InterfaceAlias {QuotePowerShellString(adapterName)} -ConnectionSpecificSuffix {QuotePowerShellString(profile.DnsSuffix)} -ErrorAction Stop; exit 0 }} catch {{ [Console]::Error.WriteLine($_.Exception.Message); exit 1 }}");
    }

    private static string QuotePowerShellString(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task RunPowerShellAsync(string command)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        process.StartInfo.ArgumentList.Add("-NoProfile");
        process.StartInfo.ArgumentList.Add("-NonInteractive");
        process.StartInfo.ArgumentList.Add("-Command");
        process.StartInfo.ArgumentList.Add(command);

        if (!process.Start())
        {
            throw new InvalidOperationException("Windows could not start PowerShell to configure the DNS suffix.");
        }
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var details = string.Join(" ", new[] { error.Trim(), output.Trim() }.Where(value => value.Length > 0));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? $"PowerShell exited with code {process.ExitCode}." : details);
        }
    }

    private static async Task RunNetshAsync(params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("Windows could not start netsh.exe.");
        }
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var details = string.Join(" ", new[] { error.Trim(), output.Trim() }.Where(value => value.Length > 0));
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(details) ? $"netsh exited with code {process.ExitCode}." : details);
        }
    }

    private void WriteProfiles(List<NetworkProfile> profiles)
    {
        ProfileStorage.Save(profiles);
        _profiles.Clear();
        _profiles.AddRange(profiles);
    }

    internal static bool TryValidateProfile(NetworkProfile profile, out string message)
    {
        if (!profile.UseDhcpAddress)
        {
            var addresses = GetProfileIpAddresses(profile);
            var optionalAddresses = GetOptionalProfileAddresses(profile);
            if (!TryParseIpv4(profile.IpAddress, out _) || addresses.Any(value => !TryParseIpv4(value, out _)))
            {
                message = "Enter a valid primary IPv4 address and any optional IPv4 addresses.";
                return false;
            }
            if (!TryParseIpv4(profile.SubnetMask, out var mask) || !IsValidSubnetMask(mask!))
            {
                message = "Enter a valid IPv4 subnet mask, such as 255.255.255.0.";
                return false;
            }
            if (optionalAddresses.Any(item => !TryParseIpv4(item.SubnetMask, out var optionalMask) || !IsValidSubnetMask(optionalMask!)))
            {
                message = "Each optional IP address needs a valid subnet mask.";
                return false;
            }
            if (addresses.Distinct(StringComparer.OrdinalIgnoreCase).Count() != addresses.Count)
            {
                message = "Each IPv4 address can only be listed once.";
                return false;
            }
            if (profile.Gateway.Length > 0 && !TryParseIpv4(profile.Gateway, out _))
            {
                message = "The default gateway must be a valid IPv4 address.";
                return false;
            }
        }

        if (!profile.UseDhcpDns && profile.DnsServers.Any(value => !TryParseIpv4(value, out _)))
        {
            message = "DNS server entries must be valid IPv4 addresses.";
            return false;
        }

        if (!IsValidDnsSuffix(profile.DnsSuffix))
        {
            message = "Enter a valid DNS suffix, such as corp.example.com.";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool TryParseIpv4(string value, out IPAddress? address)
    {
        var valid = IPAddress.TryParse(value, out address) && address.AddressFamily == AddressFamily.InterNetwork;
        return valid;
    }

    internal static bool IsValidIpv4Address(string value) => TryParseIpv4(value, out _);

    internal static bool IsValidSubnetMaskText(string value) =>
        TryParseIpv4(value, out var mask) && IsValidSubnetMask(mask!);

    private static bool IsValidSubnetMask(IPAddress mask)
    {
        var bytes = mask.GetAddressBytes();
        var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
        var inverted = ~value;
        return value != 0 && (inverted & (inverted + 1)) == 0;
    }

    private static bool IsValidDnsSuffix(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var suffix = value.Trim().TrimEnd('.');
        if (suffix.Length is 0 or > 253)
        {
            return false;
        }

        return suffix.Split('.').All(label =>
            label.Length is > 0 and <= 63 &&
            label[0] != '-' && label[^1] != '-' &&
            label.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'));
    }

    private static string PrefixToMask(int prefixLength)
    {
        uint mask = prefixLength == 0 ? 0 : uint.MaxValue << (32 - prefixLength);
        return $"{(mask >> 24) & 255}.{(mask >> 16) & 255}.{(mask >> 8) & 255}.{mask & 255}";
    }

    private void UpdateFieldAvailability()
    {
        _ipBox.Enabled = !_dhcpAddressCheck.Checked;
        _maskBox.Enabled = !_dhcpAddressCheck.Checked;
        _gatewayBox.Enabled = !_dhcpAddressCheck.Checked;
        _primaryDnsBox.Enabled = !_dhcpDnsCheck.Checked;
        _alternateDnsBox.Enabled = !_dhcpDnsCheck.Checked;
        _dnsSuffixBox.Enabled = !_dhcpDnsCheck.Checked;
        _optionalIpButton.Enabled = !_dhcpAddressCheck.Checked;
    }

    private void SetStatus(string message, bool isError = false)
    {
        _statusLabel.Text = message;
        _statusLabel.ForeColor = isError ? Color.FromArgb(157, 48, 37) : Color.FromArgb(70, 85, 79);
    }

}

internal sealed class Ipv4AddressBox : UserControl
{
    private readonly TextBox[] _octets = new TextBox[4];
    private readonly string[] _lastValidOctets = new string[4];
    private bool _updatingText;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public new string Text
    {
        get
        {
            var values = _octets.Select(octet => octet.Text).ToArray();
            return values.All(string.IsNullOrEmpty) ? string.Empty : string.Join('.', values);
        }
        set
        {
            var values = (value ?? string.Empty).Split('.');
            _updatingText = true;
            for (var index = 0; index < _octets.Length; index++)
            {
                var octetValue = index < values.Length ? values[index] : string.Empty;
                _octets[index].Text = octetValue;
                _lastValidOctets[index] = octetValue;
            }
            _updatingText = false;
            OnTextChanged(EventArgs.Empty);
        }
    }

    public Ipv4AddressBox()
    {
        Height = 26;
        MinimumSize = new Size(190, 24);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1, Margin = new Padding(0) };
        for (var index = 0; index < _octets.Length; index++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
            var octetIndex = index;
            var octetBox = new TextBox { Dock = DockStyle.Fill, MaxLength = 3, TextAlign = HorizontalAlignment.Center, Margin = new Padding(0) };
            octetBox.KeyPress += (_, eventArgs) =>
            {
                if (!char.IsControl(eventArgs.KeyChar) && !char.IsAsciiDigit(eventArgs.KeyChar))
                {
                    eventArgs.Handled = true;
                }
            };
            octetBox.KeyDown += (_, eventArgs) =>
            {
                if (eventArgs.KeyCode == Keys.Back && octetBox.SelectionStart == 0 && octetBox.Text.Length == 0 && octetIndex > 0)
                {
                    _octets[octetIndex - 1].Focus();
                    _octets[octetIndex - 1].SelectionStart = _octets[octetIndex - 1].TextLength;
                    eventArgs.Handled = true;
                }
            };
            octetBox.TextChanged += (_, _) => HandleOctetChanged(octetIndex);
            _octets[index] = octetBox;
            layout.Controls.Add(octetBox, index * 2, 0);

            if (index < _octets.Length - 1)
            {
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 14));
                layout.Controls.Add(new Label { Text = ".", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter }, index * 2 + 1, 0);
            }
        }
        Controls.Add(layout);
    }

    private void HandleOctetChanged(int index)
    {
        if (_updatingText)
        {
            return;
        }

        var octet = _octets[index].Text;
        if (octet.Length > 0 && (!octet.All(char.IsAsciiDigit) || !int.TryParse(octet, out var value) || value > 255))
        {
            var selectionStart = _octets[index].SelectionStart;
            _updatingText = true;
            _octets[index].Text = _lastValidOctets[index];
            _octets[index].SelectionStart = Math.Min(selectionStart, _octets[index].TextLength);
            _updatingText = false;
            return;
        }

        _lastValidOctets[index] = octet;
        if (octet.Length == 3 && index < _octets.Length - 1)
        {
            _octets[index + 1].Focus();
            _octets[index + 1].SelectionStart = _octets[index + 1].TextLength;
        }
        OnTextChanged(EventArgs.Empty);
    }
}

internal sealed class OptionalIpAddressesForm : Form
{
    private readonly List<AddressRow> _rows = new();
    private readonly TableLayoutPanel _rowsLayout = new() { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
    private readonly Button _addButton = new() { Text = "+ Add another IP", Width = 150, Height = 30 };

    public OptionalIpAddressesForm(IEnumerable<OptionalIpConfiguration> addresses)
    {
        Text = "Optional IP addresses";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(560, 380);
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.Controls.Add(new Label
        {
            Text = "Each optional IPv4 address can use its own subnet mask. The primary gateway remains unchanged.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _rowsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var rowsPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        rowsPanel.Controls.Add(_rowsLayout);
        layout.Controls.Add(rowsPanel, 0, 1);

        _addButton.Click += (_, _) => AddRow();
        layout.Controls.Add(_addButton, 0, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var saveButton = new Button { Text = "OK", Width = 88, Height = 30 };
        saveButton.Click += (_, _) => TryAccept();
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 88, Height = 30 };
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        layout.Controls.Add(buttons, 0, 3);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        Controls.Add(layout);

        var existingAddresses = addresses.ToList();
        if (existingAddresses.Count == 0)
        {
            AddRow();
        }
        else
        {
            foreach (var address in existingAddresses)
            {
                AddRow(address.IpAddress, address.SubnetMask);
            }
        }
    }

    public List<OptionalIpConfiguration> Addresses => _rows
        .Where(row => row.IpAddress.Text.Trim().Length > 0 && row.SubnetMask.Text.Trim().Length > 0)
        .Select(row => new OptionalIpConfiguration
        {
            IpAddress = row.IpAddress.Text.Trim(),
            SubnetMask = row.SubnetMask.Text.Trim()
        })
        .ToList();

    private void AddRow(string ipAddress = "", string subnetMask = "")
    {
        var ipBox = new Ipv4AddressBox { Dock = DockStyle.Fill, Text = ipAddress, Margin = new Padding(3) };
        var maskBox = new Ipv4AddressBox { Dock = DockStyle.Fill, Text = subnetMask, Margin = new Padding(3) };
        var row = new AddressRow(ipBox, maskBox);
        row.RemoveButton.Click += (_, _) => RemoveRow(row);
        ipBox.TextChanged += (_, _) => UpdateAddButton();
        maskBox.TextChanged += (_, _) => UpdateAddButton();
        _rows.Add(row);
        RebuildRows();
        UpdateAddButton();
    }

    private void RebuildRows()
    {
        _rowsLayout.SuspendLayout();
        _rowsLayout.Controls.Clear();
        _rowsLayout.RowStyles.Clear();
        _rowsLayout.RowCount = _rows.Count * 2;

        for (var index = 0; index < _rows.Count; index++)
        {
            var row = _rows[index];
            var details = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(4), Margin = new Padding(0) };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            details.Controls.Add(new Label { Text = "IP address", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            details.Controls.Add(row.IpAddress, 0, 1);
            details.Controls.Add(new Label { Text = "Subnet mask", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 2);
            details.Controls.Add(row.RemoveButton, 1, 2);
            details.Controls.Add(row.SubnetMask, 0, 3);
            _rowsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
            _rowsLayout.Controls.Add(details, 0, index * 2);

            var divider = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(220, 228, 224), Margin = new Padding(6, 5, 6, 5), Height = 1 };
            _rowsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));
            _rowsLayout.Controls.Add(divider, 0, index * 2 + 1);
        }

        _rowsLayout.ResumeLayout();
    }

    private void RemoveRow(AddressRow row)
    {
        _rows.Remove(row);
        if (_rows.Count == 0)
        {
            AddRow();
            return;
        }
        RebuildRows();
        UpdateAddButton();
    }

    private void UpdateAddButton()
    {
        var lastRow = _rows[^1];
        _addButton.Enabled = IsValidAddressRow(lastRow);
    }

    private void TryAccept()
    {
        if (_rows.Any(row =>
        {
            var ipAddress = row.IpAddress.Text.Trim();
            var subnetMask = row.SubnetMask.Text.Trim();
            return (ipAddress.Length > 0 || subnetMask.Length > 0) && !IsValidAddressRow(row);
        }))
        {
            MessageBox.Show(this, "Enter a complete IPv4 address and valid subnet mask for each optional row.", "Incomplete address", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static bool IsValidAddressRow(AddressRow row) =>
        MainForm.IsValidIpv4Address(row.IpAddress.Text.Trim()) &&
        MainForm.IsValidSubnetMaskText(row.SubnetMask.Text.Trim());

    private sealed class AddressRow(Ipv4AddressBox ipAddress, Ipv4AddressBox subnetMask)
    {
        public Ipv4AddressBox IpAddress { get; } = ipAddress;
        public Ipv4AddressBox SubnetMask { get; } = subnetMask;
        public Button RemoveButton { get; } = new() { Text = "Remove", Dock = DockStyle.Fill, Margin = new Padding(3) };
    }
}

// Reuse this label anywhere an adapter name is presented so the editor and tray stay consistent.
internal sealed class AdapterOption(NetworkInterface adapter)
{
    public NetworkInterface Adapter { get; } = adapter;
    public override string ToString() => $"{Adapter.Name}  ({Adapter.NetworkInterfaceType}, {Adapter.OperationalStatus})";
}

// Let users choose which discovered adapters appear under profiles in the tray menu.
internal sealed class AdapterVisibilityForm : Form
{
    private readonly CheckedListBox _adapterList = new() { Dock = DockStyle.Fill, CheckOnClick = true };

    public List<string> HiddenAdapterIds { get; private set; } = new();

    public AdapterVisibilityForm(IReadOnlyList<NetworkInterface> adapters, IReadOnlyCollection<string> hiddenAdapterIds)
    {
        Text = "Tray adapters";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 390);
        Font = new Font("Segoe UI", 9F);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        layout.Controls.Add(new Label
        {
            Text = "Checked adapters appear in each tray profile menu.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var hiddenIds = new HashSet<string>(hiddenAdapterIds, StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            var option = new AdapterOption(adapter);
            _adapterList.Items.Add(option, !hiddenIds.Contains(adapter.Id));
        }
        layout.Controls.Add(_adapterList, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(0, 8, 0, 0) };
        var saveButton = new Button { Text = "Save", Width = 90, DialogResult = DialogResult.OK };
        saveButton.Click += (_, _) => SaveSelection();
        var cancelButton = new Button { Text = "Cancel", Width = 90, DialogResult = DialogResult.Cancel };
        var noneButton = new Button { Text = "None", Width = 80 };
        noneButton.Click += (_, _) => SetAllChecked(false);
        var allButton = new Button { Text = "Select all", Width = 90 };
        allButton.Click += (_, _) => SetAllChecked(true);
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        buttons.Controls.Add(noneButton);
        buttons.Controls.Add(allButton);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);
        AcceptButton = saveButton;
        CancelButton = cancelButton;
    }

    private void SetAllChecked(bool isChecked)
    {
        for (var index = 0; index < _adapterList.Items.Count; index++)
        {
            _adapterList.SetItemChecked(index, isChecked);
        }
    }

    private void SaveSelection()
    {
        HiddenAdapterIds = Enumerable.Range(0, _adapterList.Items.Count)
            .Where(index => !_adapterList.GetItemChecked(index))
            .Select(index => ((AdapterOption)_adapterList.Items[index]!).Adapter.Id)
            .ToList();
    }
}

// This simple model is serialized to the user's local application data folder.
internal sealed class NetworkProfile
{
    public string Name { get; set; } = string.Empty;
    public string SourceAdapter { get; set; } = string.Empty;
    public bool UseDhcpAddress { get; set; }
    public string IpAddress { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? AdditionalIpAddresses { get; set; }
    public List<OptionalIpConfiguration> OptionalIpAddresses { get; set; } = new();
    public string SubnetMask { get; set; } = string.Empty;
    public string Gateway { get; set; } = string.Empty;
    public bool UseDhcpDns { get; set; }
    public List<string> DnsServers { get; set; } = new();
    public string DnsSuffix { get; set; } = string.Empty;

    public override string ToString() => Name;
}

// Keep the tray menu and editor connected to the same local JSON profile file.
internal static class ProfileStorage
{
    private static readonly string ProfileFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetSwitch",
        "profiles.json");
    private static readonly string SettingsFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetSwitch",
        "settings.json");

    public static List<NetworkProfile> Load()
    {
        if (!File.Exists(ProfileFile))
        {
            return new List<NetworkProfile>();
        }

        var json = File.ReadAllText(ProfileFile);
        return JsonSerializer.Deserialize<List<NetworkProfile>>(json) ?? new List<NetworkProfile>();
    }

    public static void Save(List<NetworkProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        WriteJsonAtomically(ProfileFile, json);
    }

    public static AdapterVisibilitySettings LoadAdapterSettings()
    {
        if (!File.Exists(SettingsFile))
        {
            return new AdapterVisibilitySettings();
        }

        var json = File.ReadAllText(SettingsFile);
        return JsonSerializer.Deserialize<AdapterVisibilitySettings>(json) ?? new AdapterVisibilitySettings();
    }

    public static void SaveAdapterSettings(AdapterVisibilitySettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        WriteJsonAtomically(SettingsFile, json);
    }

    private static void WriteJsonAtomically(string path, string json)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed class AdapterVisibilitySettings
{
    public List<string> HiddenAdapterIds { get; set; } = new();
    public bool StartMinimizedToTray { get; set; }
}

internal sealed class OptionalIpConfiguration
{
    public string IpAddress { get; set; } = string.Empty;
    public string SubnetMask { get; set; } = string.Empty;
}