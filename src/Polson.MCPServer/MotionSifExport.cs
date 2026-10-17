namespace Polson.MCPServer;

using Polson.Animation;

/// <summary>
/// A film's <c>.sif</c> taken on demand from its script: what <c>polson eval script.js --sif out.sif</c> writes.
/// </summary>
/// <remarks>
/// A run no longer has to write the <c>.sif</c> itself. With text animators it is a bake — a group per letter, its motion
/// sampled at every frame — that nobody edits, and it cost trailer1 56 MB; the script is the editable source, and the
/// export is there for whoever wants the film in Synfig Studio. Here rather than on the composition, whose public
/// members are script members.
/// </remarks>
public static class MotionSifExport
{
    /// <summary>Writes <paramref name="composition"/> to <paramref name="fullPath"/>, pictures beside it.</summary>
    /// <returns>The bytes written and the pictures' paths.</returns>
    /// <exception cref="System.InvalidOperationException">A layer Synfig cannot hold, refused by name.</exception>
    public static (long Bytes, string[] Pictures) Write(MotionComposition composition, string fullPath) =>
        composition.WriteSif(fullPath);
}
