using System.Windows;
using Switch2ProManager.ViewModels;

namespace Switch2ProManager.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        Closed += (_, _) => vm.Dispose();
    }
}
