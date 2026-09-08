using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SanmiToys.Modules.OmniGlance.Models;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace SanmiToys.Modules.OmniGlance.Views;

public partial class CalendarEventEditorWindow : Window
{
    private readonly CalendarEvent? _editingEvent;
    private readonly OmniGlanceSettings _settings;
    private readonly bool _isGoogleAvailable;
    private readonly bool _isICloudAvailable;
    private string _selectedColorHex = "#4CC2FF";

    public CalendarEvent? ResultEvent { get; private set; }
    public bool IsDeleted { get; private set; }

    public CalendarEventEditorWindow(
        CalendarEvent? eventToEdit,
        DateTime defaultDate,
        OmniGlanceSettings settings,
        bool isGoogleAvailable = false,
        bool isICloudAvailable = false)
    {
        InitializeComponent();

        _editingEvent = eventToEdit;
        _settings = settings;
        _isGoogleAvailable = isGoogleAvailable;
        _isICloudAvailable = isICloudAvailable;

        InitializeForm(defaultDate);
    }

    private void InitializeForm(DateTime defaultDate)
    {
        // カレンダー選択肢の有効状態
        GoogleCalendarOption.IsEnabled = _isGoogleAvailable;
        if (!_isGoogleAvailable)
        {
            GoogleCalendarOption.Content = "Google カレンダー (※設定からログインが必要)";
        }
        ICloudCalendarOption.IsEnabled = _isICloudAvailable;
        if (!_isICloudAvailable)
        {
            ICloudCalendarOption.Content = "iPhone / iCloud (※設定から連携が必要)";
        }

        if (_editingEvent != null)
        {
            // 編集モード
            WindowTitleText.Text = "予定を編集";
            HeaderIcon.Symbol = SymbolRegular.Edit24;
            SaveBtn.Content = "保存";
            DeleteEventBtn.Visibility = Visibility.Visible;

            TitleTextBox.Text = _editingEvent.Title;
            EventDatePicker.SelectedDate = _editingEvent.StartTime.Date;
            AllDayCheckBox.IsChecked = _editingEvent.IsAllDay;
            TimePanel.Visibility = _editingEvent.IsAllDay ? Visibility.Collapsed : Visibility.Visible;

            StartTimeTextBox.Text = _editingEvent.StartTime.ToString("HH:mm");
            EndTimeTextBox.Text = _editingEvent.EndTime.ToString("HH:mm");

            LocationTextBox.Text = _editingEvent.Location ?? string.Empty;
            DescriptionTextBox.Text = _editingEvent.Description ?? string.Empty;

            _selectedColorHex = !string.IsNullOrWhiteSpace(_editingEvent.ColorHex) ? _editingEvent.ColorHex : "#4CC2FF";

            // カレンダー種別の選択
            if (_editingEvent.ProviderType == CalendarProviderType.GoogleCalendar && _isGoogleAvailable)
            {
                CalendarTypeComboBox.SelectedItem = GoogleCalendarOption;
            }
            else if (_editingEvent.ProviderType == CalendarProviderType.AppleICloud && _isICloudAvailable)
            {
                CalendarTypeComboBox.SelectedItem = ICloudCalendarOption;
            }
            else
            {
                CalendarTypeComboBox.SelectedItem = LocalCalendarOption;
            }
        }
        else
        {
            // 新規作成モード
            WindowTitleText.Text = "新しい予定を追加";
            HeaderIcon.Symbol = SymbolRegular.CalendarAdd24;
            SaveBtn.Content = "追加";
            DeleteEventBtn.Visibility = Visibility.Collapsed;

            EventDatePicker.SelectedDate = defaultDate.Date;
            AllDayCheckBox.IsChecked = false;
            TimePanel.Visibility = Visibility.Visible;

            var now = DateTime.Now;
            var start = defaultDate.Date == DateTime.Today
                ? new DateTime(defaultDate.Year, defaultDate.Month, defaultDate.Day, Math.Min(23, now.Hour + 1), 0, 0)
                : new DateTime(defaultDate.Year, defaultDate.Month, defaultDate.Day, 10, 0, 0);
            var end = start.AddHours(1);

            StartTimeTextBox.Text = start.ToString("HH:mm");
            EndTimeTextBox.Text = end.ToString("HH:mm");

            _selectedColorHex = "#4CC2FF";

            if (_isGoogleAvailable)
            {
                CalendarTypeComboBox.SelectedItem = GoogleCalendarOption;
            }
            else if (_isICloudAvailable)
            {
                CalendarTypeComboBox.SelectedItem = ICloudCalendarOption;
            }
            else
            {
                CalendarTypeComboBox.SelectedItem = LocalCalendarOption;
            }
        }

        UpdateColorPaletteUi();
    }

    private void UpdateColorPaletteUi()
    {
        Border[] buttons = { ColorBtn1, ColorBtn2, ColorBtn3, ColorBtn4, ColorBtn5 };
        foreach (var btn in buttons)
        {
            if (btn.Tag is string hex && string.Equals(hex, _selectedColorHex, StringComparison.OrdinalIgnoreCase))
            {
                btn.BorderThickness = new Thickness(2.5);
                btn.BorderBrush = Brushes.White;
            }
            else
            {
                btn.BorderThickness = new Thickness(0);
                btn.BorderBrush = Brushes.Transparent;
            }
        }
    }

    private void OnColorChoiceClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string hex)
        {
            _selectedColorHex = hex;
            UpdateColorPaletteUi();
        }
    }

    private void OnAllDayChanged(object sender, RoutedEventArgs e)
    {
        bool isAllDay = AllDayCheckBox.IsChecked == true;
        TimePanel.Visibility = isAllDay ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCalendarTypeChanged(object sender, SelectionChangedEventArgs e)
    {
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            $"予定「{_editingEvent?.Title}」を削除してもよろしいですか？",
            "予定の削除確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            IsDeleted = true;
            DialogResult = true;
            Close();
        }
    }

    private void OnSaveClicked(object sender, RoutedEventArgs e)
    {
        string title = TitleTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            TitleTextBox.Focus();
            return;
        }

        DateTime date = EventDatePicker.SelectedDate ?? DateTime.Today;
        bool isAllDay = AllDayCheckBox.IsChecked == true;
        DateTime startTime;
        DateTime endTime;

        if (isAllDay)
        {
            startTime = date.Date;
            endTime = date.Date.AddDays(1);
        }
        else
        {
            if (!TimeSpan.TryParse(StartTimeTextBox.Text.Trim(), out var startTs))
            {
                startTs = new TimeSpan(10, 0, 0);
            }
            if (!TimeSpan.TryParse(EndTimeTextBox.Text.Trim(), out var endTs))
            {
                endTs = startTs.Add(TimeSpan.FromHours(1));
            }

            startTime = date.Date.Add(startTs);
            endTime = date.Date.Add(endTs);
            if (endTime <= startTime)
            {
                endTime = startTime.AddHours(1);
            }
        }

        CalendarProviderType providerType = CalendarProviderType.Local;
        string calendarName = "マイカレンダー";
        string calendarId = "local";

        if (CalendarTypeComboBox.SelectedItem is ComboBoxItem selectedItem)
        {
            string tag = selectedItem.Tag as string ?? "Local";
            if (tag == "Google" && _isGoogleAvailable)
            {
                providerType = CalendarProviderType.GoogleCalendar;
                calendarName = "Google カレンダー";
                calendarId = "google";
            }
            else if (tag == "ICloud" && _isICloudAvailable)
            {
                providerType = CalendarProviderType.AppleICloud;
                calendarName = !string.IsNullOrWhiteSpace(_settings.ICloudCalendarName) ? _settings.ICloudCalendarName : "iPhone カレンダー";
                calendarId = "icloud";
            }
        }

        if (_editingEvent != null)
        {
            _editingEvent.Title = title;
            _editingEvent.StartTime = startTime;
            _editingEvent.EndTime = endTime;
            _editingEvent.IsAllDay = isAllDay;
            _editingEvent.Location = LocationTextBox.Text.Trim();
            _editingEvent.Description = DescriptionTextBox.Text.Trim();
            _editingEvent.ColorHex = _selectedColorHex;
            _editingEvent.ProviderType = providerType;
            _editingEvent.CalendarName = calendarName;
            _editingEvent.CalendarId = calendarId;
            ResultEvent = _editingEvent;
        }
        else
        {
            ResultEvent = new CalendarEvent
            {
                Id = Guid.NewGuid().ToString(),
                CalendarId = calendarId,
                CalendarName = calendarName,
                ProviderType = providerType,
                Title = title,
                StartTime = startTime,
                EndTime = endTime,
                IsAllDay = isAllDay,
                Location = LocationTextBox.Text.Trim(),
                Description = DescriptionTextBox.Text.Trim(),
                ColorHex = _selectedColorHex
            };
        }

        DialogResult = true;
        Close();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not TextBox && e.OriginalSource is not Button)
        {
            DragMove();
        }
    }
}
