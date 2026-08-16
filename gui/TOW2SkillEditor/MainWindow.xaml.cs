using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    // The window is sized in LOGICAL units and scaled by the monitor DPI, because
    // AppWindow.Resize takes physical pixels while layout is measured logically.
    // These dimensions are what the two-column skill grid needs; they are used for
    // both the startup size and the minimum drag size.
    private const int LogicalW = 980;
    private const int LogicalH = 660;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("Comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, uint id, IntPtr refData);

    [DllImport("Comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                         uint id, IntPtr refData);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    private const uint WM_GETMINMAXINFO = 0x0024;
    private SubclassProc? _subclass;   // field, so the delegate isn't collected

    private double DpiScale(IntPtr hwnd)
    {
        uint dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    private void SizeToContent()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = DpiScale(hwnd);

        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)(LogicalW * scale), (int)(area.WorkArea.Width  * 0.95)),
            Math.Min((int)(LogicalH * scale), (int)(area.WorkArea.Height * 0.92))));

        // Clamp how small the user can drag it. Recomputed per message so the limit
        // stays correct if the window moves to a monitor with different scaling.
        _subclass = MinSizeProc;
        SetWindowSubclass(hwnd, _subclass, 1, IntPtr.Zero);
    }

    private IntPtr MinSizeProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                               uint id, IntPtr refData)
    {
        if (msg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            double scale = DpiScale(hWnd);
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMinTrackSize.X = (int)(LogicalW * scale);
            mmi.ptMinTrackSize.Y = (int)(LogicalH * scale);
            Marshal.StructureToPtr(mmi, lParam, false);
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
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
