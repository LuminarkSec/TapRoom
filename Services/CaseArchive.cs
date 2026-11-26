namespace TapRoom.Services;

internal static class CaseArchive
{
    internal static string Move(string storageRoot, string caseDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(storageRoot, "Cases")));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(caseDirectory));
        if (!string.Equals(Path.GetDirectoryName(source), root, StringComparison.OrdinalIgnoreCase) ||
            (!File.Exists(Path.Combine(source, "metadata.json")) && !File.Exists(Path.Combine(source, "metadata.json.bak"))))
            throw new IOException("Case is not a recognised direct child of the configured Cases directory.");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing to archive a junction or symbolic link.");
        var archive = Path.Combine(storageRoot, "ArchivedCases");
        Directory.CreateDirectory(archive);
        if ((File.GetAttributes(archive) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The archive directory must not be a junction or symbolic link.");
        // Keep the original folder name unless it already exists in the archive.
        var destination = Path.Combine(archive, Path.GetFileName(source));
        if (Directory.Exists(destination) || File.Exists(destination))
            destination += "_" + Guid.NewGuid().ToString("N");
        Directory.Move(source, destination); // same-volume move; no recursive deletion
        return destination;
    }
}
