using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using Leve.App.ViewModels;

namespace Leve.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Janela sem moldura nativa: ao maximizar, compensa a borda que o Windows empurra para fora da tela.
        StateChanged += (_, _) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is { Count: > 0 })
                LogList.ScrollIntoView(e.NewItems[e.NewItems.Count - 1]!);
        };

        Loaded += (_, _) => FocusList();

        // Fechar no meio do teste de taxa do monitor: volta a taxa anterior antes de sair.
        Closing += async (_, e) =>
        {
            if (DataContext is MainViewModel { IsDisplayTrialOpen: true } vm)
            {
                e.Cancel = true;
                await vm.RevertDisplayAsync("janela fechada no meio do teste");
                Close();
            }
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void FocusList()
    {
        ItemsList.Focus();
        if (ItemsList.SelectedIndex < 0 && ItemsList.Items.Count > 0) ItemsList.SelectedIndex = 0;
        if (ItemsList.ItemContainerGenerator.ContainerFromIndex(Math.Max(ItemsList.SelectedIndex, 0)) is UIElement row)
            row.Focus();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (DataContext is not MainViewModel)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        // Confirmação de troca de taxa do monitor: Enter mantém, Esc volta.
        if (Vm.IsDisplayTrialOpen)
        {
            if (e.Key == Key.Enter && !e.IsRepeat) Vm.KeepDisplayCommand.Execute(null);
            else if (e.Key == Key.Escape) Vm.RevertDisplayCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Segurar Enter não pode abrir E confirmar: auto-repetição é ignorada.
        if (e.IsRepeat && e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            return;
        }

        if (Vm.ShowConfirm)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Vm.ConfirmApplyCommand.Execute(null);
                    e.Handled = true;
                    break;
                case Key.Escape:
                case Key.E:
                    Vm.CancelConfirmCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
            return;
        }

        if (FilterBox.IsKeyboardFocusWithin)
        {
            if (e.Key is Key.Escape or Key.Enter or Key.Down)
            {
                if (e.Key == Key.Escape) Vm.FilterText = string.Empty;
                FocusList();
                e.Handled = true;
            }
            return;
        }

        if (Vm.ShowList && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            switch (e.Key)
            {
                case Key.Space:
                    Vm.ToggleSelected();
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Vm.ApplyCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    // Atalhos por caractere: funcionam em qualquer layout (ABNT2, US…).
    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (DataContext is not MainViewModel || Vm.ShowConfirm || Keyboard.FocusedElement is System.Windows.Controls.TextBox)
        {
            base.OnPreviewTextInput(e);
            return;
        }

        e.Handled = true;
        switch (e.Text)
        {
            case "/" when Vm.ShowList: FilterBox.Focus(); break;
            case "a" when Vm.ShowList: Vm.CheckAllCommand.Execute(null); break;
            case "x" when Vm.ShowList: Vm.UncheckAllCommand.Execute(null); break;
            case "u": Vm.UndoLastCommand.Execute(null); break;
            case "p": Vm.CycleProfile(); break;
            case "r": Vm.RefreshCommand.Execute(null); break;
            case "?": Vm.ShowHelp(); break;
            default: e.Handled = false; break;
        }

        base.OnPreviewTextInput(e);
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
