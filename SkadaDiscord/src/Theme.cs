// Тёмная тема в стиле Discord и мелкие помощники для WinForms.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace SkadaDiscord
{
    public static class Theme
    {
        public static readonly Color Back = Color.FromArgb(30, 31, 34);
        public static readonly Color Panel = Color.FromArgb(43, 45, 49);
        public static readonly Color Input = Color.FromArgb(56, 58, 64);
        public static readonly Color Line = Color.FromArgb(63, 65, 71);
        public static readonly Color Text = Color.FromArgb(219, 222, 225);
        public static readonly Color Muted = Color.FromArgb(148, 155, 164);
        public static readonly Color Accent = Color.FromArgb(88, 101, 242);
        public static readonly Color Green = Color.FromArgb(35, 165, 90);
        public static readonly Color Red = Color.FromArgb(242, 63, 67);
        public static readonly Color Yellow = Color.FromArgb(240, 178, 50);

        public static readonly Font Font = new Font("Segoe UI", 9.75f);
        public static readonly Font Small = new Font("Segoe UI", 8.75f);
        public static readonly Font Bold = new Font("Segoe UI Semibold", 10f);
        public static readonly Font H1 = new Font("Segoe UI Semibold", 16f);
        public static readonly Font H2 = new Font("Segoe UI Semibold", 12.5f);

        public static Label Label(string text, Font font, Color color)
        {
            return new Label { Text = text, Font = font ?? Font, ForeColor = color, AutoSize = true, BackColor = Color.Transparent };
        }

        public static Button Button(string text, bool primary)
        {
            var b = new Button {
                Text = text, FlatStyle = FlatStyle.Flat, Font = Font, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(10, 3, 10, 3), Cursor = Cursors.Hand, UseVisualStyleBackColor = false,
                BackColor = primary ? Accent : Input, ForeColor = Color.White, Margin = new Padding(0, 0, 6, 0),
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(71, 82, 196) : Line;
            b.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(60, 69, 165) : Panel;
            return b;
        }

        public static TextBox TextBox(string text)
        {
            return new TextBox { Text = text ?? "", BackColor = Input, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = Font };
        }

        // выпадающий список, нарисованный самостоятельно (иначе Windows делает его белым)
        public static ComboBox Combo()
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Input, ForeColor = Text, FlatStyle = FlatStyle.Flat, Font = Font, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 22 };
            c.DrawItem += (s, e) =>
            {
                var box = (ComboBox)s;
                bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var bg = new SolidBrush(selected ? Accent : Input))
                    e.Graphics.FillRectangle(bg, e.Bounds);
                if (e.Index < 0) return;
                var text = box.GetItemText(box.Items[e.Index]);
                TextRenderer.DrawText(e.Graphics, text, box.Font, e.Bounds, box.Enabled ? Text : Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.LeftAndRightPadding);
            };
            return c;
        }

        public static CheckBox Check(string text, bool value)
        {
            return new CheckBox { Text = text, Checked = value, ForeColor = Text, AutoSize = true, Font = Font, BackColor = Color.Transparent };
        }

        public static NumericUpDown Number(decimal min, decimal max, decimal value)
        {
            return new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, value)), BackColor = Input, ForeColor = Text, BorderStyle = BorderStyle.FixedSingle, Font = Font, Width = 90 };
        }

        public static FlowLayoutPanel Row()
        {
            return new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, BackColor = Color.Transparent, Margin = new Padding(0, 0, 0, 8) };
        }

        public static void Grid(DataGridView g)
        {
            g.BackgroundColor = Panel;
            g.BorderStyle = BorderStyle.None;
            g.GridColor = Line;
            g.EnableHeadersVisualStyles = false;
            g.RowHeadersVisible = false;
            g.AllowUserToAddRows = false;
            g.AllowUserToResizeRows = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            g.ColumnHeadersHeight = 32;
            g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            g.RowTemplate.Height = 30;
            g.ColumnHeadersDefaultCellStyle.BackColor = Back;
            g.ColumnHeadersDefaultCellStyle.ForeColor = Muted;
            g.ColumnHeadersDefaultCellStyle.Font = Small;
            g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Back;
            g.DefaultCellStyle.BackColor = Panel;
            g.DefaultCellStyle.ForeColor = Text;
            g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(64, 68, 92);
            g.DefaultCellStyle.SelectionForeColor = Color.White;
            g.DefaultCellStyle.Font = Font;
            g.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            g.EditMode = DataGridViewEditMode.EditOnEnter;
        }

        public static void ListBox(ListBox l)
        {
            l.BackColor = Panel;
            l.ForeColor = Text;
            l.BorderStyle = BorderStyle.None;
            l.Font = Font;
        }
    }

    // простое окно ввода
    public static class Ask
    {
        public static string Text(IWin32Window owner, string title, string prompt, string value)
        {
            using (var f = new Form())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.StartPosition = FormStartPosition.CenterParent;
                f.MinimizeBox = f.MaximizeBox = false;
                f.BackColor = Theme.Back;
                f.ForeColor = Theme.Text;
                f.Font = Theme.Font;
                f.AutoScaleMode = AutoScaleMode.Dpi;
                f.ClientSize = new Size(420, 130);
                var l = Theme.Label(prompt, null, Theme.Text);
                l.Location = new Point(14, 14);
                var t = Theme.TextBox(value);
                t.Location = new Point(16, 42);
                t.Width = 388;
                var ok = Theme.Button("OK", true);
                ok.DialogResult = DialogResult.OK;
                ok.Location = new Point(250, 84);
                var cancel = Theme.Button("Отмена", false);
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(320, 84);
                f.Controls.AddRange(new Control[] { l, t, ok, cancel });
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                return f.ShowDialog(owner) == DialogResult.OK ? t.Text.Trim() : null;
            }
        }
    }
}
