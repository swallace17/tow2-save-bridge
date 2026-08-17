using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

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

        InstallMinSize();

        // Size once the content is live, so XamlRoot.RasterizationScale reflects the
        // monitor the window actually landed on.
        if (Content is FrameworkElement root)
        {
            void OnReady(object s, RoutedEventArgs e)
            {
                root.Loaded -= OnReady;
                SizeToContent();
            }
            root.Loaded += OnReady;
        }

        SaveList.ItemsSource = _saves;
        SkillItems.ItemsSource = _skills;

        for (int i = 0; i < SaveIo.SkillNames.Length; i++)
            _skills.Add(new SkillVM { Name = SaveIo.SkillNames[i], Offset = 236 + 4 * i });

        LoadSaves();
    }

    // The window is sized in LOGICAL units and scaled by the monitor DPI, because
    // AppWindow.Resize takes physical pixels while layout is measured logically.
    //
    // Width is the sum of the actual layout constants, so it fits the content with
    // no dead space:  264 saves pane + 18 gap + 32 padding + (2 x 236 + 24) grid
    //                 + ~18 scrollbar allowance
    private const int LogicalW = 832;
    private const int LogicalH = 650;   // + name row + bits row

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

    private void InstallMinSize()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _subclass = MinSizeProc;
        SetWindowSubclass(hwnd, _subclass, 1, IntPtr.Zero);
    }

    /// <summary>
    /// Sized from the XamlRoot's rasterization scale rather than GetDpiForWindow at
    /// construction: on a mixed-DPI desktop the window isn't on its final monitor yet
    /// when the constructor runs, so the same logical size came out at different
    /// physical sizes between launches.
    /// </summary>
    private void SizeToContent()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = (Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? DpiScale(hwnd);
        if (scale <= 0) scale = 1.0;

        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
            AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);

        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            Math.Min((int)(LogicalW * scale), (int)(area.WorkArea.Width  * 0.95)),
            Math.Min((int)(LogicalH * scale), (int)(area.WorkArea.Height * 0.92))));
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

    private void ClearStatus() => Status.IsOpen = false;

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
            NameBox.Text = SaveIo.ReadName(slot);
            CharBox.Text = SaveIo.ReadCharacterName(slot);

            // Bits has no fixed offset; if the landmark isn't found, disable rather than guess.
            if (data.Bits.HasValue)
            {
                BitsBox.Value = data.Bits.Value;
                BitsBox.IsEnabled = true;
                BitsOffset.Text = "located";
            }
            else
            {
                BitsBox.Value = double.NaN;
                BitsBox.IsEnabled = false;
                BitsOffset.Text = "not found";
            }

            bool autosave = !(slot.Length == 32 && slot.All(Uri.IsHexDigit));
            CloneFirst.IsEnabled = !autosave;
            if (autosave) CloneFirst.IsChecked = false;

            RecordInfo.Text = $"{data.Magic} @{data.Record} · {data.PayloadLen} B";

            if (autosave)
                Say("Autosave — it cannot be copied, so edits apply to it directly.",
                    InfoBarSeverity.Warning);
            else
                ClearStatus();
        }
        catch (Exception ex)
        {
            RecordInfo.Text = "";
            Say(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => LoadSaves();

    // ------------------------------------------------------ Xbox -> Steam recovery

    private sealed class XboxRow
    {
        public SaveIo.XboxSave Save { get; init; } = null!;
        public bool Selected { get; set; } = true;
        public string Title => Save.Character;
        public string Detail => Save.Display;
    }

    private async void Recover_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveIo.XboxStoreExists())
        {
            Say("No Xbox container store found — nothing to recover. This is normal if you have "
              + "never played signed into an Xbox account.", InfoBarSeverity.Informational);
            return;
        }

        List<SaveIo.XboxSave> found;
        try { found = SaveIo.ListXboxSaves(); }
        catch (Exception ex) { Say(ex.Message, InfoBarSeverity.Error); return; }

        if (found.Count == 0)
        {
            Say("The Xbox store exists but holds no readable saves.", InfoBarSeverity.Warning);
            return;
        }

        var rows = found.Select(s => new XboxRow { Save = s }).ToList();

        var list = new ListView
        {
            ItemsSource = rows,
            SelectionMode = ListViewSelectionMode.Multiple,
            MaxHeight = 260,
            ItemTemplate = (DataTemplate)XamlReader.Load(
                """
                <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                  <StackPanel Padding="0,4" Spacing="1">
                    <TextBlock Text="{Binding Title}" FontWeight="SemiBold" FontSize="13" />
                    <TextBlock Text="{Binding Detail}" FontSize="11" Opacity="0.6" />
                  </StackPanel>
                </DataTemplate>
                """)
        };
        foreach (var r in rows) list.SelectedItems.Add(r);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Signing into an Xbox account makes the game write saves to Xbox connected "
                 + "storage, leaving the Steam folder with only screenshots. These are converted "
                 + "into the Steam format so Steam Cloud, Deck and GeForce Now can see them.\n\n"
                 + "The Xbox store is only read from, never modified."
        });
        panel.Children.Add(list);

        var dlg = new ContentDialog
        {
            Title = "Recover saves from Xbox",
            Content = panel,
            PrimaryButtonText = "Recover selected",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot
        };

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var chosen = list.SelectedItems.Cast<XboxRow>().Select(r => r.Save).ToList();
        if (chosen.Count == 0) { Say("Nothing selected.", InfoBarSeverity.Informational); return; }

        if (SaveIo.GameIsRunning())
        {
            Say("The Outer Worlds 2 is running. Close it before recovering saves.", InfoBarSeverity.Error);
            return;
        }

        try
        {
            string backup = SaveIo.BackupBeforeRecovery();
            int ok = 0;
            var failures = new List<string>();
            foreach (var s in chosen)
            {
                try { SaveIo.RecoverSave(s); ok++; }
                catch (Exception ex) { failures.Add($"{s.Slot}: {ex.Message}"); }
            }

            LoadSaves();

            if (failures.Count == 0)
                Say($"Recovered {ok} save{(ok == 1 ? "" : "s")}. Exit Steam fully, launch the game, "
                  + $"and decline the Xbox sign-in.  Backup: {backup}", InfoBarSeverity.Success);
            else
                Say($"Recovered {ok}, failed {failures.Count}. {string.Join("  |  ", failures)}",
                    InfoBarSeverity.Warning);
        }
        catch (Exception ex)
        {
            Say(ex.Message, InfoBarSeverity.Error);
        }
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

        string wantedName = (NameBox.Text ?? "").Trim();
        string wantedChar = (CharBox.Text ?? "").Trim();
        if (wantedName.Length == 0 || wantedChar.Length == 0)
        {
            Say("Save name and character name cannot be empty.", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            string target = slot;
            if (CloneFirst.IsChecked == true)
                target = SaveIo.Clone(slot);

            int? bits = (BitsBox.IsEnabled && !double.IsNaN(BitsBox.Value))
                        ? (int)Math.Round(BitsBox.Value) : null;
            string backup = SaveIo.Write(target, values, points, bits);

            // character first: it changes the payload length and rewrites the metadata
            // size field, which the save-name rebuild then has to preserve.
            if (wantedChar != SaveIo.ReadCharacterName(target))
                SaveIo.RenameCharacter(target, wantedChar);

            if (wantedName != SaveIo.ReadName(target))
                SaveIo.Rename(target, wantedName);

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




