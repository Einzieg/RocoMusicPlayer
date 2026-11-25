using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RocoPlayer.Audio;
using RocoPlayer.Music;

namespace RocoPlayer;

public partial class MainWindow
{
    private sealed record LibraryRow(LibraryItem Item)
    {
        public string Title => Item.Title;
        public string SourceLabel { get; } = SourceKind(Item);
        public string DurationLabel => Clock(Item.Duration);
        public DateTimeOffset? Added { get; } = DateTimeOffset.TryParse(Item.CreatedAt, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var date) ? date : null;
    }

    private static readonly StringComparer TitleComparer = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
    private LibraryRow[] rows = [];
    private string[] visibleIds = [];
    private LibraryItem? detail;
    private Task? listening;
    private bool listeningToSource, pendingAuditionSeek;
    private string? audioFile;
    private double cursor;
    private bool updatingSeek;

    private static string SourceKind(LibraryItem item)
    {
        if (item.Extra?.ContainsKey("provenance") == true) return "MIDI · 在线下载";
        return Path.GetExtension(item.Source)?.ToLowerInvariant() switch
        {
            ".mid" or ".midi" => "MIDI · 本地导入",
            ".mp3" or ".wav" or ".ogg" or ".flac" => "音频转谱",
            ".txt" => "简谱 · 本地导入",
            _ => string.IsNullOrWhiteSpace(item.Source) ? "本地曲谱" : Path.GetFileName(item.Source)
        };
    }
    private static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalMinutes:00}:{time.Seconds:00}";
    }
    private void RefreshLibrary(string? removedId = null)
    {
        rows = library.Items.Select(i => new LibraryRow(i)).Reverse().ToArray();
        ApplySongView(removedId);
        ShowDetail();
    }
    private void ApplySongView(string? removedId = null)
    {
        var query = Filter.Text.Trim();
        refreshing = true;
        try
        {
            if (removedId != null && Songs.ItemsSource is ObservableCollection<LibraryRow> visible)
            {
                // Keep the existing list and its viewport when one row disappears.
                int index = Array.IndexOf(visibleIds, removedId);
                if (index >= 0) visible.RemoveAt(index);
            }
            else
            {
                var newest = rows.Where(r => r.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    (r.Item.Source?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                    r.SourceLabel.Contains(query, StringComparison.CurrentCultureIgnoreCase)).OrderByDescending(r => r.Added);
                visible = new(SortOrder.SelectedIndex == 1 ? newest.OrderBy(r => r.Title, TitleComparer) : newest);
                Songs.ItemsSource = visible;
            }
            visibleIds = visible.Select(r => r.Item.Id).ToArray();
            Songs.SelectedItem = visible.FirstOrDefault(r => r.Item.Id == library.CurrentId);
            if (removedId == null && Songs.SelectedItem != null) Songs.ScrollIntoView(Songs.SelectedItem);
        }
        finally { refreshing = false; }
        SongCount.Text = query.Length == 0 ? $"{rows.Length} 首曲谱" : $"{visibleIds.Length} / {rows.Length} 首曲谱";
        EmptyResults.Text = rows.Length == 0 ? "导入 MIDI、TXT 文件，或添加一首简谱。" : "没有找到匹配的曲谱";
        EmptyResults.Visibility = visibleIds.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void ShowDetail()
    {
        var selected = rows.FirstOrDefault(r => r.Item.Id == library.CurrentId);
        if (detail?.Id != selected?.Item.Id)
        {
            SetCursor(0);
            DetailScroll.ScrollToTop();
        }
        detail = selected?.Item;
        if (detail != null && !(detail.Mapping ?? []).SequenceEqual(library.Settings.Mapping) && !Busy)
        {
            var id = detail.Id;
            _ = GuardAsync(() => JobAsync("正在按新键位重新编配…", async token =>
            {
                await Task.Run(() => library.Arranged(id, token), token);
                token.ThrowIfCancellationRequested();
                RefreshLibrary();
                Status.Text = "已按当前键位重新编配";
            }));
        }
        BarTitle.Text = SongTitle.Text = detail?.Title ?? (rows.Length == 0 ? "等待第一首曲谱" : "请选择曲目");
        SongMeta.Text = selected == null ? "" : $"{selected.DurationLabel}  ·  {selected.Item.Original.Notes.Count:N0} 个音符  ·  {selected.SourceLabel}";
        SongMeta.ToolTip = detail?.Source;
        OriginalCount.Text = detail?.Original.Notes.Count.ToString("N0") ?? "—";
        FittedCount.Text = detail?.Score.Notes.Count.ToString("N0") ?? "—";
        FittedCount.ToolTip = detail?.Summary;
        TargetKeys.Text = detail == null ? "—" : $"{library.Settings.Mapping.Count} 键";
        TargetKeys.ToolTip = string.Join("  ", library.Settings.Mapping.OrderBy(b => b.Pitch).Select(b => b.Key));
        SourceListen.Visibility = Path.GetExtension(detail?.Source)?.ToLowerInvariant() is ".mp3" or ".wav" or ".ogg" or ".flac"
            ? Visibility.Visible : Visibility.Collapsed;
        ShowVersion();
        UpdateTransport();
    }
    private void ShowVersion()
    {
        bool original = OriginalVersion.IsChecked == true;
        var score = original ? detail?.Original : detail?.Score;
        NoteRoll.SetScore(score, original ? null : library.Settings.Mapping);
        RollMiddle.Text = Clock((score?.Duration ?? 0) / 2);
        RollEnd.Text = Clock(score?.Duration ?? 0);
        SetTrackDuration(score?.Duration ?? 0);
        SetCursor(Math.Min(cursor, Seek.Maximum));
    }
    private void SetTrackDuration(double seconds)
    {
        TrackDuration.Text = Clock(seconds);
        updatingSeek = true;
        try { Seek.Maximum = seconds; }
        finally { updatingSeek = false; }
    }
    private void SetPosition(double seconds)
    {
        updatingSeek = true;
        try { Seek.Value = seconds; Elapsed.Text = Clock(Seek.Value); NoteRoll.SetPosition(Seek.Value); }
        finally { updatingSeek = false; }
    }
    private void SeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || updatingSeek || Busy) return;
        SetCursor(e.NewValue);
        if (listening == null) return;
        pendingAuditionSeek = Seek.IsMouseCaptureWithin;
        if (!pendingAuditionSeek) RestartAudition();
    }
    private void SeekReleased(object sender, MouseEventArgs e)
    {
        if (!pendingAuditionSeek || Seek.IsMouseCaptureWithin) return;
        pendingAuditionSeek = false;
        RestartAudition();
    }
    private void RestartAudition()
    {
        if (listening == null) return;
        if (cursor >= Seek.Maximum) { StopAudition(); return; }
        _ = GuardAsync(() => ListenAsync(listeningToSource));
    }
    private void SetCursor(double value)
    {
        cursor = value;
        SetPosition(value);
    }
    private void FilterChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready) return;
        navigator.Reset();
        ApplySongView();
    }
    private void SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready) return;
        navigator.Reset();
        ApplySongView();
    }
    private void ClearSearchClick(object sender, RoutedEventArgs e) { Filter.Clear(); Filter.Focus(); }
    private void SongChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || refreshing || Busy || Songs.SelectedItem is not LibraryRow row || row.Item.Id == library.CurrentId) return;
        Run(() =>
        {
            StopAudition();
            library.Select(row.Item.Id);
            navigator.Reset();
            ShowDetail();
        });
    }
    private void NoteVersionChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        StopAudition();
        ShowVersion();
    }
    private void AddTextClick(object sender, RoutedEventArgs e)
    {
        if (Busy) return;
        Shell.IsEnabled = false;
        TextEditor.Visibility = Visibility.Visible;
        Status.Text = "";
        TextTitle.Focus();
    }
    private void CloseTextEditor(bool clear = false)
    {
        TextEditor.Visibility = Visibility.Collapsed;
        Shell.IsEnabled = true;
        if (clear) { TextTitle.Clear(); Notation.Clear(); }
        AddNotationButton.Focus();
    }
    private void CloseTextClick(object sender, RoutedEventArgs e)
    {
        if (job != null) { job.Cancel(); return; }
        CloseTextEditor();
        Status.Text = "就绪";
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && TextEditor.IsVisible) { CloseTextClick(sender, e); e.Handled = true; }
        else if (e.Key == Key.Escape && Filter.IsKeyboardFocusWithin) { Filter.Clear(); e.Handled = true; }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !Busy && !TextEditor.IsVisible && !compact)
        {
            LibraryTab.IsSelected = true;
            UpdateLayout();
            Filter.Focus();
            Filter.SelectAll();
            e.Handled = true;
        }
    }
    private async Task AddAsync(Func<Score> read, string path, CancellationToken token,
        string texture = Arrangement.ImportTexture, Dictionary<string, JsonElement>? metadata = null)
    {
        await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var score = read();
            token.ThrowIfCancellationRequested();
            library.Add(score, path, texture, token, metadata);
        }, token);
        Imported();
    }
    private void Imported()
    {
        navigator.Reset();
        if (TextEditor.IsVisible) CloseTextEditor(clear: true);
        Filter.Clear();
        LibraryTab.IsSelected = true;
        RefreshLibrary();
        Status.Text = $"已加入谱库 · {detail?.Title}";
    }
    private async void ImportClick(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        RequireIdle();
        var dialog = new OpenFileDialog { Filter = "曲谱|*.mid;*.midi;*.txt", Title = "导入曲谱" };
        if (dialog.ShowDialog(this) == true)
        {
            var settings = library.Settings;
            await JobAsync("正在导入并适配九键…", t => AddAsync(() => ScoreIO.Load(dialog.FileName, settings), dialog.FileName, t));
        }
    });
    private async void TextImportClick(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        string title = TextTitle.Text.Trim(), text = Notation.Text;
        if (title.Length == 0) title = "文本简谱";
        var s = library.Settings;
        await JobAsync("正在解析简谱…", t => AddAsync(() => ScoreIO.ParseText(text, s.Bpm, title, s.Octave), "文本简谱", t));
    });
    private void DeleteClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        RequireIdle();
        var item = SelectedDetail();
        if (MessageBox.Show(this, $"从谱库移除《{item.Title}》？原始文件会保留。", "删除曲谱",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        RemoveConfirmedScore(item.Id);
    });
    internal void RemoveConfirmedScore(string id)
    {
        RequireIdle();
        StopAudition();
        library.Remove(id, visibleIds);
        navigator.Reset();
        RefreshLibrary(removedId: id);
        Status.Text = "曲谱已移除";
    }
    private LibraryItem SelectedDetail() => detail ?? throw new InvalidOperationException("请先选择曲谱");
    private void ExportClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        RequireIdle();
        var item = SelectedDetail();
        var title = string.Concat(item.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dialog = new SaveFileDialog { Filter = "MIDI|*.mid;*.midi", DefaultExt = ".mid", FileName = title + ".mid" };
        if (dialog.ShowDialog(this) != true) return;
        ScoreIO.ExportMidi(item.Score, dialog.FileName);
        Status.Text = $"已导出 · {dialog.FileName}";
    });
    private async void AuditionClick(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        RequireIdle();
        if (listening != null && sender == ListenButton) { StopAudition(); return; }
        await ListenAsync(sender == SourceListen);
    });
    private async Task ListenAsync(bool source)
    {
        var item = SelectedDetail();
        var score = OriginalVersion.IsChecked == true ? item.Original : item.Score;
        var task = source ? audition.PlaySourceAsync(item.Source, cursor, null) :
            audition.PlayScoreAsync(score, cursor, null, library.Settings.Speed);
        listening = task;
        listeningToSource = source;
        ListenButton.Content = "停止试听";
        Status.Text = $"{(source ? "原音频" : "手碟实录")} · {item.Title}";
        try
        {
            await task;
            if (listening == task) Status.Text = "试听结束";
        }
        catch (Exception) when (listening != task) { /* A newer playback owns the status line. */ }
        finally
        {
            if (listening == task) ResetAudition();
        }
    }
    private void ResetAudition()
    {
        listening = null;
        pendingAuditionSeek = false;
        ListenButton.Content = "试听";
        var score = OriginalVersion.IsChecked == true ? detail?.Original : detail?.Score;
        SetTrackDuration(score?.Duration ?? 0);
        SetCursor(Math.Min(cursor, Seek.Maximum));
    }
    private void StopAudition()
    {
        audition.Stop();
        if (listening == null) return;
        ResetAudition();
        Status.Text = "试听已停止";
    }
    private void AudioFileClick(object sender, RoutedEventArgs e) => Run(() =>
    {
        var dialog = new OpenFileDialog { Filter = "本地音频|*.wav;*.mp3;*.flac;*.ogg" };
        if (dialog.ShowDialog(this) != true) return;
        audioFile = dialog.FileName;
        AudioPath.Text = Path.GetFileName(audioFile);
        AudioPath.ToolTip = audioFile;
    });
    private void EngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeepChords == null) return;
        Register.IsEnabled = KeepChords.IsEnabled = Engine.SelectedIndex == 0;
        if (!KeepChords.IsEnabled) KeepChords.IsChecked = false;
    }
    private async void TranscribeClick(object sender, RoutedEventArgs e) => await GuardAsync(async () =>
    {
        string? path = audioFile;
        if (!File.Exists(path)) throw new FileNotFoundException("请先选择本地音频");
        string engine = new[] { "instrument", "melody", "yin" }[Engine.SelectedIndex];
        string register = new[] { "auto", "high", "mid", "low" }[Register.SelectedIndex];
        string texture = KeepChords.IsChecked == true ? "chords" : "melody";
        var info = new Dictionary<string, string> { ["engine"] = engine, ["register"] = register, ["texture"] = texture };
        info["revision"] = AudioTranscriber.Revision;
        await JobAsync("正在识别本地音频…", async token =>
        {
            var progress = new Progress<double>(value => { if (job?.Token == token) Timeline.Value = Math.Clamp(value * 95, 0, 95); });
            await AddAsync(() => AudioTranscriber.Transcribe(path, engine, register, texture, token, progress), path, token, texture,
                new() { ["transcription"] = JsonSerializer.SerializeToElement(info) });
        });
    });
}
