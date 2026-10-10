using System.Collections.Specialized;
using AppleRemote.App.ViewModels;

namespace AppleRemote.App.Views;

public partial class DebugPage
{
    public DebugPage()
    {
        InitializeComponent();

        // Journal : une ligne par entrée, toujours défilé jusqu'à la dernière (300 lignes max, reconstruit à chaque changement).
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not MainViewModel vm) return;
            void Refresh()
            {
                LogBox.Text = string.Join(Environment.NewLine, vm.Log);
                LogBox.ScrollToEnd();
            }
            ((INotifyCollectionChanged)vm.Log).CollectionChanged += (_, _) => Refresh();
            Refresh();
        };
    }
}
