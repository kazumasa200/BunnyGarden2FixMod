using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace BunnyGarden2FixMod.ConfigGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var (yamlPath, outPath, outMdPath) = ParseArgs(args);

            var yaml = File.ReadAllText(yamlPath);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var sections = deserializer.Deserialize<List<SectionDef>>(yaml);
            var entries = new List<ConfigEntryDef>();
            var seenSections = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in sections)
            {
                if (string.IsNullOrEmpty(s.Section))
                    throw new InvalidOperationException("[ConfigGen] section name is required for each YAML group");
                if (!seenSections.Add(s.Section))
                    throw new InvalidOperationException($"[ConfigGen] section '{s.Section}' is declared more than once; merge the blocks");
                if (s.Configs.Count == 0)
                    throw new InvalidOperationException($"[ConfigGen] section '{s.Section}' has no configs (check for typos like 'confgs:' or empty list)");
                foreach (var e in s.Configs)
                {
                    e.Section = s.Section;
                    entries.Add(e);
                }
            }
            if (entries.Count == 0)
                throw new InvalidOperationException("[ConfigGen] no entries parsed (is the YAML still in legacy flat format?)");
            Console.WriteLine($"[ConfigGen] Parsed {entries.Count} entries across {sections.Count} sections from {yamlPath}");

            var errors = Validator.Validate(entries);
            if (errors.Count > 0)
            {
                Console.Error.WriteLine($"[ConfigGen] Validation failed with {errors.Count} error(s):");
                foreach (var e in errors) Console.Error.WriteLine($"  - {e}");
                return 2;
            }

            var generated = CodeEmitter.Emit(entries);
            File.WriteAllText(outPath, generated);
            Console.WriteLine($"[ConfigGen] Wrote {generated.Length} chars to {outPath}");

            if (!string.IsNullOrEmpty(outMdPath))
            {
                var md = MarkdownEmitter.Emit(sections);
                File.WriteAllText(outMdPath, md);
                Console.WriteLine($"[ConfigGen] Wrote {md.Length} chars to {outMdPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ConfigGen] ERROR: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
                Console.Error.WriteLine($"[ConfigGen]   inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static (string yamlPath, string outPath, string? outMdPath) ParseArgs(string[] args)
    {
        string? yaml = null, output = null, outputMd = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--yaml" || args[i] == "--out" || args[i] == "--out-md")
            {
                // 値が後続するフラグ。末尾に値なしで残った場合に黙殺せず明示的にエラーにする。
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"{args[i]} requires a value");
                if (args[i] == "--yaml") yaml = args[++i];
                else if (args[i] == "--out") output = args[++i];
                else outputMd = args[++i];
            }
        }
        if (yaml == null || output == null)
            throw new ArgumentException("Usage: ConfigGen --yaml <input.yaml> --out <output.cs> [--out-md <output.md>]");
        return (yaml, output, outputMd);
    }
}
