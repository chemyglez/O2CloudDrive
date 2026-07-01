namespace O2CloudDrive.Uninstall;

internal sealed class UninstallForm : Form
{
    private readonly float _scale;
    private readonly UninstallOptions _options;
    private readonly Label _titleLabel;
    private readonly Label _statusLabel;
    private readonly ProgressBar _progressBar;
    private readonly Button _uninstallButton;
    private readonly Button _cancelButton;

    public static int ExitCode { get; private set; }

    public UninstallForm(UninstallOptions options)
    {
        _options = options;

        using var graphics = CreateGraphics();
        _scale = Math.Max(1F, graphics.DpiX / 96F);

        AutoScaleMode = AutoScaleMode.None;
        Text = "Desinstalar O2 Cloud Drive";
        ClientSize = ScaledSize(760, 360);
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
            Text = options.Perform ? "Desinstalando O2 Cloud Drive" : "Desinstalar O2 Cloud Drive",
            Font = new Font("Segoe UI Semibold", 12.5F, FontStyle.Bold),
            ForeColor = Color.FromArgb(17, 31, 44),
            Location = ScaledPoint(82, 25),
            Size = ScaledSize(620, 34),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var descriptionLabel = new Label
        {
            AutoSize = false,
            Text = "Se eliminara la aplicacion instalada. La sesion de O2 Cloud se conserva.",
            ForeColor = Color.FromArgb(75, 91, 107),
            Location = ScaledPoint(32, 88),
            Size = ScaledSize(690, 42),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var pathTextBox = new TextBox
        {
            Text = _options.InstallDir,
            Location = ScaledPoint(32, 142),
            Size = ScaledSize(688, 30),
            Font = new Font("Segoe UI", 9.5F),
            ReadOnly = true
        };

        _statusLabel = new Label
        {
            AutoEllipsis = true,
            Text = options.Perform ? "Preparando desinstalacion..." : "",
            ForeColor = Color.FromArgb(75, 91, 107),
            Location = ScaledPoint(32, 204),
            Size = ScaledSize(688, 28),
            TextAlign = ContentAlignment.MiddleLeft
        };

        _progressBar = new ProgressBar
        {
            Location = ScaledPoint(32, 238),
            Size = ScaledSize(688, 20),
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Visible = options.Perform
        };

        _uninstallButton = CreatePrimaryButton("Desinstalar");
        _uninstallButton.Location = ScaledPoint(452, 300);
        _uninstallButton.Click += OnUninstallClicked;

        _cancelButton = CreateSecondaryButton(options.Perform ? "Cerrar" : "Cancelar");
        _cancelButton.Location = ScaledPoint(596, 300);
        _cancelButton.Click += (_, _) => Close();

        Controls.AddRange([
            logo,
            _titleLabel,
            descriptionLabel,
            pathTextBox,
            _statusLabel,
            _progressBar,
            _uninstallButton,
            _cancelButton
        ]);

        Shown += OnShown;
    }

    private Button CreatePrimaryButton(string text)
    {
        var button = CreateBaseButton(text);
        button.ForeColor = Color.White;
        button.BackColor = Color.FromArgb(199, 62, 44);
        button.FlatAppearance.BorderColor = Color.FromArgb(199, 62, 44);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(179, 52, 37);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(154, 43, 31);
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
            Size = ScaledSize(128, 36),
            Font = new Font("Segoe UI Semibold", 9F),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        if (_options.Quiet && !_options.Perform)
        {
            StageAndClose();
            return;
        }

        if (_options.Perform)
        {
            await RunUninstallAsync();
        }
    }

    private void OnUninstallClicked(object? sender, EventArgs e)
    {
        var result = MessageBox.Show(
            "Se desinstalara O2 Cloud Drive de este equipo. Puedes volver a instalarlo despues si lo necesitas.",
            "Desinstalar O2 Cloud Drive",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes)
        {
            return;
        }

        StageAndClose();
    }

    private void StageAndClose()
    {
        try
        {
            _uninstallButton.Enabled = false;
            _cancelButton.Enabled = false;
            _statusLabel.Text = "Solicitando permisos para desinstalar...";
            Program.StageAndRunElevated(_options);
            Close();
        }
        catch (Exception ex)
        {
            _uninstallButton.Enabled = true;
            _cancelButton.Enabled = true;
            _statusLabel.Text = ex.Message;
            MessageBox.Show(
                "No se pudo iniciar la desinstalacion." + Environment.NewLine + Environment.NewLine + ex.Message,
                "O2 Cloud Drive",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private async Task RunUninstallAsync()
    {
        _uninstallButton.Enabled = false;
        _uninstallButton.Visible = false;
        _cancelButton.Enabled = false;
        _progressBar.Visible = true;
        var progress = new Progress<UninstallProgress>(UpdateProgress);

        try
        {
            await Program.UninstallAsync(_options, progress, CancellationToken.None);
            ExitCode = 0;
            _titleLabel.Text = "Desinstalacion completada";
            _statusLabel.Text = "O2 Cloud Drive se ha desinstalado correctamente.";
            _progressBar.Value = 100;

            if (!_options.Quiet)
            {
                MessageBox.Show(
                    "O2 Cloud Drive se ha desinstalado correctamente.",
                    "O2 Cloud Drive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            ExitCode = 1;
            _titleLabel.Text = "No se pudo desinstalar";
            _statusLabel.Text = ex.Message;

            if (!_options.Quiet)
            {
                MessageBox.Show(
                    "No se pudo desinstalar O2 Cloud Drive." + Environment.NewLine + Environment.NewLine + ex.Message,
                    "O2 Cloud Drive",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        finally
        {
            _cancelButton.Text = "Cerrar";
            _cancelButton.Enabled = true;
            _cancelButton.Focus();

            if (_options.Quiet)
            {
                Close();
            }
        }
    }

    private void UpdateProgress(UninstallProgress progress)
    {
        _progressBar.Value = Math.Max(_progressBar.Minimum, Math.Min(_progressBar.Maximum, progress.Percent));
        _statusLabel.Text = progress.Message;
    }

    private Point ScaledPoint(int x, int y) => new(ScaleValue(x), ScaleValue(y));

    private Size ScaledSize(int width, int height) => new(ScaleValue(width), ScaleValue(height));

    private int ScaleValue(int value) => (int)Math.Round(value * _scale);
}
