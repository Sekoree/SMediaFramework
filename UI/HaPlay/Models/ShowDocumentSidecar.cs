using HaPlay.Playback;
using S.Media.Session;

namespace HaPlay.Models;

/// <summary>
/// D10 (view-state-only persistence, post-pivot resolution): every full project save also writes the
/// framework <see cref="ShowDocument"/> for each cue list next to the project file, so a saved show is
/// directly runnable headless or through the C ABI (<c>s_media_player</c>) without HaPlay. The project
/// file stays the app's editing source of truth (decks, soundboards, control graphs and endpoints are
/// app-domain, not show-execution data); these sidecars are the execution/interchange artifact — the
/// exact document the in-app cue ShowSession loads, produced by the same mapper.
/// </summary>
public static class ShowDocumentSidecar
{
    /// <summary>Sidecar path for the 1-based cue-list index: <c>&lt;projectbase&gt;.show.&lt;n&gt;.json</c>.
    /// The index (not the list name) keys the file so renaming a cue list can't orphan a sidecar.</summary>
    public static string PathFor(string projectPath, int listIndex)
    {
        var directory = Path.GetDirectoryName(projectPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(projectPath);
        return Path.Combine(directory, $"{baseName}.show.{listIndex}.json");
    }

    /// <summary>
    /// Writes one validated show document per cue list and removes stale
    /// <c>&lt;projectbase&gt;.show.*.json</c> siblings first (the list count can shrink between saves).
    /// A list that fails to map or validate is skipped and reported via <paramref name="errors"/> —
    /// a sidecar problem must never fail the project save itself.
    /// </summary>
    public static async Task<IReadOnlyList<string>> WriteAllAsync(
        HaPlayProject project, string projectPath, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrEmpty(projectPath);
        ArgumentNullException.ThrowIfNull(errors);

        var directory = Path.GetDirectoryName(projectPath) ?? ".";
        var baseName = Path.GetFileNameWithoutExtension(projectPath);
        foreach (var stale in Directory.GetFiles(directory, $"{baseName}.show.*.json"))
        {
            try { File.Delete(stale); }
            catch (Exception ex) { errors.Add($"{Path.GetFileName(stale)}: {ex.Message}"); }
        }

        var written = new List<string>();
        for (var i = 0; i < project.CueLists.Count; i++)
        {
            var list = project.CueLists[i];
            try
            {
                var document = HaPlayShowMapper.ToShowDocument(list, project.Outputs);
                // The same gate ShowSession.LoadDocument applies — never publish a sidecar a headless
                // host would reject at load.
                ShowDocumentValidator.ThrowIfInvalid(document);
                var path = PathFor(projectPath, i + 1);
                await File.WriteAllTextAsync(path, document.ToJson()).ConfigureAwait(false);
                written.Add(path);
            }
            catch (Exception ex)
            {
                errors.Add($"'{list.Name}': {ex.Message}");
            }
        }

        return written;
    }
}
