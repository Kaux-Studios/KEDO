using System.Text;
using BenchmarkDotNet.Attributes;
using Kedo.Compiler.Model;
using Kedo.Compiler.Resolution;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;

namespace Kedo.Benchmarks;

/// <summary>
/// Invariants and computed fields (ADR-0016 D9): the same typed content resolved against its schema without rules and
/// with two computed fields and three invariants per type. Budget: at most 115 % of <see cref="ResolveTyped"/>.
/// </summary>
[MemoryDiagnoser]
public class InvariantBenchmarks
{
    private const string Fields = """
                { name = hp  kind = int  min = 1  required = true }
                { name = level  kind = int  min = 1  required = true }
                { name = curve  kind = list  element = { kind = int }  required = true }
                { name = ruleset  kind = reference  target = ruleset  required = true }
        """;

    private const string Plain = "kedo 1\nmodule s\nkedo_type ruleset { fields = [{ name = cap  kind = int  required = true }] }\nkedo_type unit {\n    fields = [\n" + Fields + "\n        { name = max_hp  kind = int }\n        { name = total  kind = int }\n    ]\n}\n";

    private const string WithRules = "kedo 1\nmodule s\nkedo_type ruleset { fields = [{ name = cap  kind = int  required = true }] }\nkedo_type unit {\n    fields = [\n" + Fields + """

                { name = max_hp  kind = int  computed = (hp * level) }
                { name = total  kind = int  computed = (sum(curve)) }
            ]
            invariants = [
                { id = curve_up  check = (increasing(curve)) }
                { id = alive  check = (max_hp > 0) }
                { id = capped  check = (total <= ruleset.cap) }
            ]
        }
        """;

    [Params(10_000)]
    public int Objects { get; set; }

    private FileDeclarations _plainDecls = null!;
    private FileDeclarations _ruleDecls = null!;
    private SchemaRegistry _plain = null!;
    private SchemaRegistry _rules = null!;

    private static SchemaRegistry Load(string text)
    {
        var bag = new DiagnosticBag();
        var schema = SchemaLoader.Load(new[] { DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(text, "schema.kedo"), bag), bag) }, bag);
        if (bag.HasErrors)
        {
            throw new InvalidOperationException(string.Join("\n", bag.Items.Select(d => d.Message)));
        }

        return schema;
    }

    [GlobalSetup]
    public void Setup()
    {
        _plain = Load(Plain);
        _rules = Load(WithRules);
        var rules = new StringBuilder("kedo 1\nmodule m\n\nruleset main { cap = 1000 }\n\n");
        var plain = new StringBuilder(rules.ToString());
        for (var i = 0; i < Objects; i++)
        {
            int hp = 10 + i % 90, level = 1 + i % 9, c = i % 7;
            var unit = $"unit u{i} {{\n    hp = {hp}\n    level = {level}\n    curve = [{c}, {10 + c}, {20 + c}, {30 + c}]\n    ruleset = ruleset:main\n";
            rules.Append(unit).Append("}\n\n");

            // The plain schema gets the same values written by hand, so both catalogs hold the same fields.
            plain.Append(unit).Append($"    max_hp = {hp * level}\n    total = {60 + 4 * c}\n}}\n\n");
        }

        var bag = new DiagnosticBag();
        _ruleDecls = DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(rules.ToString(), "units.kedo"), bag), bag);
        _plainDecls = DeclarationBuilder.Build(Parser.Parse(SourceText.FromString(plain.ToString(), "units.kedo"), bag), bag);
        var check = new DiagnosticBag();
        Resolver.Resolve(new[] { _ruleDecls }, _rules, check);
        if (bag.HasErrors || check.HasErrors)
        {
            throw new InvalidOperationException(string.Join("\n", bag.Items.Concat(check.Items).Select(d => d.Message)));
        }
    }

    [Benchmark(Baseline = true)]
    public int ResolveTyped() => Resolver.Resolve(new[] { _plainDecls }, _plain, new DiagnosticBag()).Objects.Count;

    [Benchmark]
    public int ResolveWithInvariants() => Resolver.Resolve(new[] { _ruleDecls }, _rules, new DiagnosticBag()).Objects.Count;
}
