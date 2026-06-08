using System.Windows;
using RamAI.Phase4.Models;
using RamAI.Phase4.ViewModels;

namespace RamAI.Phase4.Views;

public partial class LicenseWindow : Window
{
    public LicenseWindow()
    {
        InitializeComponent();
        var vm = new LicenseViewModel(App.LicenseService);
        vm.RequestClose += () =>
        {
            DialogResult = true;
            Close();
        };
        DataContext = vm;
    }

    /// <summary>
    /// Intercepte toutes les tentatives de fermeture (bouton X, Alt+F4, etc.).
    /// Si aucune licence valide n'est active, l'utilisateur ne peut PAS fermer
    /// la fenêtre sans quitter l'application.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // DialogResult = true → fermeture déclenchée par ValidateKey (succès) : laisser passer
        if (DialogResult == true)
        {
            base.OnClosing(e);
            return;
        }

        // Aucune licence active → bloquer X / Alt+F4 ; seul le bouton "Quitter RAM-AI" est autorisé
        if (App.LicenseService.Current.Tier == LicenseTier.None)
        {
            e.Cancel = true;
            System.Windows.MessageBox.Show(
                "Vous devez entrer une clé de licence valide pour utiliser RAM-AI.\n\n" +
                "Utilisez le bouton « Quitter RAM-AI » pour fermer l'application.",
                "RAM-AI — Licence requise",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else
        {
            base.OnClosing(e);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.LicenseService.Current.Tier != LicenseTier.None)
        {
            DialogResult = true;
            Close();
        }
    }

    /// <summary>Bouton "Quitter RAM-AI" — ferme proprement l'application entière.</summary>
    private void QuitButton_Click(object sender, RoutedEventArgs e)
    {
        // Contourner la protection OnClosing : on quitte volontairement
        DialogResult = false;
        System.Windows.Application.Current.Shutdown();
    }
}
