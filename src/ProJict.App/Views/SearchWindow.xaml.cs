using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ProJict.App.ViewModels;
using ProJict.Core.Features;
using ProJict.Core.Model;

namespace ProJict.App.Views;

public partial class SearchWindow : Window
{
    private readonly AppState _state;
    private readonly MainViewModel _main;

    public sealed record ResultRow(SearchHit Hit)
    {
        public string Snippet => Hit.Snippet;
        public string Location => Hit.Location;
        public string StateText => Hit.Item switch
        {
            null => "",
            { IsDone: true } => " · done",
            { State: ItemState.InProgress } => " · in progress",
            _ => "",
        };
    }

    public SearchWindow(AppState state, MainViewModel main)
    {
        InitializeComponent();
        _state = state;
        _main = main;
        Loaded += (_, _) => Query.Focus();
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        QueryHint.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var hits = GlobalSearch.Search(_state.Projects, Query.Text, 150);
        Results.ItemsSource = hits.Select(h => new ResultRow(h)).ToList();
        Count.Text = Query.Text.Trim().Length == 0 ? "" : hits.Count == 150 ? "150+ results" : $"{hits.Count} result(s)";
        if (hits.Count > 0) Results.SelectedIndex = 0;
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when Results.Items.Count > 0:
                Results.Focus();
                (Results.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, Results.SelectedIndex)) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
            case Key.Enter:
                Open();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Results_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Open();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Open();

    private void Open()
    {
        if (Results.SelectedItem is not ResultRow row) return;
        Close();
        Owner?.Activate();
        _main.Navigate(row.Hit);
    }
}
