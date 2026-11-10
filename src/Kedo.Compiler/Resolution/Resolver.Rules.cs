using System.Globalization;
using Kedo.Compiler.Model;
using Kedo.Compiler.Schema;
using Kedo.Compiler.Syntax;
using Kedo.Diagnostics;
using Kedo.Schema;

namespace Kedo.Compiler.Resolution;

// Invariants and computed fields of the schema (ADR-0016): the pass that computes fields after resolution and before
// stage B and the hash, the invariants of stage B (K4008, K4016) and the catalog invariants.
public sealed partial class Resolver
{
    private SchemaEvalContext? _eval;
    private Dictionary<ResolvedObject, int>? _computing;
    private Func<ResolvedObject, bool>? _prepare;
    private int _computeDepth;
    private readonly HashSet<string> _computeFailed = new(StringComparer.Ordinal);

    private SchemaEvalContext EvalFor(ResolvedCatalog catalog)
        => _eval is { } e && ReferenceEquals(e.Catalog, catalog) ? e : _eval = new SchemaEvalContext(catalog);

    /// <summary>A value written in a meta field of kind expression (<c>check = (…)</c>) is kept as syntax, not evaluated.</summary>
    private ResolvedValue? LowerExpression(Value value, FieldSchema fs, Provenance prov)
    {
        string source;
        var offset = value.Origin.Span.Start;
        switch (value)
        {
            case ExprValue ev:
            {
                var node = ev.Node;
                var t = ev.Tree;
                if (t[node].Kind == NodeKind.ParenExpr && t.Children(node).Length == 1)
                {
                    node = t.Children(node)[0];
                }

                var span = t.SpanOf(node);
                source = t.Source.GetText(span);
                offset = span.Start;
                break;
            }

            // A parenthesised name or literal arrives as that value (DeclarationBuilder): it is still an expression.
            case EnumValue en:
                source = en.Name;
                break;
            case BoolValue b:
                source = b.Flag ? "true" : "false";
                break;
            case IntValue i:
                source = i.Number.ToString(CultureInfo.InvariantCulture);
                break;
            case DecimalValue d:
                source = RDecimal.Format(d.Units, d.Scale);
                break;
            case DurationValue du:
                source = DurationSources.TextOf(du) ?? du.Amount.ToString(CultureInfo.InvariantCulture) + du.Unit;
                break;
            case RefValue r:
                source = r.ToString();
                break;
            case TextValue:
                _d.Error("K4001", $"`{fs.Name}` takes a schema expression without quotes: write `{fs.Name} = (…)`", value.Origin.File, value.Origin.Span);
                return null;
            default:
                _d.Error("K4001", $"`{fs.Name}` takes a schema expression in parentheses, found {Describe(value)}", value.Origin.File, value.Origin.Span);
                return null;
        }

        return new RExpression(prov, SchemaExpressionCompiler.Canonical(source)) { Source = source, Offset = offset };
    }

    // ----- computed fields -----

    /// <summary>Computes the fields of every concrete object (objects read through references first).</summary>
    private void ComputeFields(ResolvedCatalog catalog)
    {
        _computeFailed.Clear();
        if (!_rules.HasComputed)
        {
            return;
        }

        _computing = new Dictionary<ResolvedObject, int>(catalog.Objects.Count, ReferenceEqualityComparer.Instance);
        _prepare = o => EnsureComputed(o, catalog);
        foreach (var obj in catalog.Objects)
        {
            if (!obj.IsAbstract)
            {
                EnsureComputed(obj, catalog);
            }
        }

        _computing = null;
        _prepare = null;
    }

    private bool EnsureComputed(ResolvedObject obj, ResolvedCatalog catalog)
    {
        if (_computing is null || obj.IsAbstract)
        {
            return true;
        }

        if (_computing.TryGetValue(obj, out var state))
        {
            return state == 2;
        }

        _computing[obj] = 1;
        if (_rules.For(_schema.FindType(obj.Type)?.Body) is { Computed.Count: > 0 } body)
        {
            obj.Body = Compute(body, obj.Body, obj, catalog, isBody: true);
        }

        foreach (var kv in obj.Components)
        {
            if (_rules.For(_schema.FindComponent(kv.Key)) is { Computed.Count: > 0 } component && kv.Value is RRecord record)
            {
                obj.Components = obj.Components.With(kv.Key, Compute(component, record, obj, catalog, isBody: false));
            }
        }

        _computing[obj] = 2;
        return true;
    }

    private RRecord Compute(RecordRules rules, RRecord record, ResolvedObject obj, ResolvedCatalog catalog, bool isBody)
    {
        // Computed fields go last, in dependency order, whatever an ancestor's body had (incremental = full).
        var fields = record.Fields;
        foreach (var c in rules.Computed)
        {
            if (fields.TryGetValue(c.Field.Name, out _))
            {
                fields = fields.Without(c.Field.Name);
            }
        }

        if (!ReferenceEquals(fields, record.Fields))
        {
            record = record with { Fields = fields };
        }

        // Reading another object computes its fields first, in the middle of this evaluation: that one gets its own context.
        var ctx = _computeDepth == 0 ? EvalFor(catalog) : new SchemaEvalContext(catalog);
        _computeDepth++;
        try
        {
            return ComputeWith(ctx, rules, record, obj, isBody);
        }
        finally
        {
            _computeDepth--;
        }
    }

    private RRecord ComputeWith(SchemaEvalContext ctx, RecordRules rules, RRecord record, ResolvedObject obj, bool isBody)
    {
        if (isBody)
        {
            obj.Body = record;
        }

        // When no computed field of the record reads another one, all read the same record and it is copied once.
        if (rules.Independent)
        {
            var fields = new OrderedFields.Builder(record.Fields);
            foreach (var c in rules.Computed)
            {
                if (ComputeOne(ctx, c, record, obj, isBody) is { } value)
                {
                    fields.Set(c.Field.Name, value);
                }
            }

            return record with { Fields = fields.ToFields() };
        }

        foreach (var c in rules.Computed)
        {
            if (isBody)
            {
                obj.Body = record;
            }

            if (ComputeOne(ctx, c, record, obj, isBody) is { } value)
            {
                record = record with { Fields = record.Fields.With(c.Field.Name, value) };
            }
        }

        return record;
    }

    private ResolvedValue? ComputeOne(SchemaEvalContext ctx, CompiledComputed c, RRecord record, ResolvedObject obj, bool isBody)
    {
        ctx.Reset(obj, isBody ? record : obj.Body, record);
        ctx.Prepare = _prepare;
        ctx.References = null;
        var v = ctx.Run(c.Expression);
        string? error = ctx.Failed ? ctx.Error : null;
        var value = ctx.Failed ? null : ToResolved(v, c, obj, out error);
        ctx.Prepare = null;
        if (value is null && error is not null)
        {
            _computeFailed.Add(obj.Key);
            var message = $"computed field `{c.Field.Name}` of `{obj.Key}` cannot be computed: {error}";
            if (_warned.Add($"K4016|{obj.Declaration.Origin.File}|{obj.Declaration.Origin.Span.Start}|{message}"))
            {
                _d.Error("K4016", message, obj.Declaration.Origin.File, obj.Declaration.Origin.Span);
            }
        }

        return value;
    }

    private static ResolvedValue? ToResolved(SValue v, CompiledComputed c, ResolvedObject obj, out string? error)
    {
        error = null;
        var f = c.Field;
        // One provenance per field, shared by every object: a computed value has no authored position (its object has).
        var prov = c.Provenance;
        switch (f.Kind, v.Kind)
        {
            case (ValueKind.Int, SKind.Int):
                return new RInt(prov, v.N);
            case (ValueKind.Decimal, SKind.Int or SKind.Decimal):
            {
                var scale = v.Kind == SKind.Int ? 0 : v.Scale;
                var units = v.N;
                if (scale > f.Scale)
                {
                    for (var i = f.Scale; i < scale; i++)
                    {
                        if (units % 10 != 0)
                        {
                            error = $"`{RDecimal.Format(v.N, scale)}` has more decimal places than the {f.Scale} of the field";
                            return null;
                        }

                        units /= 10;
                    }

                    return new RDecimal(prov, units, f.Scale);
                }

                try
                {
                    return new RDecimal(prov, BinaryNode.Rescale(units, scale, f.Scale), f.Scale);
                }
                catch (OverflowException)
                {
                    error = "the decimal does not fit in 64 bits";
                    return null;
                }
            }

            case (ValueKind.Duration, SKind.Duration):
            {
                var per = DurationUnits.MillisecondsPer(obj.TimeUnit);
                if (v.N % per != 0)
                {
                    error = $"`{DurationLowering.FormatMs(v.N)}` is not a whole number of `{DurationUnits.Suffix(obj.TimeUnit)}` (the module's base unit)";
                    return null;
                }

                return new RDuration(prov, v.N / per) { Unit = obj.TimeUnit };
            }

            case (ValueKind.Bool, SKind.Bool):
                return new RBool(prov, v.Flag);
            case (ValueKind.Text, SKind.Text):
                return new RText(prov, (string)v.O!);
            case (ValueKind.Enum, SKind.Enum):
                return new REnum(prov, (string)v.O!);
            case (ValueKind.Reference or ValueKind.Asset, SKind.Ref):
            {
                var r = (RRef)v.O!;
                return new RRef(prov, r.Type, r.Module, r.Id);
            }

            default:
                error = $"the expression gives {FieldNode.Describe(v.Kind)}, not {f.Kind.ToString().ToLowerInvariant()}";
                return null;
        }
    }

    /// <summary>Incremental: a text of each object's computed values; the keys whose computed values changed since <paramref name="previous"/>.</summary>
    private HashSet<string> ComputedChanges(IEnumerable<ResolvedObject> objects, IncrementalState? previous, IncrementalState state)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        if (!_rules.HasComputed)
        {
            return changed;
        }

        foreach (var obj in objects)
        {
            if (obj.IsAbstract)
            {
                continue;
            }

            var sb = new System.Text.StringBuilder();
            void Append(RecordRules? rules, RRecord? record)
            {
                foreach (var c in rules?.Computed ?? new List<CompiledComputed>())
                {
                    sb.Append(record is not null && record.Fields.TryGetValue(c.Field.Name, out var v) ? v.CanonicalText : "-").Append('\u0001');
                }
            }

            Append(_rules.For(_schema.FindType(obj.Type)?.Body), obj.Body);
            foreach (var kv in obj.Components)
            {
                Append(_rules.For(_schema.FindComponent(kv.Key)), kv.Value as RRecord);
            }

            if (sb.Length == 0)
            {
                continue;
            }

            var text = sb.ToString();
            state.Computed[obj.Key] = text;
            if (previous is null || !previous.Computed.TryGetValue(obj.Key, out var was) || was != text)
            {
                changed.Add(obj.Key);
            }
        }

        return changed;
    }

    // ----- invariants -----

    /// <summary>The invariants of the record schema of <paramref name="record"/> (the object body, a component or a nested record).</summary>
    private void CheckInvariants(RecordRules rules, RRecord record, ResolvedObject obj, ResolvedCatalog catalog)
    {
        var ctx = EvalFor(catalog);
        var where = ReferenceEquals(record, obj.Body) ? "" : $" in `{_path}`";
        foreach (var inv in rules.Invariants)
        {
            ctx.Reset(obj, obj.Body, record);
            ctx.References = _references;
            var v = ctx.Run(inv.Check);
            ctx.References = null;
            _objectFailed |= Judge(inv, inv.Check, v, ctx, obj, record, where);
        }
    }

    /// <summary>Catalog invariants: <c>forall(type, condition)</c> object by object, each failure reported on its object.</summary>
    private void CheckCatalogInvariants(ResolvedCatalog catalog)
    {
        if (_rules.Catalog.Count == 0)
        {
            return;
        }

        var ctx = EvalFor(catalog);
        foreach (var inv in _rules.Catalog)
        {
            var forall = inv.Forall!;
            foreach (var obj in ctx.OfType(forall.TypeId))
            {
                ctx.Reset(obj, obj.Body, obj.Body);
                var v = ctx.Run(forall.Body);
                if (Judge(inv, forall.Body, v, ctx, obj, obj.Body, ""))
                {
                    obj.HasErrors = true;
                }
            }
        }
    }

    /// <summary>Reports a failed or unevaluable check; true when that is an error for the object.</summary>
    private bool Judge(CompiledInvariant inv, SNode check, SValue v, SchemaEvalContext ctx, ResolvedObject obj, RRecord scope, string where)
    {
        if (!ctx.Failed && v.Kind == SKind.Bool && v.Flag)
        {
            return false;
        }

        var id = inv.Schema.Id;
        if (ctx.Failed || v.Kind != SKind.Bool)
        {
            var why = ctx.Failed ? ctx.Error : $"the check gives {FieldNode.Describe(v.Kind)}, not a boolean";
            if (why is null)
            {
                return false;
            }

            var unevaluable = $"invariant `{id}` cannot be checked{where}: {why}";
            if (_warned.Add($"K4016|{obj.Declaration.Origin.File}|{obj.Declaration.Origin.Span.Start}|{unevaluable}"))
            {
                _d.Error("K4016", unevaluable, obj.Declaration.Origin.File, obj.Declaration.Origin.Span);
            }

            return true;
        }

        // Explain pass: the fields of the object the rule read give the position and the labels.
        var root = obj.Body;
        ctx.Reset(obj, root, scope);
        ctx.Reads = new List<(string Name, ResolvedValue Value)>();
        ctx.Run(check);
        var reads = ctx.Reads;
        ctx.Reads = null;

        string text;
        if (inv.Message.Length == 0)
        {
            text = $"`{(inv.Forall is { } fa ? fa.BodyText : inv.Schema.Check)}` is false";
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            foreach (var part in inv.Message)
            {
                if (part.Expr is null)
                {
                    sb.Append(part.Text);
                    continue;
                }

                ctx.Reset(obj, root, scope);
                var shown = ctx.Run(part.Expr);
                sb.Append(ctx.Failed ? "?" : shown.Format());
            }

            text = sb.ToString();
        }

        // The first field the rule read that content wrote (not a default nor a computed value) is where the error points.
        var first = reads.FindIndex(r => r.Value.Provenance.Layer is not (LayerKind.Default or LayerKind.Computed) && r.Value.Provenance.Origin.File is not null);
        var primary = first >= 0 ? reads[first].Value : null;
        var origin = primary is not null && primary.Provenance.Layer is not (LayerKind.Default or LayerKind.Computed) && primary.Provenance.Origin.File is not null
            ? primary.Provenance.Origin
            : obj.Declaration.Origin;
        var labels = new List<DiagnosticLabel>();
        if (primary is not null && primary.Provenance.Layer is not (LayerKind.Default or LayerKind.Computed) && primary.Provenance.LayerName != obj.Key)
        {
            labels.Add(new DiagnosticLabel(obj.Declaration.Origin.File, obj.Declaration.Origin.Span, $"inherited by `{obj.Key}` via {From(primary.Provenance, obj.Key)}"));
        }

        foreach (var (name, value) in reads.Where((_, i) => i != first))
        {
            var o = value.Provenance.Layer is LayerKind.Default or LayerKind.Computed ? obj.Declaration.Origin : value.Provenance.Origin;
            var shown = value.CanonicalText.Length > 60 ? value.CanonicalText[..57] + "..." : value.CanonicalText;
            labels.Add(new DiagnosticLabel(o.File, o.Span, $"`{name}` = {shown}"));
        }

        var message = $"invariant `{id}` fails{where}: {text}";
        var severity = inv.Schema.Severity;
        if (_warned.Add($"K4008|{origin.File}|{origin.Span.Start}|{message}"))
        {
            _d.Add(new Diagnostic("K4008", severity, message, origin.File, origin.Span, labels, Array.Empty<string>()));
        }

        return severity == Severity.Error;
    }
}
