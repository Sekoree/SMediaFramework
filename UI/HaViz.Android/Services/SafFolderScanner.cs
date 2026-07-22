using Android.App;
using Android.Content;
using Android.Provider;
using HaViz.Core;
using AUri = Android.Net.Uri;

namespace HaViz.Android.Services;

/// <summary>
/// SAF folder pick + recursive scan. Enumerates with raw DocumentsContract child queries (one
/// resolver query per directory) instead of DocumentFile, which issues several queries per node
/// and is painfully slow on large trees.
/// </summary>
public sealed class SafFolderScanner : IMediaFolderScanner
{
    // Extension fallback for providers that report generic MIMEs (e.g. .mka as video/x-matroska).
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav", ".mka",
    };

    private readonly MainActivity _activity;

    public SafFolderScanner(MainActivity activity) => _activity = activity;

    public async Task<IReadOnlyList<TrackInfo>> PickAndScanAsync()
    {
        var result = await _activity.StartActivityForResultAsync(new Intent(Intent.ActionOpenDocumentTree));
        if (result.ResultCode != Result.Ok || result.Data?.Data is not { } treeUri)
            return [];

        // Keep read access across process restarts so previously picked folders stay scannable.
        _activity.ContentResolver!.TakePersistableUriPermission(treeUri, ActivityFlags.GrantReadUriPermission);
        return await Task.Run(() => Scan(treeUri));
    }

    private List<TrackInfo> Scan(AUri treeUri)
    {
        var resolver = _activity.ContentResolver!;
        string[] projection =
        [
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
        ];
        var tracks = new List<TrackInfo>();
        var directories = new Stack<string>();
        directories.Push(DocumentsContract.GetTreeDocumentId(treeUri)!);

        while (directories.Count > 0)
        {
            var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, directories.Pop())!;
            using var cursor = resolver.Query(childrenUri, projection, null, null, null);
            if (cursor is null)
                continue;
            while (cursor.MoveToNext())
            {
                var documentId = cursor.GetString(0);
                if (documentId is null)
                    continue;
                var name = cursor.GetString(1) ?? documentId;
                var mime = cursor.GetString(2);
                if (mime == DocumentsContract.Document.MimeTypeDir)
                    directories.Push(documentId);
                else if (IsAudio(mime, name))
                    tracks.Add(new TrackInfo(
                        DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId)!.ToString()!,
                        name));
            }
        }

        tracks.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
        return tracks;
    }

    private static bool IsAudio(string? mime, string name) =>
        mime?.StartsWith("audio/", StringComparison.Ordinal) == true
        || AudioExtensions.Contains(Path.GetExtension(name));
}
