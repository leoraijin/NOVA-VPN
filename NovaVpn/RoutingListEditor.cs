using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace NovaVpn;

// Each rule has its own input; the persisted routing lists retain their existing format.
public sealed class RoutingListEditor : StackPanel
{
    private readonly List<string> values;
    private readonly Action save;
    private readonly Func<TextBox> createInput;
    private readonly Func<string, Button> createButton;
    private readonly string itemLabel;
    private readonly bool readOnly;
    private readonly StackPanel rows = new StackPanel();
    private readonly List<TextBox> inputs = new List<TextBox>();
    private bool updating;

    public RoutingListEditor(List<string> values, Action save, Func<TextBox> createInput,
        Func<string, Button> createButton, string itemLabel, bool readOnly)
    {
        this.values = values;
        this.save = save;
        this.createInput = createInput;
        this.createButton = createButton;
        this.itemLabel = itemLabel;
        this.readOnly = readOnly;

        var scroll = new ScrollViewer
        {
            Content = rows, MaxHeight = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        scroll.PreviewMouseWheel += (s, e) => SmoothScrollBehavior.ForwardToPage(scroll, e);
        Children.Add(scroll);
        foreach (string value in values) InsertRow(inputs.Count, value);
        if (inputs.Count == 0) InsertRow(0, "");

        var add = createButton("+ Добавить " + itemLabel);
        add.Name = "AddRoutingEntry";
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.Margin = new Thickness(0, 4, 0, 0);
        add.IsEnabled = !readOnly;
        AutomationProperties.SetName(add, "Добавить " + itemLabel);
        add.Click += delegate
        {
            if (readOnly) return;
            // Reuse an empty input rather than accumulating empty rows.
            TextBox empty = inputs.FirstOrDefault(input => string.IsNullOrWhiteSpace(input.Text));
            FocusInput(empty ?? InsertRow(inputs.Count, ""));
        };
        Children.Add(add);
    }

    internal void AppendValue(string value)
    {
        if (readOnly || string.IsNullOrWhiteSpace(value)) return;
        var empty = inputs.FirstOrDefault(input => string.IsNullOrWhiteSpace(input.Text));
        if (empty != null) empty.Text = value;
        else { InsertRow(inputs.Count, value); Commit(); }
    }

    private TextBox InsertRow(int index, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        TextBox input = createInput();
        input.Name = "RoutingEntry";
        input.AcceptsReturn = false;
        input.AcceptsTab = false;
        input.TextWrapping = TextWrapping.NoWrap;
        input.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        input.HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        input.IsReadOnly = readOnly;
        input.Text = value ?? "";
        AutomationProperties.SetName(input, itemLabel + " маршрутизации");
        row.Children.Add(input);

        var remove = createButton("×");
        remove.Name = "RemoveRoutingEntry";
        remove.Width = 32;
        remove.MinWidth = 32;
        remove.Padding = new Thickness(0);
        remove.Margin = new Thickness(6, 0, 0, 0);
        remove.VerticalAlignment = VerticalAlignment.Center;
        remove.IsEnabled = !readOnly;
        remove.ToolTip = "Удалить " + itemLabel;
        AutomationProperties.SetName(remove, "Удалить " + itemLabel);
        Grid.SetColumn(remove, 1);
        row.Children.Add(remove);

        rows.Children.Insert(index, row);
        inputs.Insert(index, input);
        input.TextChanged += delegate { Commit(); };
        remove.Click += delegate
        {
            if (readOnly) return;
            int removed = inputs.IndexOf(input);
            rows.Children.Remove(row);
            inputs.RemoveAt(removed);
            if (inputs.Count == 0) InsertRow(0, "");
            Commit();
            FocusInput(inputs[Math.Min(removed, inputs.Count - 1)]);
        };
        DataObject.AddPastingHandler(input, (s, e) =>
        {
            if (readOnly) { e.CancelCommand(); return; }
            if (!e.DataObject.GetDataPresent(DataFormats.UnicodeText)) return;
            string text = e.DataObject.GetData(DataFormats.UnicodeText) as string;
            if (text == null || (text.IndexOf('\n') < 0 && text.IndexOf('\r') < 0)) return;
            e.CancelCommand();
            PasteRows(input, text);
        });
        return input;
    }

    private void PasteRows(TextBox input, string text)
    {
        // Respect the selection and preserve text before and after it, as normal paste does.
        string replacement = input.Text.Remove(input.SelectionStart, input.SelectionLength)
            .Insert(input.SelectionStart, text);
        string[] parts = replacement.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Select(part => part.Trim()).Where(part => part.Length > 0).ToArray();
        int index = inputs.IndexOf(input);
        TextBox last = input;
        updating = true;
        try
        {
            input.Text = parts.Length == 0 ? "" : parts[0];
            for (int i = 1; i < parts.Length; i++) last = InsertRow(index + i, parts[i]);
        }
        finally { updating = false; }
        Commit();
        FocusInput(last);
        last.CaretIndex = last.Text.Length;
    }

    private void Commit()
    {
        if (updating || readOnly) return;
        var next = inputs.Select(input => input.Text.Trim()).Where(value => value.Length > 0).ToList();
        if (values.SequenceEqual(next)) return;
        values.Clear();
        values.AddRange(next);
        save();
    }

    private static void FocusInput(TextBox input)
    {
        input.BringIntoView();
        input.Focus();
    }
}
