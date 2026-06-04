using System.ComponentModel;
using Avalonia.Controls;
using HaPlay.ViewModels;

namespace HaPlay.Views;

public partial class ControlWorkspaceView : UserControl
{
    private ControlWorkspaceViewModel? _viewModel;
    private bool _updatingEditor;

    public ControlWorkspaceView()
    {
        InitializeComponent();
        ScriptEditor.TextChanged += OnScriptEditorTextChanged;
        DataContextChanged += OnDataContextChanged;
        BindViewModel(DataContext as ControlWorkspaceViewModel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e) =>
        BindViewModel(DataContext as ControlWorkspaceViewModel);

    private void BindViewModel(ControlWorkspaceViewModel? viewModel)
    {
        if (ReferenceEquals(_viewModel, viewModel))
            return;

        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            SetEditorText(_viewModel.SelectedScriptText);
        }
        else
        {
            SetEditorText(string.Empty);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlWorkspaceViewModel.SelectedScriptText))
            SetEditorText(_viewModel?.SelectedScriptText ?? string.Empty);
    }

    private void OnScriptEditorTextChanged(object? sender, EventArgs e)
    {
        if (_updatingEditor || _viewModel is null)
            return;
        _viewModel.SelectedScriptText = ScriptEditor.Text;
    }

    private void SetEditorText(string text)
    {
        if (ScriptEditor.Text == text)
            return;

        _updatingEditor = true;
        try
        {
            ScriptEditor.Text = text;
        }
        finally
        {
            _updatingEditor = false;
        }
    }
}
