using System.Security.Cryptography;

namespace WarnoModerator.Core.Ndf;

/// <summary>
/// A generated file's .tag (TNDFTagFile): the build configuration hash and, for every source file the file was built
/// from, the first 8 bytes of that source's MD5. When WARNO loads a mod it compares those hashes with the sources the
/// mod ships; any difference makes it treat the file as out of date and try to rebuild it, which a released mod can't
/// do ("No writable component found: 'ZZ:/NDF/Scenario/GFX/Skirmish.txn'"). A combined mod takes its sources from
/// several mods, so every tag is re-stamped with the hashes of the sources the combined mod actually contains.
/// </summary>
public static class GenerationTag
{
    private const string TagClass = "TNDFTagFile";
    private const string HashesField = "SourceFileHashes";
    private const uint HashType = 0x13;

    /// <summary>True for a tag that lists source hashes (other .tag files, such as pack tags, are left as they are).</summary>
    public static bool HasSourceHashes(byte[] file)
    {
        if (!NdfContainer.IsNdfBinary(file)) return false;
        var database = NdfDatabase.Load(file);
        return database.TopLevel.Count == 1 && database.TopLevel[0].Value.ClassName == TagClass
            && database.TopLevel[0].Value.Get(HashesField) is NdfMap;
    }

    /// <summary>The tag's source files, such as "GameData:/Gameplay/Skirmish/IASkirmishDescriptors.ndf".</summary>
    public static IReadOnlyList<string> Sources(byte[] tag) => HashMap(NdfDatabase.Load(tag)).Entries
        .Select(e => e.Key is NdfText text ? text.Text : throw new NdfFormatException("A tag source is not a path.")).ToArray();

    /// <summary>The tag with each source's hash replaced by the hash of <paramref name="sourceFile"/>(source).</summary>
    public static byte[] Restamp(byte[] tag, Func<string, string> sourceFile)
    {
        var database = NdfDatabase.Load(tag);
        var root = database.TopLevel[0];
        var hashes = HashMap(database);
        var entries = hashes.Entries.Select(e =>
        {
            var path = ((NdfText)e.Key).Text;
            using var stream = File.OpenRead(sourceFile(path));
            return new KeyValuePair<NdfValue, NdfValue>(e.Key, new NdfRaw(HashType, MD5.HashData(stream)[..8]));
        }).ToArray();
        var fields = root.Value.Fields.Select(f => f.Key == HashesField
            ? new KeyValuePair<string, NdfValue>(f.Key, new NdfMap(entries)) : f).ToArray();
        var restamped = new NdfObject(root.Value.ClassName, fields, root.Value.Origin);
        return new NdfDatabase([root with { Value = restamped }], database.PropertyOwners).Save();
    }

    /// <summary>"GameData:/A/B.ndf" → "GameData\A\B.ndf", the path of that source inside a mod folder.</summary>
    public static string ModRelativePath(string source)
    {
        var colon = source.IndexOf(":/", StringComparison.Ordinal);
        if (colon <= 0) throw new NdfFormatException($"Unexpected tag source path {source}.");
        return source[..colon] + "\\" + source[(colon + 2)..].Replace('/', '\\');
    }

    private static NdfMap HashMap(NdfDatabase database) =>
        database.TopLevel is [{ Value: { ClassName: TagClass } root }] && root.Get(HashesField) is NdfMap map
            ? map : throw new NdfFormatException("Not a generation tag with source hashes.");
}
