using System.Text;

namespace Kedo.Benchmarks;

/// <summary>Generates a deterministic synthetic module: N objects across a few types with inheritance, mixins, keyed lists and hooks.</summary>
public static class SyntheticCorpus
{
    /// <summary>Builds the source of one file with <paramref name="objects"/> objects (~500 bytes each).</summary>
    public static string Generate(int objects, int seed = 1)
    {
        var sb = new StringBuilder(objects * 520);
        sb.Append("kedo 1\nmodule bench\n\n");
        sb.Append("mixin armored { component health { regen = 1 }  tags = [armored] }\n");
        sb.Append("mixin fast { component patrol { interval = 5min }  tags = [fast] }\n\n");
        sb.Append("abstract actor base_actor {\n    name_key = \"actor.base\"\n    tags = [machine]\n    component health { max = 40  regen = 0 }\n    component patrol { radius = 6  interval = 10min }\n    options = [{ id = safe  duration = 30min  resolution = deterministic }, { id = risk  duration = 0min  resolution = d20 }]\n}\n\n");
        var rng = seed;
        for (var i = 0; i < objects; i++)
        {
            rng = unchecked(rng * 1103515245 + 12345);
            var parent = i == 0 || (rng & 3) == 0 ? "base_actor" : $"actor_{Math.Max(0, i - 1 - (rng >> 4 & 7))}";
            var mixin = (rng >> 8 & 3) switch { 0 => " with armored", 1 => " with fast", 2 => " with armored, fast", _ => "" };
            sb.Append($"/// Synthetic actor {i}.\n");
            sb.Append($"actor actor_{i} extends {parent}{mixin} {{\n");
            sb.Append($"    name_key = \"actor.{i}\"\n");
            sb.Append($"    tags += [t{i % 17}]\n");
            sb.Append($"    component health {{ max = {40 + i % 60} }}\n");
            sb.Append($"    options[risk] {{ check = {{ attribute = agility  difficulty = {10 + i % 8} }} }}\n");
            if (i % 5 == 0)
            {
                sb.Append($"    options += [{{ id = extra_{i}  duration = {i % 90}min  resolution = deterministic }}]\n");
            }

            if (i % 10 == 0)
            {
                sb.Append("    on arrive(ctx) {\n        if (ctx.alert > 2) {\n            wait 30min\n            apply effect:alarm(ctx.place)\n        }\n    }\n");
            }

            sb.Append("}\n\n");
        }

        return sb.ToString();
    }

    /// <summary>The same module split into <paramref name="files"/> files of consecutive objects (the header in the first).</summary>
    public static List<(string Name, string Text)> GenerateFiles(int objects, int files, int seed = 1)
    {
        var text = Generate(objects, seed);
        var starts = new List<int>();
        var at = text.IndexOf("/// Synthetic actor ", StringComparison.Ordinal);
        while (at >= 0)
        {
            starts.Add(at);
            at = text.IndexOf("/// Synthetic actor ", at + 1, StringComparison.Ordinal);
        }

        var result = new List<(string, string)>();
        var perFile = (starts.Count + files - 1) / files;
        for (var f = 0; f < files; f++)
        {
            var first = f * perFile;
            if (first >= starts.Count)
            {
                break;
            }

            var from = f == 0 ? 0 : starts[first];
            var to = first + perFile < starts.Count ? starts[first + perFile] : text.Length;
            var body = text[from..to];
            result.Add(($"f{f:D3}.kedo", f == 0 ? body : "kedo 1\nmodule bench\n\n" + body));
        }

        return result;
    }
}
