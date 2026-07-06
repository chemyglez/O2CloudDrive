namespace O2CloudDrive.Setup;

internal sealed class InstallerForm : Form
{
    private readonly float _scale;
    private readonly Label _titleLabel;
    private readonly TextBox _pathTextBox;
    private readonly Button _browseButton;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _installButton;
    private readonly Button _closeButton;

    public static int ExitCode { get; private set; }
    public static bool NeedsReboot { get; set; }

    public InstallerForm()
    {
        using var graphics = CreateGraphics();
        _scale = Math.Max(1F, graphics.DpiX / 96F);

        AutoScaleMode = AutoScaleMode.None;
        Text = "O2 Cloud Drive 0.8.3 beta";
        ClientSize = ScaledSize(760, 330);
        MinimumSize = SizeFromClientSize(ClientSize);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 250, 252);
        Font = new Font("Segoe UI", 9F);

        var appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        if (appIcon is not null)
        {
            Icon = appIcon;
        }

        var logo = new PictureBox
        {
            Image = appIcon?.ToBitmap(),
            Location = ScaledPoint(30, 27),
            Size = ScaledSize(38, 38),
            SizeMode = PictureBoxSizeMode.StretchImage
        };

        _titleLabel = new Label
        {
            AutoSize = false,
            Text = "Instalar O2 Cloud Drive",
            Font = new Font("Segoe UI Semibold", 12.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(17, 31, 44),
            Location = ScaledPoint(82, 25),
            Size = ScaledSize(620, 34),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var pathLabel = new Label
        {
            AutoSize = false,
            Text = "Carpeta de instalacion",
            ForeColor = Color.FromArgb(75, 91, 107),
            Location = ScaledPoint(32, 92),
            Size = ScaledSize(650, 24),
            TextAlign = ContentAlignment.BottomLeft
        };

        _pathTextBox = new TextBox
        {
            Text = Program.DefaultInstallDir,
            Location = ScaledPoint(32, 124),
            Size = ScaledSize(520, 30),
            Font = new Font("Segoe UI", 9.5F)
        };

        _browseButton = CreateSecondaryButton("Cambiar...");
        _browseButton.Location = ScaledPoint(568, 121);
        _browseButton.Size = ScaledSize(152, 36);
        _browseButton.Click += OnBrowseClicked;

        _statusLabel = new Label
        {
            AutoEllipsis = true,
            Text = "",
            ForeColor = Color.FromArgb(75, 91, 107),
            Location = ScaledPoint(32, 184),
            Size = ScaledSize(688, 28),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _progressBar = new ProgressBar
        {
            Location = ScaledPoint(32, 218),
            Size = ScaledSize(688, 20),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Visible = false
        };

        _installButton = CreatePrimaryButton("Instalar");
        _installButton.Location = ScaledPoint(468, 270);
        _installButton.Click += OnInstallClicked;

        _closeButton = CreateSecondaryButton("Cancelar");
        _closeButton.Location = ScaledPoint(596, 270);
        _closeButton.Click += (_, _) => Close();

        Controls.AddRange([
            logo,
            _titleLabel,
            pathLabel,
            _pathTextBox,
            _browseButton,
            _statusLabel,
            _progressBar,
            _installButton,
            _closeButton
        ]);
    }

    private Button CreatePrimaryButton(string text)
    {
        var button = CreateBaseButton(text);
        button.ForeColor = Color.White;
        button.BackColor = Color.FromArgb(0, 126, 140);
        button.FlatAppearance.BorderColor = Color.FromArgb(0, 126, 140);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 111, 124);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 94, 105);
        return button;
    }

    private Button CreateSecondaryButton(string text)
    {
        var button = CreateBaseButton(text);
        button.ForeColor = Color.FromArgb(17, 31, 44);
        button.BackColor = Color.White;
        button.FlatAppearance.BorderColor = Color.FromArgb(174, 190, 205);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(236, 242, 247);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(224, 233, 241);
        return button;
    }

    private Button CreateBaseButton(string text)
    {
        return new Button
        {
            Text = text,
            Size = ScaledSize(112, 36),
            Font = new Font("Segoe UI Semibold", 9F),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
    }

    private void OnBrowseClicked(object? sender, EventArgs e)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Elige donde instalar O2 Cloud Drive",
            SelectedPath = GetExistingFolderForDialog(_pathTextBox.Text),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _pathTextBox.Text = Program.FolderSelectionToInstallDir(dialog.SelectedPath);
    }

    private async void OnInstallClicked(object? sender, EventArgs e)
    {
        var installDir = Program.NormalizeInstallDir(_pathTextBox.Text);

        try
        {
            Program.ValidateInstallDir(installDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "O2 Cloud Drive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            _pathTextBox.Focus();
            return;
        }

        var progress = new Progress<InstallProgress>(UpdateProgress);
        SetInstallingState(true);

        try
        {
            await Program.InstallAsync(installDir, progress, CancellationToken.None);
            ExitCode = 0;
            _titleLabel.Text = "Instalacion completada";
            _statusLabel.Text = NeedsReboot
                ? "Instalado. Reinicia Windows antes de montar la unidad."
                : "O2 Cloud Drive esta listo para usar.";
            _progressBar.Value = 100;

            MessageBox.Show(
                NeedsReboot
                    ? "O2 Cloud Drive 0.8.3 beta se ha instalado correctamente. WinFsp ha pedido reiniciar Windows antes de montar unidades."
                    : "O2 Cloud Drive 0.8.3 beta se ha instalado correctamente.",
                "O2 Cloud Drive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ExitCode = 1;
            _titleLabel.Text = "No se pudo instalar";
            _statusLabel.Text = ex.Message;
            MessageBox.Show(
                "No se pudo instalar O2 Cloud Drive." + Environment.NewLine + Environment.NewLine + ex.Message,
                "O2 Cloud Drive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            SetInstallingState(false);
            _closeButton.Text = "Cerrar";
            _closeButton.Focus();
        }
    }

    private void SetInstallingState(bool installing)
    {
        _pathTextBox.Enabled = !installing;
        _browseButton.Enabled = !installing;
        _installButton.Enabled = !installing;
        _installButton.Visible = !installing;
        _closeButton.Enabled = !installing;
        _progressBar.Visible = true;
        _statusLabel.Text = installing ? "Preparando instalacion..." : _statusLabel.Text;
    }

    private void UpdateProgress(InstallProgress progress)
    {
        _progressBar.Value = Math.Max(_progressBar.Minimum, Math.Min(_progressBar.Maximum, progress.Percent));
        _statusLabel.Text = progress.Message;
    }

    private Point ScaledPoint(int x, int y) => new(ScaleValue(x), ScaleValue(y));

    private Size ScaledSize(int width, int height) => new(ScaleValue(width), ScaleValue(height));

    private int ScaleValue(int value) => (int)Math.Round(value * _scale);

    private static string GetExistingFolderForDialog(string currentPath)
    {
        var path = Program.NormalizeInstallDir(currentPath);
        while (!string.IsNullOrWhiteSpace(path) && !Directory.Exists(path))
        {
            path = Path.GetDirectoryName(path) ?? "";
        }

        return string.IsNullOrWhiteSpace(path) ? Program.DefaultInstallDir : path;
    }
}
