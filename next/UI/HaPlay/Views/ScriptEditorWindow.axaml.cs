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
/// Pop-out editor for ONE control script (one script per window). The window's
/// <see cref="ScriptEditorWindowViewModel"/> pins its script row for the window's lifetime; window
/// activation re-asserts the pinned row as the workspace selection so the selection-scoped
/// machinery (buffer, save, learn, diagnostics) targets this script while it is being edited.
/// </summary>
public partial class ScriptEditorWindow : Window
{
    private ScriptEditorWindowViewModel? _viewModel;
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
        Activated += (_, _) => _viewModel?.PinSelection();
        BindViewModel(DataContext as ScriptEditorWindowViewModel);
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
        BindViewModel(DataContext as ScriptEditorWindowViewModel);

    private void BindViewModel(ScriptEditorWindowViewModel? viewModel)
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
        if (e.PropertyName == nameof(ScriptEditorWindowViewModel.SelectedScriptText))
            BindSelectedScriptDocument();
    }

    private void BindSelectedScriptDocument()
    {
        var path = _viewModel?.SelectedScriptRow.ScriptPath?.Trim() ?? string.Empty;
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
        {
            var document = ScriptEditor.Document!;
            if (text.StartsWith(document.Text, StringComparison.Ordinal)
                && text.Length > document.TextLength)
            {
                _updatingEditor = true;
                try
                {
                    document.Insert(document.TextLength, text[document.TextLength..]);
                }
                finally
                {
                    _updatingEditor = false;
                }
            }
            else
            {
                SetDocumentText(document, text);
            }
        }
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
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Detach();
        }
        _viewModel = null;
        _activeScriptPath = null;
        _documents.Clear();
    }
}
