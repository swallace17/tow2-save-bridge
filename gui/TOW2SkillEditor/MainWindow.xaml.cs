using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace TOW2SkillEditor;

public sealed class SkillVM : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public int Offset { get; init; }
    public string OffsetLabel => $"+{Offset}";

    private double _value;
    public double Value
    {
        get => _value;
        set { if (Math.Abs(_value - value) > 0.0001) { _value = value; Raise(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed class SaveRow
{
    public string Slot { get; init; } = "";
    public string Character { get; init; } = "";
    public string Subtitle { get; init; } = "";
}

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<SaveRow> _saves = new();
    private readonly ObservableCollection<SkillVM> _skills = new();

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        SizeToContent();

        SaveList.ItemsSource = _saves;
        SkillItems.ItemsSource = _skills;

        for (int i = 0; i < SaveIo.SkillNames.Length; i++)
            _skills.Add(new SkillVM { Name = SaveIo.SkillNames[i], Offset = 236 + 4 * i });

        LoadSaves();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>
    /// AppWindow.Resize takes PHYSICAL pixels, but layout sizes are logical. On a
    /// 150% display a "1150px" window is only ~767 logical units wide, which isn't
    /// enough for the two-column skill grid. Size in logical units and scale by DPI.
    /// </summary>
    private void SizeToContent()
    {
        const int logicalW = 980;   // saves pane + two skill columns, with margin
        const int logicalH = 660;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        uint dpi = GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;

        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);

        int w = Math.Min((int)(logicalW * scale), (int)(area.WorkArea.Width * 0.95));
        int h = Math.Min((int)(logicalH * scale), (int)(area.WorkArea.Height * 0.92));

        AppWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
    }

    private void Say(string message, InfoBarSeverity severity)
    {
        Status.Message = message;
        Status.Severity = severity;
        Status.IsOpen = true;
    }

    private void LoadSaves()
    {
        _saves.Clear();
        try
        {
            foreach (var s in SaveIo.ListSaves())
                _saves.Add(new SaveRow
                {
                    Slot = s.Slot,
                    Character = s.Character,
                    Subtitle = $"{s.Saved:MMM d  HH:mm:ss}   ·   {s.Bytes / 1024.0 / 1024.0:0.00} MB"
                });
        }
        catch (Exception ex) { Say(ex.Message, InfoBarSeverity.Error); return; }

        if (_saves.Count == 0)
        {
            Say($"No saves found in {SaveIo.Root}", InfoBarSeverity.Warning);
            return;
        }

        // prefer a manual save -- autosaves can't be cloned, so they're a poor default
        var manual = _saves.FirstOrDefault(r => r.Slot.Length == 32 && r.Slot.All(Uri.IsHexDigit));
        SaveList.SelectedItem = manual ?? _saves[0];
    }

    private string? SelectedSlot => (SaveList.SelectedItem as SaveRow)?.Slot;

    private void SaveList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSkills();

    private void LoadSkills()
    {
        var slot = SelectedSlot;
        if (slot is null) return;

        try
        {
            var data = SaveIo.Read(slot);
            for (int i = 0; i < _skills.Count; i++) _skills[i].Value = data.Values[i];
            PointsBox.Value = data.Points;

            bool autosave = !(slot.Length == 32 && slot.All(Uri.IsHexDigit));
            CloneFirst.IsEnabled = !autosave;
            if (autosave) CloneFirst.IsChecked = false;

            Say(autosave
                    ? $"{data.Magic} @{data.Record} · {data.PayloadLen} B — autosave, edits apply in place"
                    : $"{data.Magic} @{data.Record} · {data.PayloadLen} B",
                InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            Say(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        LoadSaves();
        Say("Reloaded from disk.", InfoBarSeverity.Informational);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var slot = SelectedSlot;
        if (slot is null) { Say("Pick a save first.", InfoBarSeverity.Warning); return; }

        if (SaveIo.GameIsRunning())
        {
            Say("The Outer Worlds 2 is running. Close it before applying edits.", InfoBarSeverity.Error);
            return;
        }

        var values = _skills.Select(s => (int)Math.Round(s.Value)).ToArray();
        int points = (int)Math.Round(double.IsNaN(PointsBox.Value) ? 0 : PointsBox.Value);

        try
        {
            string target = slot;
            if (CloneFirst.IsChecked == true)
                target = SaveIo.Clone(slot, "EDITED COPY");

            string backup = SaveIo.Write(target, values, points);

            LoadSaves();
            var row = _saves.FirstOrDefault(r => r.Slot == target);
            if (row is not null) SaveList.SelectedItem = row;

            Say(target == slot
                    ? $"Saved and verified. Backup: {backup}"
                    : $"Saved to a copy and verified. Original untouched.  Backup: {backup}",
                InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Say(ex.Message, InfoBarSeverity.Error);
            var dlg = new ContentDialog
            {
                Title = "Could not write the save",
                Content = ex.Message,
                CloseButtonText = "OK",
                XamlRoot = Content.XamlRoot
            };
            await dlg.ShowAsync();
        }
    }
}
