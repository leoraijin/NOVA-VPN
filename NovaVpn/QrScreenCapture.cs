using System;
using System.Drawing;
using Forms = System.Windows.Forms;

namespace NovaVpn;

public static class QrScreenCapture
{
    public static Bitmap SelectRegion()
    {
        var bounds = Forms.SystemInformation.VirtualScreen;
        if ((long)bounds.Width * bounds.Height > 100 * 1024 * 1024) throw new InvalidOperationException("Слишком большая область рабочего стола для захвата.");
        using (var screenshot = new Bitmap(bounds.Width, bounds.Height))
        {
            using (var graphics = Graphics.FromImage(screenshot)) graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            using (var picker = new RegionPicker(screenshot, bounds))
                return picker.ShowDialog() == Forms.DialogResult.OK ? picker.Selection : null;
        }
    }

    public static Rectangle ImageRectangle(Rectangle selection, Size client, Size image)
    {
        if (client.Width <= 0 || client.Height <= 0) return Rectangle.Empty;
        int x = (int)Math.Floor((double)selection.Left * image.Width / client.Width);
        int y = (int)Math.Floor((double)selection.Top * image.Height / client.Height);
        int right = (int)Math.Ceiling((double)selection.Right * image.Width / client.Width);
        int bottom = (int)Math.Ceiling((double)selection.Bottom * image.Height / client.Height);
        return Rectangle.Intersect(new Rectangle(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y)), new Rectangle(Point.Empty, image));
    }

    private sealed class RegionPicker : Forms.Form
    {
        private readonly Bitmap screenshot;
        private Point start;
        private Rectangle rectangle;
        private bool selecting;
        public Bitmap Selection { get; private set; }
        public RegionPicker(Bitmap image, Rectangle screen)
        {
            screenshot = image; AutoScaleMode = Forms.AutoScaleMode.None; StartPosition = Forms.FormStartPosition.Manual;
            FormBorderStyle = Forms.FormBorderStyle.None; Bounds = screen; ShowInTaskbar = false; TopMost = true; KeyPreview = true;
            Cursor = Forms.Cursors.Cross; DoubleBuffered = true; BackgroundImage = image; BackgroundImageLayout = Forms.ImageLayout.Stretch;
            KeyDown += (s, e) => { if (e.KeyCode == Forms.Keys.Escape) { DialogResult = Forms.DialogResult.Cancel; Close(); } };
            MouseDown += (s, e) => { if (e.Button != Forms.MouseButtons.Left) return; selecting = true; start = e.Location; rectangle = Rectangle.Empty; };
            MouseMove += (s, e) => { if (!selecting) return; rectangle = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X), Math.Max(start.Y, e.Y)); Invalidate(); };
            MouseUp += (s, e) =>
            {
                if (!selecting || e.Button != Forms.MouseButtons.Left) return;
                selecting = false;
                var crop = ImageRectangle(rectangle, ClientSize, screenshot.Size);
                if (crop.Width < 8 || crop.Height < 8) return;
                if ((long)crop.Width * crop.Height > QrImportService.MaxPixels) { Forms.MessageBox.Show(this, "Выделите QR-код меньшей областью."); return; }
                Selection = screenshot.Clone(crop, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                DialogResult = Forms.DialogResult.OK; Close();
            };
        }
        protected override void OnPaint(Forms.PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(Color.FromArgb(32, 196, 220), 3)) if (!rectangle.IsEmpty) e.Graphics.DrawRectangle(pen, rectangle);
            const string message = "Выделите QR-код мышью · Esc — отмена";
            var text = Forms.TextRenderer.MeasureText(message, Font);
            e.Graphics.FillRectangle(Brushes.Black, new Rectangle(12, 12, text.Width + 20, text.Height + 16));
            Forms.TextRenderer.DrawText(e.Graphics, message, Font, new Point(22, 20), Color.White);
        }
    }
}
