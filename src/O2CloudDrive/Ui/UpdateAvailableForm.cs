using System.Diagnostics;
using O2CloudDrive.Updates;

namespace O2CloudDrive.Ui;

public sealed class UpdateAvailableForm : Form
{
    private static readonly Color AppBackColor = Color.FromArgb(229, 235, 241);
    private static readonly Color SurfaceColor = Color.FromArgb(248, 250, 252);
    private static readonly Color TextColor = Color.FromArgb(27, 34, 42);
    private static readonly Color MutedTextColor = Color.FromArgb(88, 100, 114);
    private static readonly Color BorderColor = Color.FromArgb(202, 211, 222);
    private static readonly Color PrimaryColor = Color.FromArgb(0, 112, 150);
    private static readonly Color SuccessColor = Color.FromArgb(34, 153, 92);

    private readonly UpdateRelease _release;

    public UpdateAvailableForm(UpdateRelease release)
    {
        _release = release;

        Text = "Actualizacion disponible";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = AppBackColor;
        ClientSize = new Size(740, 520);
        MinimumSize = new Size(660, 440);
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
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        root.Controls.Add(CreateHeader(), 0, 0);
        root.Controls.Add(CreateVersionSummary(), 0, 1);

        var notes = CreateReleaseNotesBox();
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

    private Control CreateHeader()
    {
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = AppBackColor,
        };
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        header.Controls.Add(new Label
        {
            Text = "Nueva version disponible",
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 15F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = TextColor,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        header.Controls.Add(new Label
        {
            Text = "Revisa los cambios y descarga el instalador que necesites.",
            Dock = DockStyle.Fill,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 1);

        return header;
    }

    private Control CreateVersionSummary()
    {
        var summary = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = AppBackColor,
            Padding = new Padding(0, 8, 0, 10),
        };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        summary.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        summary.Controls.Add(CreateVersionCard("Version instalada", AppVersion.DisplayVersion, MutedTextColor), 0, 0);
        summary.Controls.Add(CreateVersionCard("Version disponible", _release.Name, SuccessColor), 1, 0);
        return summary;
    }

    private static Control CreateVersionCard(string label, string value, Color valueColor)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = SurfaceColor,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 10, 0),
        };
        panel.CellBorderStyle = TableLayoutPanelCellBorderStyle.None;
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            ForeColor = MutedTextColor,
            Font = new Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);

        panel.Controls.Add(new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            ForeColor = valueColor,
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Regular, GraphicsUnit.Point),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        }, 0, 1);

        return panel;
    }

    private RichTextBox CreateReleaseNotesBox()
    {
        var notes = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Color.White,
            ForeColor = TextColor,
            Font = new Font("Segoe UI", 9.5F, FontStyle.Regular, GraphicsUnit.Point),
            ScrollBars = RichTextBoxScrollBars.Vertical,
            DetectUrls = true,
            TabStop = false,
        };

        FillReleaseNotes(notes);
        notes.SelectionStart = 0;
        notes.SelectionLength = 0;
        return notes;
    }

    private void FillReleaseNotes(RichTextBox notes)
    {
        var sections = ParseReleaseSections(_release.Body);
        if (sections.Count == 0)
        {
            AppendSectionTitle(notes, "Notas de la version");
            AppendBodyLine(notes, "Abre la release para ver los cambios publicados.");
            return;
        }

        foreach (var section in sections)
        {
            AppendSectionTitle(notes, section.Title);
            foreach (var line in section.Lines)
            {
                AppendBodyLine(notes, line);
            }

            AppendBlankLine(notes);
        }
    }

    private static List<ReleaseSection> ParseReleaseSections(string? body)
    {
        var normalized = NormalizeReleaseBody(body);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var sections = new List<ReleaseSection>();
        ReleaseSection? current = null;
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (sections.Count == 0 &&
                current is null &&
                line.StartsWith("O2 Cloud Drive ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsSectionTitle(line))
            {
                current = new ReleaseSection(CleanSectionTitle(line));
                sections.Add(current);
                continue;
            }

            current ??= AddSection(sections, "Notas de la version");
            current.Lines.Add(CleanBodyLine(line));
        }

        return sections.Where(section => section.Lines.Count > 0).ToList();
    }

    private static ReleaseSection AddSection(List<ReleaseSection> sections, string title)
    {
        var section = new ReleaseSection(title);
        sections.Add(section);
        return section;
    }

    private static string NormalizeReleaseBody(string? body)
    {
        return string.IsNullOrWhiteSpace(body)
            ? string.Empty
            : body.Trim('\uFEFF', ' ', '\r', '\n', '\t')
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
    }

    private static bool IsSectionTitle(string line)
    {
        return line.StartsWith("#", StringComparison.Ordinal) ||
               (line.EndsWith(":", StringComparison.Ordinal) &&
                line.Length <= 80 &&
                !line.StartsWith("-", StringComparison.Ordinal));
    }

    private static string CleanSectionTitle(string line)
    {
        return line.Trim().TrimStart('#').Trim().TrimEnd(':').Trim();
    }

    private static string CleanBodyLine(string line)
    {
        var trimmed = line.Trim();
        return trimmed.StartsWith("- ", StringComparison.Ordinal) ||
               trimmed.StartsWith("* ", StringComparison.Ordinal)
            ? "- " + trimmed[2..].Trim()
            : trimmed;
    }

    private static void AppendSectionTitle(RichTextBox notes, string title)
    {
        notes.SelectionFont = new Font("Segoe UI Semibold", 10.5F, FontStyle.Regular, GraphicsUnit.Point);
        notes.SelectionColor = PrimaryColor;
        notes.AppendText(title + Environment.NewLine);
        notes.SelectionFont = notes.Font;
        notes.SelectionColor = TextColor;
    }

    private static void AppendBodyLine(RichTextBox notes, string line)
    {
        notes.SelectionFont = notes.Font;
        notes.SelectionColor = TextColor;
        notes.AppendText(line + Environment.NewLine);
    }

    private static void AppendBlankLine(RichTextBox notes)
    {
        notes.AppendText(Environment.NewLine);
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

    private sealed class ReleaseSection(string title)
    {
        public string Title { get; } = title;
        public List<string> Lines { get; } = [];
    }
}
