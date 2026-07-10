using System.Security.Cryptography;
using System.Text;

namespace HaPlay.Models;

/// <summary>Stable content hash of a <see cref="HaPlayProject"/> (its serialized JSON). Used to detect
/// unsaved changes without a central dirty flag - the shell compares the current snapshot's hash against a
/// baseline captured at the last New / Open / Save.</summary>
public static class ProjectHash
{
    public static string Of(HaPlayProject project) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ProjectIO.Serialize(project))));
}
