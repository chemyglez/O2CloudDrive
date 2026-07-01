using System.Diagnostics;
using O2CloudDrive.Api;
using O2CloudDrive.Config;
using O2CloudDrive.Mounting;

namespace O2CloudDrive.Ui;

public sealed class MainForm : Form
{
    private static readonly Color AppBackColor = Color.FromArgb(229, 235, 241);
    private static readonly Color SurfaceColor = Color.FromArgb(248, 250, 252);
    private static readonly Color SidebarColor = Color.FromArgb(38, 43, 49);
    private static readonly Color SidebarPanelColor = Color.FromArgb(48, 55, 62);
    private static readonly Color SidebarTextColor = Color.FromArgb(246, 248, 250);
    private static readonly Color SidebarMutedColor = Color.FromArgb(182, 194, 205);
    private static readonly Color TextColor = Color.FromArgb(27, 34, 42);
    private static readonly Color MutedTextColor = Color.FromArgb(88, 100, 114);
    private static readonly Color BorderColor = Color.FromArgb(202, 211, 222);
    private static readonly Color PrimaryColor = Color.FromArgb(0, 112, 150);
    private static readonly Color SuccessColor = Color.FromArgb(34, 153, 92);
    private static readonly Color WarningColor = Color.FromArgb(181, 121, 0);
    private static readonly Color DangerColor = Color.FromArgb(193, 62, 48);

    private readonly O2DriveAppServices _services;
    private readonly ComboBox _driveComboBox = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly TextBox _volumeLabelTextBox = new() { Width = 320 };
    private readonly CheckBox _forceLoginCheckBox = new() { AutoSize = true };
    private readonly Label _sessionValueLabel = new() { AutoSize = true };
    private readonly Label _mountValueLabel = new() { AutoSize = true };
    private readonly Label _statusValueLabel = new() { AutoSize = false, Height = 56, Dock = DockStyle.Fill };
    private readonly TextBox _logTextBox = new()
    {
        ReadOnly = true,
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.FixedSingle,
        Dock = DockStyle.Fill,
    };
    private readonly Label _trayHintLabel = new() { AutoSize = true };
    private readonly Button _loginButton;
    private readonly Button _mountButton;
    private readonly Button _openButton;
    private readonly Button _unmountButton;
    private readonly Button _logoutButton;
    private readonly NotifyIcon _notifyIcon;
    private readonly List<string> _logLines = [];
    private readonly Dictionary<string, int> _lastTransferPercentLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _lastTransferLogAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _liveTransferLogIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _busy;
    private bool _allowClose;
    private string _lastStatus = "Listo.";

    public MainForm(O2DriveAppServices services)
    {
        _services = services;

        Text = "O2 Cloud Drive";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 380);
        Size = new Size(720, 460);
        BackColor = AppBackColor;
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScroll = false;
        Icon = AppIcon.Load();

        _loginButton = CreateButton("Login", PrimaryColor, Color.White);
        _mountButton = CreateButton("Montar unidad", SuccessColor, Color.White);
        _openButton = CreateButton("Abrir unidad", SurfaceColor, TextColor, BorderColor);
        _unmountButton = CreateButton("Desmontar", SurfaceColor, TextColor, BorderColor);
        _logoutButton = CreateButton("Logout", SurfaceColor, DangerColor, Color.FromArgb(241, 185, 179));
        _driveComboBox.BackColor = Color.White;
        _volumeLabelTextBox.BackColor = Color.White;
        _forceLoginCheckBox.BackColor = SurfaceColor;
        _logTextBox.BackColor = Color.White;
        _logTextBox.ForeColor = TextColor;

        _notifyIcon = new NotifyIcon
        {
            Icon = AppIcon.Load(),
            Text = "O2 Cloud Drive",
            Visible = true,
            ContextMenuStrip = CreateTrayMenu(),
        };
        _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
        _services.ApiClient.TransferProgress += OnTransferProgress;

        BuildLayout();
        Load += (_, _) =>
        {
            LoadInitialValues(_services.Config);
            UpdateState(_services.AuthService.HasStoredSession()
                ? "auth:stored-session"
                : "Listo.");
        };
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                HideToTray();
            }
        };
        FormClosing += OnFormClosing;

        _loginButton.Click += async (_, _) => await LoginOrLogoutAsync();
        _mountButton.Click += async (_, _) => await MountAsync();
        _openButton.Click += (_, _) => OpenMountedDrive();
        _unmountButton.Click += async (_, _) => await UnmountAsync();
        _logoutButton.Click += async (_, _) => await LogoutAsync();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(18, 16, 18, 16),
            BackColor = AppBackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 148));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        root.Controls.Add(CreateCompactHeader(), 0, 0);
        root.Controls.Add(CreateCompactMountPanel(), 0, 1);
        root.Controls.Add(CreateStateAndLogPanel(), 0, 2);
        root.Controls.Add(CreateCopyrightFooter(), 0, 3);
        Controls.Add(root);
    }

    private Control CreateCompactHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = AppBackColor,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var title = new Label
        {
            Text = "O2 Cloud Drive 0.5 beta",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TextColor,
            Margin = new Padding(0, 0, 0, 0),
        };
        _loginButton.Anchor = AnchorStyles.Right;
        _loginButton.Margin = new Padding(0);
        _loginButton.MinimumSize = new Size(96, 34);

        header.Controls.Add(title, 0, 0);
        header.Controls.Add(_loginButton, 1, 0);
        return header;
    }

    private Control CreateCompactMountPanel()
    {
        var panel = CreateSurfacePanel(new Padding(18, 14, 18, 12));
        panel.Margin = new Padding(0, 0, 0, 12);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            BackColor = SurfaceColor,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 4,
            BackColor = SurfaceColor,
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _driveComboBox.Anchor = AnchorStyles.Left;
        _driveComboBox.Width = 84;
        _volumeLabelTextBox.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        fields.Controls.Add(CreateFieldLabel("Letra de unidad"), 0, 0);
        fields.Controls.Add(_driveComboBox, 1, 0);
        fields.Controls.Add(CreateFieldLabel("Nombre"), 2, 0);
        fields.Controls.Add(_volumeLabelTextBox, 3, 0);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = SurfaceColor,
            Padding = new Padding(0, 8, 0, 0),
        };
        actions.Controls.Add(_mountButton);
        actions.Controls.Add(_openButton);
        actions.Controls.Add(_unmountButton);

        layout.Controls.Add(fields, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control CreateStateAndLogPanel()
    {
        var panel = CreateSurfacePanel(new Padding(16));
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 3,
            ColumnCount = 1,
            BackColor = SurfaceColor,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var state = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 1,
            ColumnCount = 4,
            BackColor = SurfaceColor,
        };
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 240));
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        state.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        state.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _sessionValueLabel.Anchor = AnchorStyles.Left;
        _mountValueLabel.Anchor = AnchorStyles.Left;
        _sessionValueLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Regular, GraphicsUnit.Point);
        _mountValueLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Regular, GraphicsUnit.Point);
        state.Controls.Add(CreateFieldLabel("Sesion"), 0, 0);
        state.Controls.Add(_sessionValueLabel, 1, 0);
        state.Controls.Add(CreateFieldLabel("Unidad"), 2, 0);
        state.Controls.Add(_mountValueLabel, 3, 0);

        var logTitle = CreateSectionTitle("Log");
        logTitle.Margin = new Padding(0, 4, 0, 6);

        layout.Controls.Add(state, 0, 0);
        layout.Controls.Add(logTitle, 0, 1);
        layout.Controls.Add(_logTextBox, 0, 2);
        panel.Controls.Add(layout);
        return panel;
    }

    private Control CreateSidebar()
    {
        var sidebar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 5,
            ColumnCount = 1,
            BackColor = SidebarColor,
            Padding = new Padding(22),
        };
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));

        var brand = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SidebarColor,
        };
        var title = new Label
        {
            Text = "O2 Cloud Drive",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 18F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = SidebarTextColor,
            Location = new Point(0, 4),
        };
        var subtitle = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = SidebarMutedColor,
            Location = new Point(2, 48),
        };
        brand.Controls.Add(title);
        brand.Controls.Add(subtitle);

        sidebar.Controls.Add(brand, 0, 0);
        sidebar.Controls.Add(CreateSidebarStatusBlock("Sesion", _sessionValueLabel), 0, 1);
        sidebar.Controls.Add(CreateSidebarStatusBlock("Unidad", _mountValueLabel), 0, 2);

        var activity = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SidebarPanelColor,
            Padding = new Padding(14),
            Margin = new Padding(0, 6, 0, 10),
        };
        var activityTitle = new Label
        {
            Text = "Log",
            Dock = DockStyle.Top,
            Height = 26,
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = SidebarTextColor,
        };
        _statusValueLabel.ForeColor = SidebarMutedColor;
        _statusValueLabel.BackColor = SidebarPanelColor;
        _statusValueLabel.Padding = new Padding(0, 8, 0, 0);
        _statusValueLabel.Dock = DockStyle.Fill;
        activity.Controls.Add(_statusValueLabel);
        activity.Controls.Add(activityTitle);
        sidebar.Controls.Add(activity, 0, 3);

        _trayHintLabel.Text = "Al minimizar, la app queda activa en la bandeja.";
        _trayHintLabel.ForeColor = SidebarMutedColor;
        _trayHintLabel.AutoSize = false;
        _trayHintLabel.Dock = DockStyle.Fill;
        _trayHintLabel.Padding = new Padding(0, 8, 0, 0);
        sidebar.Controls.Add(_trayHintLabel, 0, 4);
        return sidebar;
    }

    private Control CreateMainArea()
    {
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(24, 22, 24, 22),
            BackColor = AppBackColor,
            AutoScroll = true,
        };
        main.RowStyles.Add(new RowStyle(SizeType.Absolute, 74));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        main.Controls.Add(CreateMainHeader(), 0, 0);
        main.Controls.Add(CreateSettingsPanel(), 0, 1);
        return main;
    }

    private Control CreateMainHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AppBackColor,
        };
        var title = new Label
        {
            Text = "O2 Cloud Drive",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 17F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TextColor,
            Location = new Point(0, 0),
        };
        var subtitle = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = MutedTextColor,
            Location = new Point(2, 42),
        };
        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private static Control CreateSidebarStatusBlock(string label, Label valueLabel)
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SidebarPanelColor,
            Padding = new Padding(14, 12, 14, 10),
            Margin = new Padding(0, 0, 0, 12),
        };
        var labelControl = new Label
        {
            Text = label,
            Dock = DockStyle.Top,
            Height = 24,
            ForeColor = SidebarMutedColor,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point),
        };
        valueLabel.Dock = DockStyle.Top;
        valueLabel.Height = 28;
        valueLabel.AutoSize = false;
        valueLabel.Font = new Font("Segoe UI Semibold", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
        valueLabel.BackColor = SidebarPanelColor;
        panel.Controls.Add(valueLabel);
        panel.Controls.Add(labelControl);
        return panel;
    }

    private Control CreateHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AppBackColor,
            Padding = new Padding(0, 0, 0, 14),
        };

        var title = new Label
        {
            Text = "O2 Cloud Drive",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 20F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TextColor,
            Location = new Point(0, 4),
        };

        var subtitle = new Label
        {
            Text = string.Empty,
            AutoSize = true,
            Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = MutedTextColor,
            Location = new Point(2, 48),
        };

        panel.Controls.Add(title);
        panel.Controls.Add(subtitle);
        return panel;
    }

    private Control CreateContent()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = AppBackColor,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(CreateStatusPanel(), 0, 0);
        layout.Controls.Add(CreateSettingsPanel(), 1, 0);
        return layout;
    }

    private Control CreateStatusPanel()
    {
        var panel = CreateSurfacePanel(new Padding(18));
        panel.Margin = new Padding(0, 0, 14, 0);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 8,
            ColumnCount = 1,
            BackColor = SurfaceColor,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(CreateSectionTitle("Estado"), 0, 0);
        layout.Controls.Add(CreateKeyValueBlock("Sesion", _sessionValueLabel), 0, 1);
        layout.Controls.Add(CreateKeyValueBlock("Unidad", _mountValueLabel), 0, 3);
        layout.Controls.Add(CreateSectionTitle("Log"), 0, 6);
        _statusValueLabel.ForeColor = MutedTextColor;
        _statusValueLabel.Padding = new Padding(0, 8, 0, 0);
        layout.Controls.Add(_statusValueLabel, 0, 7);

        panel.Controls.Add(layout);
        return panel;
    }

    private Control CreateSettingsPanel()
    {
        var panel = CreateSurfacePanel(new Padding(22));

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 6,
            ColumnCount = 1,
            BackColor = SurfaceColor,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(CreateSectionTitle("Configuracion de montaje"), 0, 0);
        layout.Controls.Add(CreateConfigFields(), 0, 1);
        layout.Controls.Add(CreateSessionOptions(), 0, 2);
        layout.Controls.Add(CreateMainActions(), 0, 3);
        layout.Controls.Add(CreateAccountActions(), 0, 4);

        panel.Controls.Add(layout);
        return panel;
    }

    private Control CreateConfigFields()
    {
        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 2,
            BackColor = SurfaceColor,
            Padding = new Padding(0, 14, 0, 0),
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        fields.Controls.Add(CreateFieldLabel("Letra de unidad"), 0, 0);
        fields.Controls.Add(_driveComboBox, 1, 0);
        fields.Controls.Add(CreateFieldLabel("Nombre visible"), 0, 1);
        fields.Controls.Add(_volumeLabelTextBox, 1, 1);
        return fields;
    }

    private Control CreateSessionOptions()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceColor,
            Padding = new Padding(0, 8, 0, 0),
        };
        _forceLoginCheckBox.ForeColor = TextColor;
        _forceLoginCheckBox.Location = new Point(0, 12);
        panel.Controls.Add(_forceLoginCheckBox);
        return panel;
    }

    private Control CreateMainActions()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = SurfaceColor,
            Padding = new Padding(0, 12, 0, 0),
        };
        panel.Controls.Add(_mountButton);
        panel.Controls.Add(_openButton);
        panel.Controls.Add(_unmountButton);
        return panel;
    }

    private Control CreateAccountActions()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            BackColor = SurfaceColor,
            Padding = new Padding(0, 8, 0, 0),
        };
        panel.Controls.Add(_loginButton);
        panel.Controls.Add(_logoutButton);
        return panel;
    }

    private Control CreateFooter()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AppBackColor,
            Padding = new Padding(0, 8, 0, 0),
        };
        _trayHintLabel.Text = "Al minimizar, O2 Cloud Drive permanece activo en la bandeja del sistema.";
        _trayHintLabel.ForeColor = MutedTextColor;
        _trayHintLabel.AutoSize = true;
        _trayHintLabel.Location = new Point(2, 8);
        panel.Controls.Add(_trayHintLabel);
        return panel;
    }

    private static Control CreateCopyrightFooter()
    {
        return new Label
        {
            Text = "(C) Chemys 2026",
            Dock = DockStyle.Fill,
            ForeColor = MutedTextColor,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.BottomRight,
            Margin = new Padding(0),
        };
    }

    private static Panel CreateSurfacePanel(Padding padding)
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = SurfaceColor,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = padding,
        };
    }

    private static Label CreateSectionTitle(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 11F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TextColor,
            Margin = new Padding(0, 0, 0, 10),
        };
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = MutedTextColor,
            Margin = new Padding(0, 0, 10, 0),
        };
    }

    private static Control CreateKeyValueBlock(string label, Label valueLabel)
    {
        var panel = new Panel
        {
            AutoSize = true,
            Dock = DockStyle.Top,
            Padding = new Padding(0, 6, 0, 0),
            BackColor = SurfaceColor,
        };
        var labelControl = new Label
        {
            Text = label,
            AutoSize = true,
            ForeColor = MutedTextColor,
            Location = new Point(0, 0),
        };
        valueLabel.Location = new Point(0, 24);
        valueLabel.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Regular, GraphicsUnit.Point);
        panel.Height = 54;
        panel.Controls.Add(labelControl);
        panel.Controls.Add(valueLabel);
        return panel;
    }

    private static Button CreateButton(string text, Color backColor, Color foreColor, Color? borderColor = null)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = true,
            Height = 34,
            MinimumSize = new Size(112, 34),
            Padding = new Padding(12, 4, 12, 4),
            Margin = new Padding(0, 0, 10, 10),
            BackColor = backColor,
            ForeColor = foreColor,
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = borderColor ?? backColor;
        button.FlatAppearance.BorderSize = 1;
        return button;
    }

    private static void ApplyButtonStyle(Button button, Color backColor, Color foreColor, Color? borderColor = null)
    {
        button.BackColor = backColor;
        button.ForeColor = foreColor;
        button.FlatAppearance.BorderColor = borderColor ?? backColor;
    }

    private ContextMenuStrip CreateTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Abrir", null, (_, _) => ShowFromTray());
        menu.Items.Add("Montar", null, async (_, _) => await MountAsync());
        menu.Items.Add("Desmontar", null, async (_, _) => await UnmountAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Logout", null, async (_, _) => await LogoutAsync());
        menu.Items.Add("Salir", null, async (_, _) => await ExitAsync());
        menu.Opening += (_, _) =>
        {
            menu.Items[1].Enabled = !_services.MountService.IsMounted && !_busy;
            menu.Items[2].Enabled = _services.MountService.IsMounted && !_busy;
            menu.Items[4].Enabled = !_busy;
        };
        return menu;
    }

    private void LoadInitialValues(AppConfig config)
    {
        _driveComboBox.Items.Clear();
        foreach (var drive in AvailableDriveLetters(config.MountPoint))
        {
            _driveComboBox.Items.Add(drive);
        }

        var preferred = NormalizeMountPoint(config.MountPoint);
        _driveComboBox.SelectedItem = _driveComboBox.Items.Contains(preferred)
            ? preferred
            : _driveComboBox.Items.Count > 0 ? _driveComboBox.Items[0] : preferred;
        _volumeLabelTextBox.Text = string.IsNullOrWhiteSpace(config.VolumeLabel) ? "O2 Cloud" : config.VolumeLabel;
    }

    private async Task LoginOrLogoutAsync()
    {
        if (_services.AuthService.HasStoredSession() || _services.AuthService.HasValidatedSession)
        {
            await LogoutAsync();
            return;
        }

        await LoginAsync();
    }

    private async Task LoginAsync()
    {
        await RunUiActionAsync("auth:login:start", async () =>
        {
            var session = _services.AuthService.EnsureAuthenticated(allowInteractive: true, forceInteractive: true);
            if (session is not { IsAuthenticated: true })
            {
                throw new InvalidOperationException("No hay una sesion valida de O2 Cloud.");
            }

            await Task.CompletedTask;
            return "auth:login:ok";
        });
    }

    private async Task MountAsync()
    {
        if (_services.MountService.IsMounted)
        {
            ShowFromTray();
            return;
        }

        var mountPoint = SelectedMountPoint();
        var volumeLabel = _volumeLabelTextBox.Text.Trim();
        await RunUiActionAsync($"mount:start {mountPoint}", async () =>
        {
            if (!_services.Config.UseSimulatedData && _services.Config.RequireAuthentication)
            {
                var session = _services.AuthService.EnsureAuthenticated(
                    allowInteractive: true,
                    forceInteractive: _forceLoginCheckBox.Checked);
                if (session is not { IsAuthenticated: true })
                {
                    throw new InvalidOperationException("No hay una sesion valida de O2 Cloud.");
                }
            }

            var options = new DriveMountOptions(mountPoint, volumeLabel, _services.Config.UseSimulatedData);
            await Task.Run(() => _services.MountService.Mount(options));
            _notifyIcon.ShowBalloonTip(1500, "O2 Cloud Drive", $"Unidad montada en {mountPoint}", ToolTipIcon.Info);
            if (!_services.Config.UseSimulatedData && _services.MountService.LastRootItemCount == 0)
            {
                return $"mount:ok-empty-root {mountPoint}";
            }

            return $"mount:ok {mountPoint}";
        });
    }

    private async Task UnmountAsync()
    {
        if (!_services.MountService.IsMounted)
        {
            UpdateState("mount:not-mounted");
            return;
        }

        var mountPoint = _services.MountService.MountPoint ?? "la unidad";
        await RunUiActionAsync($"mount:unmount:start {mountPoint}", async () =>
        {
            await Task.Run(() => _services.MountService.Unmount());
            _notifyIcon.ShowBalloonTip(1500, "O2 Cloud Drive", "Unidad desmontada.", ToolTipIcon.Info);
            return "mount:unmount:ok";
        });
    }

    private async Task LogoutAsync()
    {
        await RunUiActionAsync("auth:logout:start", async () =>
        {
            if (_services.MountService.IsMounted)
            {
                await Task.Run(() => _services.MountService.Unmount());
            }

            _services.AuthService.Logout();
            _forceLoginCheckBox.Checked = false;
            return "auth:logout:ok";
        });
    }

    private async Task ExitAsync()
    {
        _allowClose = true;
        if (_services.MountService.IsMounted)
        {
            await UnmountAsync();
        }

        Close();
    }

    private void OnTransferProgress(object? sender, O2TransferProgress progress)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => OnTransferProgress(sender, progress)));
            }
            catch (InvalidOperationException)
            {
            }

            return;
        }

        var message = TransferLogMessage(progress);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _statusValueLabel.Text = message;
        _statusValueLabel.ForeColor = progress.Phase switch
        {
            "completed" => SuccessColor,
            "accepted" => WarningColor,
            "failed" => DangerColor,
            _ => MutedTextColor,
        };
        if (IsTransferLogPhase(progress.Phase))
        {
            UpsertTransferLog(progress.FileName, message, IsTerminalTransferPhase(progress.Phase));
        }

        ShowTransferNotification(progress);
    }

    private string TransferLogMessage(O2TransferProgress progress)
    {
        var fileName = string.IsNullOrWhiteSpace(progress.FileName) ? "archivo" : progress.FileName;
        var key = fileName;
        return progress.Phase switch
        {
            "start" => StartTransferLog(key, fileName, progress.TotalBytes),
            "progress" => ProgressTransferLog(key, fileName, progress.BytesTransferred, progress.TotalBytes, progress.BytesPerSecond),
            "confirming" => $"Subida: {fileName} enviada; esperando confirmacion y refresco de carpeta en O2 Cloud.",
            "waiting" => $"Subida: {fileName} enviada; O2 Cloud sigue procesando el archivo ({progress.BytesTransferred}s esperando confirmacion).",
            "completed" => CompleteTransferLog(key, $"Subida concluida: {fileName} 100% enviado y confirmado por O2 Cloud."),
            "accepted" => CompleteTransferLog(key, $"Subida concluida: {fileName} 100% enviado; O2 Cloud lo acepto y queda pendiente de listado remoto."),
            "failed" => CompleteTransferLog(key, FailureTransferLog(fileName, progress.Message)),
            _ => string.Empty,
        };
    }

    private static bool IsTransferLogPhase(string phase)
    {
        return phase is "start" or "progress" or "confirming" or "waiting" or "completed" or "accepted" or "failed";
    }

    private static bool IsTerminalTransferPhase(string phase)
    {
        return phase is "completed" or "accepted" or "failed";
    }

    private void ShowTransferNotification(O2TransferProgress progress)
    {
        if (!progress.Phase.Equals("completed", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var fileName = string.IsNullOrWhiteSpace(progress.FileName) ? "archivo" : progress.FileName;
        try
        {
            _notifyIcon.ShowBalloonTip(
                3500,
                "O2 Cloud Drive",
                $"Subida completada: {fileName}",
                ToolTipIcon.Info);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string FailureTransferLog(string fileName, string? reason)
    {
        return string.IsNullOrWhiteSpace(reason)
            ? $"Subida fallida: {fileName}."
            : $"Subida fallida: {fileName}. {reason}";
    }

    private string StartTransferLog(string key, string fileName, long totalBytes)
    {
        _lastTransferPercentLogged[key] = 0;
        _lastTransferLogAt[key] = DateTimeOffset.UtcNow;
        return $"Subida iniciada: {fileName} 0% de {FormatBytes(totalBytes)} a {FormatRate(0)}.";
    }

    private string ProgressTransferLog(string key, string fileName, long bytesTransferred, long totalBytes, long bytesPerSecond)
    {
        if (totalBytes <= 0)
        {
            return string.Empty;
        }

        var percent = (int)Math.Clamp(Math.Floor(bytesTransferred * 100d / totalBytes), 0, 100);
        var now = DateTimeOffset.UtcNow;
        var percentChanged = !_lastTransferPercentLogged.TryGetValue(key, out var lastPercent) || percent > lastPercent;
        var timeElapsed = !_lastTransferLogAt.TryGetValue(key, out var lastAt) || now - lastAt >= TimeSpan.FromSeconds(5);
        if (!percentChanged && !timeElapsed)
        {
            return string.Empty;
        }

        if (percentChanged)
        {
            _lastTransferPercentLogged[key] = percent;
        }

        _lastTransferLogAt[key] = now;
        return $"Subida: {fileName} {percent}% enviado ({FormatBytes(bytesTransferred)} de {FormatBytes(totalBytes)}) a {FormatRate(bytesPerSecond)}.";
    }

    private string CompleteTransferLog(string key, string message)
    {
        _lastTransferPercentLogged.Remove(key);
        _lastTransferLogAt.Remove(key);
        return message;
    }

    private static string FormatRate(long bytesPerSecond)
    {
        return bytesPerSecond <= 0 ? "0 KB/s" : $"{FormatBytes(bytesPerSecond)}/s";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        var scaled = (double)value;
        while (scaled >= 1024 && unit < units.Length - 1)
        {
            scaled /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value} {units[unit]}" : $"{scaled:0.##} {units[unit]}";
    }

    private async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason == CloseReason.WindowsShutDown)
        {
            _services.ApiClient.TransferProgress -= OnTransferProgress;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _services.Dispose();
            return;
        }

        e.Cancel = true;
        await ExitAsync();
    }

    private async Task RunUiActionAsync(string pendingStatus, Func<Task<string>> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        UpdateState(pendingStatus);
        try
        {
            SetControlsEnabled(false);
            var message = await action();
            UpdateState(message);
        }
        catch (Exception ex)
        {
            UpdateState($"error:{ex.GetType().Name}");
            MessageBox.Show(this, ex.Message, "O2 Cloud Drive", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            UpdateState(_lastStatus, appendLog: false);
        }
    }

    private void SetControlsEnabled(bool enabled)
    {
        var mounted = _services.MountService.IsMounted;
        _driveComboBox.Enabled = enabled && !mounted;
        _volumeLabelTextBox.Enabled = enabled && !mounted;
        _forceLoginCheckBox.Enabled = enabled && !mounted && !_services.Config.UseSimulatedData;
        _loginButton.Enabled = enabled && !_services.Config.UseSimulatedData;
        _mountButton.Enabled = enabled && !mounted;
        _openButton.Enabled = enabled && mounted;
        _unmountButton.Enabled = enabled && mounted;
        _logoutButton.Enabled = enabled && !_services.Config.UseSimulatedData;
    }

    private void UpdateState(string status, bool appendLog = true)
    {
        _lastStatus = status;
        var mounted = _services.MountService.IsMounted;
        var hasValidatedSession = _services.AuthService.HasValidatedSession;
        var hasStoredSession = _services.AuthService.HasStoredSession();
        _sessionValueLabel.Text = _services.Config.UseSimulatedData
            ? "Simulado"
            : hasValidatedSession ? "Iniciada"
            : hasStoredSession ? "Guardada"
            : "No iniciada";
        _sessionValueLabel.ForeColor = hasValidatedSession ? SuccessColor : hasStoredSession ? WarningColor : MutedTextColor;
        _mountValueLabel.Text = mounted ? _services.MountService.MountPoint ?? "-" : "-";
        _mountValueLabel.ForeColor = mounted ? SuccessColor : SidebarMutedColor;
        _loginButton.Text = hasStoredSession || hasValidatedSession ? "Logout" : "Login";
        ApplyButtonStyle(
            _loginButton,
            hasStoredSession || hasValidatedSession ? DangerColor : PrimaryColor,
            Color.White,
            hasStoredSession || hasValidatedSession ? DangerColor : PrimaryColor);
        if (appendLog)
        {
            AppendLog(status);
        }

        SetControlsEnabled(!_busy);
    }

    private void AppendLog(string message)
    {
        var text = TechnicalLogMessage(message);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        if (_logLines.Count > 0 && _logLines[^1].EndsWith(text, StringComparison.Ordinal))
        {
            return;
        }

        _logLines.Add(line);
        TrimLogLines();

        RefreshLogTextBox();
    }

    private void UpsertTransferLog(string fileName, string message, bool terminal)
    {
        var text = TechnicalLogMessage(message);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var key = string.IsNullOrWhiteSpace(fileName) ? "archivo" : fileName;
        var line = $"{DateTime.Now:HH:mm:ss}  {text}";
        if (_liveTransferLogIndex.TryGetValue(key, out var index) &&
            index >= 0 &&
            index < _logLines.Count)
        {
            _logLines[index] = line;
        }
        else
        {
            _liveTransferLogIndex[key] = _logLines.Count;
            _logLines.Add(line);
            TrimLogLines();
        }

        if (terminal)
        {
            _liveTransferLogIndex.Remove(key);
        }

        RefreshLogTextBox();
    }

    private void TrimLogLines()
    {
        while (_logLines.Count > 80)
        {
            _logLines.RemoveAt(0);
            foreach (var pair in _liveTransferLogIndex.ToArray())
            {
                if (pair.Value <= 0)
                {
                    _liveTransferLogIndex.Remove(pair.Key);
                }
                else
                {
                    _liveTransferLogIndex[pair.Key] = pair.Value - 1;
                }
            }
        }
    }

    private void RefreshLogTextBox()
    {
        _logTextBox.Lines = _logLines.ToArray();
        _logTextBox.SelectionStart = _logTextBox.TextLength;
        _logTextBox.ScrollToCaret();
    }

    private static string TechnicalLogMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        if (message.StartsWith("mount:start ", StringComparison.OrdinalIgnoreCase))
        {
            return $"Montaje: validando sesion y creando la unidad {message["mount:start ".Length..]}.";
        }

        if (message.StartsWith("mount:ok-empty-root ", StringComparison.OrdinalIgnoreCase))
        {
            return $"Montaje: unidad {message["mount:ok-empty-root ".Length..]} creada, pero O2 devolvio la raiz vacia.";
        }

        if (message.StartsWith("mount:ok ", StringComparison.OrdinalIgnoreCase))
        {
            return $"Montaje: unidad {message["mount:ok ".Length..]} activa.";
        }

        if (message.StartsWith("mount:unmount:start ", StringComparison.OrdinalIgnoreCase))
        {
            return $"Desmontaje: cerrando la unidad {message["mount:unmount:start ".Length..]}.";
        }

        if (message.StartsWith("error:", StringComparison.OrdinalIgnoreCase))
        {
            return $"Error: {message["error:".Length..]}. Revisa el mensaje mostrado por la aplicacion.";
        }

        return message switch
        {
            "Listo." => "Preparada.",
            "auth:stored-session" => "Sesion guardada detectada en el equipo.",
            "auth:login:start" => "Login: abriendo navegador integrado; se conserva el estado web para que el SMS de O2 no se reinicie entre intentos.",
            "auth:login:ok" => "Login: sesion validada contra O2 Cloud.",
            "auth:logout:start" => "Logout: desmontando si hace y borrando la sesion local.",
            "auth:logout:ok" => "Logout: limpieza local completada; no queda sesion guardada por la app.",
            "mount:not-mounted" => "Desmontaje: no hay unidad montada.",
            "mount:unmount:ok" => "Desmontaje: unidad liberada.",
            _ => message,
        };
    }

    private void OpenMountedDrive()
    {
        if (!_services.MountService.IsMounted || string.IsNullOrWhiteSpace(_services.MountService.MountPoint))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = _services.MountService.MountPoint + "\\",
            UseShellExecute = true,
        });
    }

    private void HideToTray()
    {
        Hide();
        WindowState = FormWindowState.Normal;
        _notifyIcon.ShowBalloonTip(1500, "O2 Cloud Drive", "La aplicacion sigue activa en la bandeja.", ToolTipIcon.Info);
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private string SelectedMountPoint()
    {
        return NormalizeMountPoint(_driveComboBox.SelectedItem?.ToString() ?? _services.Config.MountPoint);
    }

    private static IReadOnlyList<string> AvailableDriveLetters(string preferredMountPoint)
    {
        var preferred = NormalizeMountPoint(preferredMountPoint);
        var used = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();

        var letters = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(value => $"{(char)value}:")
            .Where(value => !used.Contains(value[0]))
            .ToList();

        if (letters.Count == 0)
        {
            letters.Add(preferred);
        }

        return letters
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeMountPoint(string mountPoint)
    {
        var trimmed = mountPoint.Trim().TrimEnd('\\');
        return trimmed.EndsWith(":", StringComparison.Ordinal) ? trimmed : $"{trimmed}:";
    }
}
