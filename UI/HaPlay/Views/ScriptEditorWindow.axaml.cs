using System.ComponentModel;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Platform;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using HaPlay.ViewModels;

namespace HaPlay.Views;

/// <summary>
/// Pop-out editor for a single control script. Hosts the AvaloniaEdit code editor plus the script's
/// metadata, trigger editor, learn mode, and diagnostics. It binds to the live
/// <see cref="ControlWorkspaceViewModel"/>, so it always reflects the workspace's selected script.
/// </summary>
public partial class ScriptEditorWindow : Window
{
    private ControlWorkspaceViewModel? _viewModel;
    private bool _updatingEditor;

    public ScriptEditorWindow()
    {
        InitializeComponent();
        ApplyMondSyntaxHighlighting();
        ScriptEditor.TextChanged += OnScriptEditorTextChanged;
        DataContextChanged += OnDataContextChanged;
        BindViewModel(DataContext as ControlWorkspaceViewModel);
    }

    private void ApplyMondSyntaxHighlighting()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://HaPlay/Assets/Mond.xshd"));
            using var reader = XmlReader.Create(stream);
            ScriptEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch
        {
            // Highlighting is a nicety; fall back to plain text if the definition can't load.
        }
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ScriptEditor.TextChanged -= OnScriptEditorTextChanged;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }
}
