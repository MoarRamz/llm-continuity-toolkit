using System.Windows;
using LLMContinuityToolkit.Core;

namespace LLMContinuityToolkit.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
