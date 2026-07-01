using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace O2CloudDrive.Ui;

public sealed class ShareLinkForm : Form
{
    private static readonly Color BackPanelColor = Color.FromArgb(244, 248, 251);
    private static readonly Color TextColor = Color.FromArgb(27, 34, 42);
    private static readonly Color MutedColor = Color.FromArgb(84, 96, 109);
    private static readonly Color BorderColor = Color.FromArgb(178, 190, 204);
    private static readonly Color PrimaryColor = Color.FromArgb(0, 112, 150);
    private static readonly Color PrimaryHoverColor = Color.FromArgb(0, 128, 170);
    private static readonly Color DangerColor = Color.FromArgb(178, 52, 45);

    private readonly string _link;

    public ShareLinkForm(string fileName, string link)
    {
        _link = link;

        Text = "Enlace compartido";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(420, 82);
        MinimumSize = Size;
        Font = new Font("Segoe UI", 9.25F, FontStyle.Regular, GraphicsUnit.Point);
        BackColor = BackPanelColor;
        AutoScaleMode = AutoScaleMode.Dpi;
        Icon = AppIcon.Load();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(10, 8, 10, 8),
            BackColor = BackColor,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));

        var title = new Label
        {
            Text = fileName,
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Font = new Font("Segoe UI Semibold", 9.75F, FontStyle.Regular, GraphicsUnit.Point),
            ForeColor = MutedColor,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0),
        };

        var linkRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = BackColor,
            Margin = new Padding(0),
        };
        linkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        linkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 31));
        linkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 31));
        linkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 31));

        var linkBox = new LinkDisplay(link)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 1, 7, 1),
        };
        linkBox.Click += (_, _) => Clipboard.SetText(_link);

        var toolTip = new ToolTip
        {
            InitialDelay = 250,
            ReshowDelay = 100,
            ShowAlways = true,
        };

        var openButton = new IconButton(IconButtonKind.Open, Color.White, TextColor, BorderColor)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 1, 0, 1),
        };
        toolTip.SetToolTip(openButton, "Abrir enlace");
        openButton.Click += (_, _) => Process.Start(new ProcessStartInfo
        {
            FileName = _link,
            UseShellExecute = true,
        });

        var copyButton = new IconButton(IconButtonKind.Copy, PrimaryColor, Color.White, PrimaryColor)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 1, 0, 1),
            HoverBackColor = PrimaryHoverColor,
            PressedBackColor = Color.FromArgb(0, 95, 130),
        };
        toolTip.SetToolTip(copyButton, "Copiar enlace");
        copyButton.Click += (_, _) => Clipboard.SetText(_link);

        var closeButton = new IconButton(IconButtonKind.Close, Color.White, DangerColor, BorderColor)
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(3, 1, 0, 1),
        };
        toolTip.SetToolTip(closeButton, "Cerrar");
        closeButton.Click += (_, _) => Close();

        linkRow.Controls.Add(linkBox, 0, 0);
        linkRow.Controls.Add(openButton, 1, 0);
        linkRow.Controls.Add(copyButton, 2, 0);
        linkRow.Controls.Add(closeButton, 3, 0);

        root.Controls.Add(title, 0, 0);
        root.Controls.Add(linkRow, 0, 1);
        Controls.Add(root);

        AcceptButton = copyButton;
        CancelButton = closeButton;
    }

    private sealed class LinkDisplay : Control
    {
        private readonly string _text;

        public LinkDisplay(string text)
        {
            _text = text;
            BackColor = Color.White;
            ForeColor = TextColor;
            Cursor = Cursors.IBeam;
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var borderPen = new Pen(BorderColor);
            e.Graphics.Clear(BackColor);
            e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

            var textBounds = new Rectangle(7, 1, Math.Max(0, Width - 14), Height - 2);
            TextRenderer.DrawText(
                e.Graphics,
                _text,
                Font,
                textBounds,
                ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        }
    }

    private enum IconButtonKind
    {
        Open,
        Copy,
        Close,
    }

    private sealed class IconButton : Button
    {
        private readonly IconButtonKind _kind;
        private readonly Color _iconColor;
        private readonly Color _borderColor;
        private readonly Color _normalBackColor;
        private Color _hoverBackColor;
        private Color _pressedBackColor;
        private bool _hovered;
        private bool _pressed;

        public IconButton(IconButtonKind kind, Color backColor, Color iconColor, Color borderColor)
        {
            _kind = kind;
            _normalBackColor = backColor;
            _hoverBackColor = backColor == Color.White ? Color.FromArgb(232, 238, 245) : backColor;
            _pressedBackColor = backColor == Color.White ? Color.FromArgb(218, 227, 236) : backColor;
            _iconColor = iconColor;
            _borderColor = borderColor;
            BackColor = backColor;
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            TabStop = true;
            Text = string.Empty;
            UseVisualStyleBackColor = false;
            DoubleBuffered = true;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = _hoverBackColor;
            FlatAppearance.MouseDownBackColor = _pressedBackColor;
        }

        public Color HoverBackColor
        {
            set
            {
                _hoverBackColor = value;
                FlatAppearance.MouseOverBackColor = value;
            }
        }

        public Color PressedBackColor
        {
            set
            {
                _pressedBackColor = value;
                FlatAppearance.MouseDownBackColor = value;
            }
        }

        protected override void OnPaint(PaintEventArgs pevent)
        {
            pevent.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var backBrush = new SolidBrush(CurrentBackColor());
            pevent.Graphics.FillRectangle(backBrush, ClientRectangle);

            using var borderPen = new Pen(_borderColor);
            pevent.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);

            var bounds = CenteredIconBounds();
            DrawIcon(pevent.Graphics, bounds);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseDown(MouseEventArgs mevent)
        {
            if (mevent.Button == MouseButtons.Left)
            {
                _pressed = true;
                Invalidate();
            }

            base.OnMouseDown(mevent);
        }

        protected override void OnMouseUp(MouseEventArgs mevent)
        {
            if (_pressed)
            {
                _pressed = false;
                Invalidate();
            }

            base.OnMouseUp(mevent);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            _pressed = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        private Color CurrentBackColor()
        {
            if (!Enabled)
            {
                return Color.FromArgb(232, 236, 240);
            }

            if (_pressed)
            {
                return _pressedBackColor;
            }

            return _hovered ? _hoverBackColor : _normalBackColor;
        }

        private Rectangle CenteredIconBounds()
        {
            const int size = 18;
            return new Rectangle((Width - size) / 2, (Height - size) / 2, size, size);
        }

        private void DrawIcon(Graphics graphics, Rectangle bounds)
        {
            using var pen = new Pen(_iconColor, 2F)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            switch (_kind)
            {
                case IconButtonKind.Open:
                    DrawOpenIcon(graphics, pen, bounds);
                    break;
                case IconButtonKind.Copy:
                    DrawCopyIcon(graphics, pen, bounds);
                    break;
                case IconButtonKind.Close:
                    DrawCloseIcon(graphics, pen, bounds);
                    break;
            }
        }

        private static void DrawOpenIcon(Graphics graphics, Pen pen, Rectangle bounds)
        {
            graphics.DrawRectangle(pen, bounds.X + 2, bounds.Y + 6, bounds.Width - 8, bounds.Height - 8);
            graphics.DrawLine(pen, bounds.X + 8, bounds.Y + 3, bounds.Right - 2, bounds.Y + 3);
            graphics.DrawLine(pen, bounds.Right - 2, bounds.Y + 3, bounds.Right - 2, bounds.Y + 9);
            graphics.DrawLine(pen, bounds.X + 8, bounds.Y + 10, bounds.Right - 2, bounds.Y + 3);
        }

        private static void DrawCopyIcon(Graphics graphics, Pen pen, Rectangle bounds)
        {
            graphics.DrawRectangle(pen, bounds.X + 6, bounds.Y + 3, bounds.Width - 7, bounds.Height - 7);
            graphics.DrawRectangle(pen, bounds.X + 3, bounds.Y + 6, bounds.Width - 7, bounds.Height - 7);
        }

        private static void DrawCloseIcon(Graphics graphics, Pen pen, Rectangle bounds)
        {
            graphics.DrawLine(pen, bounds.X + 4, bounds.Y + 4, bounds.Right - 4, bounds.Bottom - 4);
            graphics.DrawLine(pen, bounds.Right - 4, bounds.Y + 4, bounds.X + 4, bounds.Bottom - 4);
        }
    }
}
