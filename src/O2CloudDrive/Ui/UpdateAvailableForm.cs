using System.Diagnostics;
using O2CloudDrive.Updates;

namespace O2CloudDrive.Ui;

public sealed class UpdateAvailableForm : Form
{
    private static readonly Color AppBackColor = Color.FromArgb(229, 235, 241);
    private static readonly Color TextColor = Color.FromArgb(27, 34, 42);
    private static readonly Color MutedTextColor = Color.FromArgb(88, 100, 114);
    private static readonly Color BorderColor = Color.FromArgb(202, 211, 222);
    private static readonly Color PrimaryColor = Color.FromArgb(0, 112, 150);

    private readonly UpdateRelease _release;

    public UpdateAvailableForm(UpdateRelease release)
    {
        _release = release;

        Text = "Actualizacion disponible";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = AppBackColor;
        ClientSize = new Size(500, 210);
        Font = new Font("Segoe UI", 10F, FontStyle.Regular, GraphicsUnit.Point);
        Icon = AppIcon.Load();

        BuildLayout();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 4,
            ColumnCount = 1,
            Padding = new Padding(22, 18, 22, 18),
            BackColor = AppBackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));

        root.Controls.Add(new Label
        {
            Text = "Nueva version disponible",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 15F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TextColor,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        root.Controls.Add(new Label
        {
            Text = $"Instalada: {AppVersion.DisplayVersion}\r\nDisponible: {_release.Name}",
            Dock = DockStyle.Fill,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 1);

        var notes = new TextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = TextColor,
            Text = string.IsNullOrWhiteSpace(_release.Body)
                ? "Abre la release para ver los cambios publicados."
                : _release.Body,
        };
        root.Controls.Add(notes, 0, 2);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = AppBackColor,
            Padding = new Padding(0, 8, 0, 0),
        };

        var closeButton = CreateButton("Cerrar", Color.White, TextColor, BorderColor);
        var releaseButton = CreateButton("Ver release", Color.White, TextColor, BorderColor);
        var downloadButton = CreateButton("Descargar", PrimaryColor, Color.White, PrimaryColor);

        closeButton.Click += (_, _) => Close();
        releaseButton.Click += (_, _) => OpenUrl(_release.HtmlUrl);
        downloadButton.Enabled = !string.IsNullOrWhiteSpace(_release.InstallerDownloadUrl);
        downloadButton.Click += (_, _) => OpenUrl(_release.InstallerDownloadUrl ?? _release.HtmlUrl);

        actions.Controls.Add(closeButton);
        actions.Controls.Add(releaseButton);
        actions.Controls.Add(downloadButton);
        root.Controls.Add(actions, 0, 3);

        Controls.Add(root);
    }

    private static Button CreateButton(string text, Color backColor, Color foreColor, Color borderColor)
    {
        var button = new Button
        {
            Text = text,
            Width = 112,
            Height = 34,
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = foreColor,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = borderColor;
        return button;
    }

    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        });
    }
}
