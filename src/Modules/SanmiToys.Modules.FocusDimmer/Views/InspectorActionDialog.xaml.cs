using System.Windows;

namespace SanmiToys.Modules.FocusDimmer.Views;

public partial class InspectorActionDialog : Window
{
    public string ActionType { get; private set; } = "";
    public string ProcessName { get; }
    public string WindowTitle { get; }

    public InspectorActionDialog(string processName, string windowTitle)
    {
        InitializeComponent();
        SanmiToys.Core.Helpers.WindowBackdropCompatibilityHelper.EnsureTransparentPopupCompatibility(this);
        ProcessName = processName;
        WindowTitle = windowTitle;
        ProcessNameText.Text = processName;
        WindowTitleText.Text = string.IsNullOrEmpty(windowTitle) ? "(タイトルなし)" : windowTitle;
    }

    private void Action_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement el)
        {
            ActionType = el.Tag?.ToString() ?? "";
            DialogResult = ActionType != "Cancel";
            Close();
        }
    }
}
