using System.Windows;
using RamAI.Phase4.ViewModels;

namespace RamAI.Phase4.Views;

public partial class MainWindow : Window
{
    // La bulle "RAM-AI continue en arrière-plan" ne s'affiche qu'une seule fois
    // (la première fois que l'utilisateur clique sur la croix de la fenêtre).
    private bool _balloonShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.MainVm;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Annuler la fermeture : masquer dans le tray plutôt que quitter.
        e.Cancel = true;
        Hide();

        if (!_balloonShown)
        {
            _balloonShown = true;
            App.ShowTrayBalloon(
                "RAM-AI",
                "RAM-AI continue d'optimiser en arrière-plan.\nDouble-cliquez sur l'icône pour rouvrir.");
        }
    }
}
