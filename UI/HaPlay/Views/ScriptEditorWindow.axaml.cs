using System.Collections.Generic;
using System.ComponentModel;
using System.Xml;
using Avalonia.Controls;
using Avalonia.Platform;
using AvaloniaEdit.Document;
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
    private string? _activeScriptPath;
    private readonly Dictionary<string, TextDocument> _documents = new(StringComparer.Ordinal);

    public ScriptEditorWindow()
    {
        InitializeComponent();
        ApplyMondSyntaxHighlighting();
        ScriptEditor.Document = new TextDocument();
        ScriptEditor.Document.TextChanged += OnEditorDocumentTextChanged;
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
            BindSelectedScriptDocument();
        }
        else
        {
            BindDocument(null, string.Empty);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ControlWorkspaceViewModel.SelectedScriptText)
            || e.PropertyName == nameof(ControlWorkspaceViewModel.SelectedScriptRow))
        {
            BindSelectedScriptDocument();
        }
    }

    private void BindSelectedScriptDocument()
    {
        var path = _viewModel?.SelectedScriptRow?.ScriptPath?.Trim() ?? string.Empty;
        var text = _viewModel?.SelectedScriptText ?? string.Empty;
        BindDocument(string.IsNullOrEmpty(path) ? null : path, text);
    }

    private void BindDocument(string? scriptPath, string text)
    {
        if (scriptPath is null)
        {
            _activeScriptPath = null;
            SetDocumentText(ScriptEditor.Document ?? new TextDocument(), string.Empty);
            return;
        }

        if (!ReferenceEquals(_activeScriptPath, scriptPath) || !ReferenceEquals(ScriptEditor.Document, _documents.GetValueOrDefault(scriptPath)))
        {
            _activeScriptPath = scriptPath;
            var document = GetOrCreateDocument(scriptPath, text);
            ScriptEditor.Document = document;
            return;
        }

        if (ScriptEditor.Document?.Text != text)
            SetDocumentText(ScriptEditor.Document!, text);
    }

    private TextDocument GetOrCreateDocument(string scriptPath, string text)
    {
        if (_documents.TryGetValue(scriptPath, out var existing))
        {
            if (existing.Text != text)
                SetDocumentText(existing, text);
            return existing;
        }

        var document = new TextDocument(text);
        _documents[scriptPath] = document;
        return document;
    }

    private void OnEditorDocumentTextChanged(object? sender, EventArgs e)
    {
        if (_updatingEditor || _viewModel is null || _activeScriptPath is null)
            return;

        var text = ScriptEditor.Document?.Text ?? string.Empty;
        if (_viewModel.SelectedScriptText != text)
            _viewModel.SelectedScriptText = text;
    }

    private void SetDocumentText(TextDocument document, string text)
    {
        if (document.Text == text)
            return;

        _updatingEditor = true;
        try
        {
            document.Text = text;
        }
        finally
        {
            _updatingEditor = false;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (ScriptEditor.Document is not null)
            ScriptEditor.Document.TextChanged -= OnEditorDocumentTextChanged;
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
        _activeScriptPath = null;
        _documents.Clear();
    }
}
