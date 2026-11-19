namespace WarnoModerator.Core.Ndf;

/// <summary>
/// Where compiled databases live in a mod's Gen\NDF folder. WARNO's November 2026 update kept ModGen 210942 but
/// renamed every database from <c>&lt;Db&gt;.ndfbin</c> to <c>&lt;Db&gt;.txn</c> (same EUG0/CNDF container), and moved
/// the per-name ones into folders: <c>Localisation\&lt;Name&gt;\DicoDef.txn</c> and
/// <c>ResourcePacks\&lt;Name&gt;\ResourcePackDefinition.txn</c>. Mods published before it still ship the old names.
/// </summary>
public static class NdfLayout
{
    private const string Root = "Gen\\NDF\\";
    private const string Legacy = ".ndfbin";
    private const string Transaction = ".txn";

    private static readonly (string Folder, string File)[] NamedFolders =
        [("Localisation", "DicoDef"), ("ResourcePacks", "ResourcePackDefinition")];

    public static bool IsLegacyDatabase(string relativePath) =>
        relativePath.StartsWith(Root, StringComparison.OrdinalIgnoreCase) && relativePath.EndsWith(Legacy, StringComparison.OrdinalIgnoreCase);

    public static bool IsTransactionDatabase(string relativePath) =>
        relativePath.StartsWith(Root, StringComparison.OrdinalIgnoreCase) && relativePath.EndsWith(Transaction, StringComparison.OrdinalIgnoreCase);

    public static bool IsDatabase(string relativePath) => IsLegacyDatabase(relativePath) || IsTransactionDatabase(relativePath);

    /// <summary>Gen\NDF\Localisation\X.ndfbin → Gen\NDF\Localisation\X\DicoDef.txn; Gen\NDF\GFX\Sound.ndfbin → Gen\NDF\GFX\Sound.txn.</summary>
    public static string ToTransactionPath(string legacyPath)
    {
        var stem = legacyPath[..^Legacy.Length];
        var parts = stem[Root.Length..].Split('\\');
        if (parts.Length == 2)
            foreach (var (folder, file) in NamedFolders)
                if (parts[0].Equals(folder, StringComparison.OrdinalIgnoreCase))
                    return $"{stem}\\{file}{Transaction}";
        return stem + Transaction;
    }

    /// <summary>The inverse of <see cref="ToTransactionPath"/>.</summary>
    public static string ToLegacyPath(string transactionPath)
    {
        var stem = transactionPath[..^Transaction.Length];
        var parts = stem[Root.Length..].Split('\\');
        if (parts.Length == 3)
            foreach (var (folder, file) in NamedFolders)
                if (parts[0].Equals(folder, StringComparison.OrdinalIgnoreCase) && parts[2].Equals(file, StringComparison.OrdinalIgnoreCase))
                    return Root + parts[0] + "\\" + parts[1] + Legacy;
        return stem + Legacy;
    }

    /// <summary>The Config.ini [Config] key of a database in either layout, such as GFX/Pawn or Localisation/X.</summary>
    public static string ManifestKey(string relativePath)
    {
        var legacy = IsTransactionDatabase(relativePath) ? ToLegacyPath(relativePath) : relativePath;
        return legacy[Root.Length..^Legacy.Length].Replace('\\', '/');
    }
}
