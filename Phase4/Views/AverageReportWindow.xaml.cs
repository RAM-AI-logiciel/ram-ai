using System.Windows;
using RamAI.Phase4.Services;
using RamAI.Phase4.ViewModels;

namespace RamAI.Phase4.Views;

public partial class AverageReportWindow : Window
{
    public AverageReportWindow()
    {
        InitializeComponent();
        DataContext = new AverageReportViewModel(new SessionHistoryService());
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
