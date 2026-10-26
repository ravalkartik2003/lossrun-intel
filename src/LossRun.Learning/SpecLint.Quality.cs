using System.Text.RegularExpressions;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;

namespace LossRun.Learning;

/// <summary>
/// Brief 20: learn-loop repairs found on the corpus v4 c7/c8 learns and the real-sample run 5, without a model call. Each
/// is layout-generic: it reads the spec, the graph and the glossary, never a carrier identity.
/// </summary>
public static partial class SpecLint
{
    private static readonly HashSet<string> CarrierConstantDocumentFields = new(StringComparer.Ordinal) { "document.carrierName", "document.lineOfBusiness" };

    /// <summary>
    /// Document fields on a PDF spec. The repairs:
    /// <list type="bullet">
    /// <item>The prompt's markup is removed from a regex: a markdown heading mark ("^#\s*") or a paragraph-role label
    /// ("PageFooter:"). The model copied these from the layout markdown and the paragraph list; they are not printed.</item>
    /// <item>A capture that is this document's literal value ("(?&lt;value&gt;Kappa Foods \(synthetic\))") is generalized
    /// when the generalized pattern reads the same value here, so the next document of the layout is read too. Carrier name
    /// and line of business are constant per format and keep their literal.</item>
    /// <item>An optional field (not required by the learn gate) whose locator still finds nothing that normalizes on the
    /// learning document is removed, so a field the run does not print does not block the gate.</item>
    /// </list>
    /// Each repair is kept only when the replay on the learning document confirms it. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> RepairDocumentFields(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        var notes = new List<string>();
        if (spec.DocumentFields.Count == 0)
        {
            return notes;
        }

        var required = LearnGate.Default.RequiredFields.Where(f => f.StartsWith("document.", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var replay = ReplayEngine.Execute(graph, spec, glossary: glossary);
        foreach (var field in spec.DocumentFields.Where(f => f.Locator.Type != "const" && f.Target.StartsWith("document.", StringComparison.Ordinal)).ToList())
        {
            var path = field.Target["document.".Length..];
            bool Fails(ReplayResult r) => r.Issues.Any(i => i.StartsWith($"document field {path}:", StringComparison.Ordinal) || i.StartsWith($"{path}: value", StringComparison.Ordinal));
            object? Value(ReplayResult r) => r.Document.Lineage is { } all && all.TryGetValue(path, out var lineage) ? lineage.SourceText ?? "" : null;
            bool Try(string candidate, Func<ReplayResult, bool> accept)
            {
                if (candidate == field.Locator.Pattern || !Compiles(candidate))
                {
                    return false;
                }

                var before = field.Locator.Pattern;
                field.Locator.Pattern = candidate;
                var trial = ReplayEngine.Execute(graph, spec, glossary: glossary);
                if (accept(trial))
                {
                    replay = trial;
                    return true;
                }

                field.Locator.Pattern = before;
                return false;
            }

            if (field.Locator.Type is "regex" or "regexPresent" && field.Locator.Pattern is { Length: > 0 } pattern)
            {
                var stripped = StripPromptMarkup(pattern);
                if (Fails(replay) && Try(stripped, r => !Fails(r)))
                {
                    notes.Add($"documentFields {field.Target}: prompt markup removed from the regex ({pattern} → {stripped})");
                }

                if (field.Locator.Type == "regex" && !Fails(replay))
                {
                    var printed = Value(replay);
                    var merged = field.Locator.Pattern!;
                    foreach (var candidate in NearestLabelAnchors(merged))
                    {
                        if (Try(candidate, r => !Fails(r) && Equals(Value(r), printed)))
                        {
                            notes.Add($"documentFields {field.Target}: anchored on its printed label ({merged} → {candidate})");
                            break;
                        }
                    }
                }

                if (field.Locator.Type == "regex" && !Fails(replay) && !CarrierConstantDocumentFields.Contains(field.Target)
                    && GeneralizeLiteralValue(field.Locator.Pattern!, field.Normalizer.Type) is { } general)
                {
                    var printed = Value(replay);
                    var literal = field.Locator.Pattern!;
                    if (Try(general, r => !Fails(r) && Equals(Value(r), printed)))
                    {
                        notes.Add($"documentFields {field.Target}: the literal capture is generalized ({literal} → {general})");
                    }
                }
            }

            if (Fails(replay) && !required.Contains(field.Target))
            {
                spec.DocumentFields.Remove(field);
                replay = ReplayEngine.Execute(graph, spec, glossary: glossary);
                notes.Add($"documentFields {field.Target}: removed (optional; its {field.Locator.Type} locator reads nothing that normalizes on this document)");
            }
        }

        return notes;
    }

    /// <summary>
    /// Corpus v4 c8: the model mapped a stacked cell's status part twice (claim.status and its printed companion
    /// claim.rawStatus) and, told each part maps once, moved rawStatus to a part the cell does not have. rawStatus is put
    /// back on the status part. A component listed absent that a column maps is not absent. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> RepairStackedParts(CarrierFormat spec)
    {
        var notes = new List<string>();
        foreach (var raw in spec.ColumnMap.Where(c => c.Target == "claim.rawStatus" && c.Part is not null))
        {
            var status = spec.ColumnMap.FirstOrDefault(c => c.Target == "claim.status" && c.Part is not null
                && CanonicalComparer.NormalizeText(c.Source) == CanonicalComparer.NormalizeText(raw.Source));
            if (status is not null && (raw.Part != status.Part || raw.PartPattern != status.PartPattern))
            {
                notes.Add($"columnMap \"{raw.Source}\" claim.rawStatus: reads the status part {status.Part} (was part {raw.Part})");
                (raw.Part, raw.PartPattern) = (status.Part, status.PartPattern);
            }
        }

        foreach (var component in spec.AbsentComponents.ToList())
        {
            if (spec.ColumnMap.Any(c => c.Target.StartsWith($"claim.{component}.", StringComparison.Ordinal)))
            {
                spec.AbsentComponents.Remove(component);
                notes.Add($"absentComponents {component} removed: a column maps claim.{component}.*");
            }
        }

        return notes;
    }

    /// <summary>
    /// A regex that anchors on text printed before the value's own label ("^Workers' Compensation Loss Run Insured:\s*(?&lt;value&gt;…)",
    /// copied from a paragraph CU merged) matches only documents CU merged the same way. Candidates anchored on the last one,
    /// two or three words of the label before the value ("^Insured:\s*(?&lt;value&gt;…)"), shortest first.
    /// </summary>
    internal static IEnumerable<string> NearestLabelAnchors(string pattern)
    {
        var at = pattern.IndexOf("(?<value>", StringComparison.Ordinal);
        var head = PatternHead().Match(pattern).Value;
        if (at <= head.Length || !head.EndsWith('^'))
        {
            yield break;
        }

        var before = pattern[head.Length..at];
        var label = LabelBeforeValue().Match(before);
        if (!label.Success)
        {
            yield break;
        }

        var words = label.Groups["words"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var n = 1; n <= Math.Min(3, words.Length); n++)
        {
            var anchored = string.Join(' ', words[^n..]) + label.Groups["rest"].Value;
            if (anchored.Length < before.Length)
            {
                yield return head + anchored + pattern[at..];
            }
        }
    }

    /// <summary>One printed value written as a regex: no syntax beyond escaped characters and whitespace tokens ("Eta Printing\s*\(synthetic\)").</summary>
    private static bool IsLiteral(string body) =>
        WhitespaceToken().Replace(body, " ").Trim() is { Length: > 0 } text && !RegexSyntax().IsMatch(text);

    /// <summary>
    /// A spreadsheet prints a document field's label and its value in two cells ("Loss experience valued" | 2024-11-10; corpus v4
    /// c4 learns, brief 20). A regex over the label cell cannot read the value, so when a field's locator reads nothing that
    /// normalizes, and a cell outside the tables prints its label (a glossary alias of the field, or the literal label the
    /// regex starts with) with a value in the next cell to the right, the field is located by <c>labelRight</c>. Kept only
    /// when the replay reads it. No model call.
    /// </summary>
    internal static IReadOnlyList<string> LocateLabelRightValues(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        var notes = new List<string>();
        if (spec.FileKind != "spreadsheet")
        {
            return notes;
        }

        var replay = ReplayEngine.Execute(graph, spec, glossary: glossary);
        // One label cell locates one field (a model that pointed two fields at the same label is not repaired into reading one value twice).
        var used = spec.DocumentFields.Where(f => f.Locator is { Type: "labelRight", Label: { } l }).Select(f => CanonicalComparer.NormalizeText(f.Locator.Label!)).ToHashSet(StringComparer.Ordinal);
        foreach (var field in spec.DocumentFields.Where(f => f.Locator.Type is "regex" or "regexPresent" or "cell" && f.Target.StartsWith("document.", StringComparison.Ordinal))
                     .OrderBy(f => LearnGate.Default.RequiredFields.Contains(f.Target) ? 0 : 1).ToList())
        {
            var path = field.Target["document.".Length..];
            if (!replay.Issues.Any(i => i.StartsWith($"document field {path}:", StringComparison.Ordinal) || i.StartsWith($"{path}: value", StringComparison.Ordinal)))
            {
                continue;
            }

            var labels = (glossary.Fields.TryGetValue(field.Target, out var entry) ? entry.Aliases.Select(a => a.Header) : [])
                .Append(field.Locator.Pattern is { } pattern ? LiteralLabel(pattern) : null).OfType<string>()
                .Select(CanonicalComparer.NormalizeText).Where(l => l.Length >= 3).ToHashSet(StringComparer.Ordinal);
            foreach (var label in graph.Cells.Where(c => c.TableId is null && labels.Contains(CanonicalComparer.NormalizeText(c.RawValue.TrimEnd(':', ' ')))
                         && !used.Contains(CanonicalComparer.NormalizeText(c.RawValue.Trim()))))
            {
                var before = (field.Locator, field.Normalizer, field.Reason);
                field.Locator = new FieldLocator { Type = "labelRight", Sheet = label.Sheet, Label = label.RawValue.Trim() };
                if (field.Normalizer.Type == "text" && DateTarget().IsMatch(field.Target))
                {
                    field.Normalizer = new NormalizerSpec { Type = "date" };
                }

                var trial = ReplayEngine.Execute(graph, spec, glossary: glossary);
                if (!trial.Issues.Any(i => i.StartsWith($"document field {path}:", StringComparison.Ordinal) || i.StartsWith($"{path}: value", StringComparison.Ordinal)))
                {
                    field.Reason = $"autofix: \"{label.RawValue.Trim()}\" and its value are printed in two cells; located by labelRight";
                    notes.Add($"{field.Target}: labelRight \"{label.RawValue.Trim()}\" on {label.Sheet}");
                    used.Add(CanonicalComparer.NormalizeText(label.RawValue.Trim()));
                    replay = trial;
                    break;
                }

                (field.Locator, field.Normalizer, field.Reason) = before;
            }
        }

        return notes;
    }

    /// <summary>The literal text a pattern starts with, before its first regex construct ("(?i)^Loss experience valued(?:…" → "Loss experience valued").</summary>
    private static string? LiteralLabel(string pattern)
    {
        var body = pattern[PatternHead().Match(pattern).Value.Length..];
        var end = LiteralPrefix().Match(body);
        return end.Success && end.Value.Trim().Length > 0 ? Regex.Unescape(end.Value).Trim().TrimEnd(':') : null;
    }

    [GeneratedRegex(@"^(?:[^\\()\[\]{}|.*+?^$]|\\[ :.\-'&/#])+")]
    private static partial Regex LiteralPrefix();

    /// <summary>
    /// ADR 0018 records rows mode: a line-n entry reads the column it is printed under, which the composed header names
    /// ("Loss State: Ind Paid:" = "Ind Paid:" under "Loss State:"). Corpus v4 c6 (brief 20): five attempts put every line-1
    /// amount under the line-0 column to its left, so indemnity paid was read from an empty cell. Each line-n entry is set
    /// under the line-0 source its composed header names, when exactly one does. No model call.
    /// </summary>
    internal static IReadOnlyList<string> RepairRecordUnder(CarrierFormat spec, DocumentGraph graph)
    {
        var notes = new List<string>();
        if (spec.TableLocator.Records is not { Mode: "rows" })
        {
            return notes;
        }

        var lineZero = spec.ColumnMap.Where(c => c.Line == 0).Select(c => c.Source).Distinct().ToList();
        var composed = graph.Tables.SelectMany(t => t.HeaderTexts).Select(CanonicalComparer.NormalizeText).Distinct().ToList();
        foreach (var column in spec.ColumnMap.Where(c => c.Line > 0))
        {
            var source = CanonicalComparer.NormalizeText(column.Source);
            var under = lineZero.Where(u => composed.Contains($"{CanonicalComparer.NormalizeText(u)} {source}")).ToList();
            if (under.Count == 1 && CanonicalComparer.NormalizeText(column.Under ?? "") != CanonicalComparer.NormalizeText(under[0]))
            {
                notes.Add($"columnMap \"{column.Source}\" line {column.Line}: under \"{under[0]}\", as its composed header prints it (was \"{column.Under}\")");
                column.Under = under[0];
            }
        }

        return notes;
    }

    /// <summary>
    /// Brief 20 Answers 4 (the c7-000 five-trial study): a printed zero is a value, not an absent one. A spec that lists a zero
    /// ("0.00", "$0", "(0.00)") in <c>nullWhen</c> on an amount column turns every zero cell into null, so derived incurred
    /// values and the totals no longer tie (4 of 5 c7-000 learns failed this way). Zeros are removed from amount columns'
    /// <c>nullWhen</c>; dashes and blanks stay. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> KeepPrintedZeros(CarrierFormat spec)
    {
        var notes = new List<string>();
        foreach (var column in spec.ColumnMap.Where(c => c.Normalizer.Type == "currency" || MoneyTarget().IsMatch(c.Target)))
        {
            var zeros = column.NullWhen.Where(v => PrintedZero().IsMatch(v.Trim())).ToList();
            if (zeros.Count > 0)
            {
                column.NullWhen.RemoveAll(zeros.Contains);
                notes.Add($"columnMap {column.Target}: nullWhen {string.Join(", ", zeros.Select(z => $"\"{z}\""))} removed (a printed zero is 0, not absent)");
            }
        }

        return notes;
    }

    /// <summary>
    /// Brief 20 Answers 2 (c7-000 five-trial study, mistake 2): <c>components</c> declares what a total column sums, and ADR 0018
    /// sets it only when the header names its components ("Total Incurred (Ind+Med)"). Declared on a plain total ("Total
    /// Incurred"), it made the document indemnity + medical and R-29 failed on every claim with expense. Components are
    /// dropped from a column whose header names none. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> DropUnnamedComponents(CarrierFormat spec, Glossary glossary)
    {
        var notes = new List<string>();
        foreach (var column in spec.ColumnMap.Where(c => c.Components is { Count: > 0 }))
        {
            if (glossary.ResolveHeader(column.Source) is not { Method: "component-sum" })
            {
                notes.Add($"columnMap \"{column.Source}\" {column.Target}: components {string.Join(" + ", column.Components!)} dropped (the header names no components)");
                column.Components = null;
            }
        }

        return notes;
    }

    /// <summary>
    /// Brief 22 Answers 1 (corpus v5 c10 learns): in records mode <c>measures</c> a component column is read once per measure
    /// row, so it maps to the component without a measure. The model wrote one entry per measure (<c>claim.total.paid</c>,
    /// <c>.reserve</c>, <c>.incurred</c> from "Total"), each reading the key row, or a single measure ("Total" → total
    /// incurred) beside measure-less siblings; R-02 failed on every claim. Every money entry of a component whose source is
    /// one printed column becomes the one measure-less entry. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> RepairMeasureColumns(CarrierFormat spec)
    {
        var notes = new List<string>();
        if (spec.TableLocator.Records is not { Mode: "measures" })
        {
            return notes;
        }

        foreach (var component in ReplayEngine.MeasureComponents)
        {
            var entries = spec.ColumnMap.Where(c => c.Target.StartsWith(component + ".", StringComparison.Ordinal) && c.Target[(component.Length + 1)..] is "paid" or "reserve" or "incurred").ToList();
            var sources = entries.Select(c => CanonicalComparer.NormalizeText(c.Source)).Distinct(StringComparer.Ordinal).ToList();
            if (entries.Count == 0 || sources.Count != 1 || spec.ColumnMap.Any(c => c.Target == component && CanonicalComparer.NormalizeText(c.Source) != sources[0]))
            {
                continue;
            }

            var index = spec.ColumnMap.IndexOf(entries[0]);
            spec.ColumnMap.RemoveAll(entries.Contains);
            if (!spec.ColumnMap.Any(c => c.Target == component))
            {
                spec.ColumnMap.Insert(Math.Min(index, spec.ColumnMap.Count), new ColumnMapping
                {
                    Source = entries[0].Source, Target = component, Normalizer = entries[0].Normalizer, NullWhen = entries[0].NullWhen, Components = entries[0].Components,
                });
            }

            notes.Add($"columnMap \"{entries[0].Source}\": {string.Join(", ", entries.Select(e => e.Target))} → {component} (records mode measures reads each measure row)");
        }

        return notes;
    }

    /// <summary>
    /// Brief 23 §1: a document field the spec does not read (no locator, or one that finds nothing), whose label and value the
    /// header block prints (<see cref="HeaderFields"/>, found at ingestion), is located there with <c>headerLabel</c>. A
    /// single-period run's policy number and dates printed in the header fill its period the same way. A field the spec
    /// already reads is never touched. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> LocateHeaderFields(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        var notes = new List<string>();
        var header = graph.HeaderFields ?? HeaderFields.Detect(graph, glossary);
        if (header.Count == 0)
        {
            return notes;
        }

        var replay = ReplayEngine.Execute(graph, spec, glossary: glossary);
        var periods = replay.Document.PolicyPeriods;
        foreach (var found in header.Where(h => h.Field != HeaderFields.LossWindow))
        {
            var period = found.Field.StartsWith("period.", StringComparison.Ordinal);
            var path = found.Field[(found.Field.IndexOf('.') + 1)..];
            var read = period
                ? periods.Count != 1 || replay.Document.Lineage?.ContainsKey($"policyPeriods[0].{path}") == true
                : replay.Document.Lineage?.ContainsKey(path) == true;
            if (read || found.Field == "document.carrierName" && spec.DocumentFields.Any(f => f.Target == found.Field && f.Locator.Type == "const"))
            {
                continue;
            }

            var type = HeaderFields.Kinds[found.Field] is "date" or "window" ? "date" : "text";
            var field = spec.DocumentFields.FirstOrDefault(f => f.Target == found.Field);
            if (field is null)
            {
                spec.DocumentFields.Add(field = new DocumentFieldSpec { Target = found.Field });
            }

            field.Locator = new FieldLocator { Type = "headerLabel", Label = found.Label };
            field.Normalizer = new NormalizerSpec { Type = type };
            notes.Add($"documentFields {found.Field}: located in the header block by its label \"{found.Label}\" (headerLabel)");
        }

        return notes;
    }

    [GeneratedRegex(@"^\(?-?\$?\s*0+(?:[.,]0+)?\)?$")]
    private static partial Regex PrintedZero();

    /// <summary>The pattern without the prompt's markup after its flags and start anchor ("(?m)^# Title" → "(?m)^Title").</summary>
    internal static string StripPromptMarkup(string pattern)
    {
        var head = PatternHead().Match(pattern).Value;
        var rest = pattern[head.Length..];
        string previous;
        do
        {
            previous = rest;
            rest = MarkdownHeading().Replace(rest, "");
            rest = RoleLabel().Replace(rest, "");
        }
        while (rest != previous);

        return head + rest;
    }

    /// <summary>
    /// A value group whose body is a literal (no class, quantifier or alternation) is replaced by a shape: a date for a date
    /// normalizer, else a lazy run to what follows the group (greedy to the line end when nothing follows). Null when the
    /// capture is already a pattern.
    /// </summary>
    internal static string? GeneralizeLiteralValue(string pattern, string normalizerType)
    {
        var group = ValueGroup().Match(pattern);
        if (!group.Success || !IsLiteral(group.Groups["body"].Value))
        {
            return null;
        }

        var tail = pattern[(group.Index + group.Length)..];
        var shape = normalizerType == "date" ? @"\d{1,2}[/.-]\d{1,2}[/.-]\d{2,4}" : tail.Trim().Length == 0 ? ".+" : ".+?";
        return pattern[..group.Index] + "(?<value>" + shape + ")" + tail;
    }

    /// <summary>
    /// A normalizer that cannot produce the canonical field's type (corpus v4 c7: "Medical Only" read as text into the claim
    /// type enum; real samples: dates read as text) is replaced by the one the target needs: an enum for a glossary field
    /// with value aliases, a date for a date field, currency for an amount. Column map, line fields and document fields. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> FitNormalizers(CarrierFormat spec, Glossary glossary)
    {
        var notes = new List<string>();
        void Fit(string where, string target, NormalizerSpec normalizer, Action<NormalizerSpec> set)
        {
            if (normalizer.Type != "text")
            {
                return;
            }

            var fitted = glossary.Fields.TryGetValue(target, out var field) && field.ValueAliases is { Count: > 0 } ? new NormalizerSpec { Type = "enum", GlossaryField = target }
                : DateTarget().IsMatch(target) ? new NormalizerSpec { Type = "date" }
                : MoneyTarget().IsMatch(target) ? new NormalizerSpec { Type = "currency" }
                : null;
            if (fitted is not null)
            {
                set(fitted);
                notes.Add($"{where} {target}: normalizer text → {fitted.Type}");
            }
        }

        foreach (var column in spec.ColumnMap)
        {
            Fit("columnMap", column.Target, column.Normalizer, n => column.Normalizer = n);
        }

        foreach (var line in spec.TableLocator.Records?.LineFields ?? [])
        {
            Fit("lineFields", line.Target, line.Normalizer, n => line.Normalizer = n);
        }

        foreach (var field in spec.DocumentFields.Where(f => f.Locator.Type is not ("const" or "regexPresent")))
        {
            Fit("documentFields", field.Target, field.Normalizer, n => field.Normalizer = n);
        }

        return notes;
    }

    /// <summary>
    /// A word boundary written right after a literal punctuation mark ("^Policy Total:\b") never matches before a space, so
    /// the total row was read as a claim row (corpus v4 c7: two phantom claims per period). The boundary is removed from
    /// row-kind, counts, anchor, line-field and document-field patterns. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> FixPunctuationBoundaries(CarrierFormat spec)
    {
        var notes = new List<string>();
        void Fix(string where, string? pattern, Action<string> set)
        {
            if (pattern is null || BoundaryAfterPunctuation().Replace(pattern, "${p}") is not { } fixedPattern || fixedPattern == pattern || !Compiles(fixedPattern))
            {
                return;
            }

            set(fixedPattern);
            notes.Add($"{where}: word boundary after punctuation removed ({pattern} → {fixedPattern})");
        }

        foreach (var kind in spec.TableLocator.RowKinds)
        {
            Fix($"rowKinds {kind.Kind}.regex", kind.Regex, v => kind.Regex = v);
            Fix($"rowKinds {kind.Kind}.countsRegex", kind.CountsRegex, v => kind.CountsRegex = v);
        }

        Fix("tableLocator.blockAnchor", spec.TableLocator.BlockAnchor, v => spec.TableLocator.BlockAnchor = v);
        foreach (var line in spec.TableLocator.Records?.LineFields ?? [])
        {
            Fix($"lineFields {line.Target}", line.Pattern, v => line.Pattern = v);
        }

        foreach (var field in spec.DocumentFields.Where(f => f.Locator.Pattern is not null))
        {
            Fix($"documentFields {field.Target}", field.Locator.Pattern, v => field.Locator.Pattern = v);
        }

        return notes;
    }

    /// <summary>
    /// Brief 20 §2: a claim-number line field must be a pattern validated on the printed lines, not one claim number. The
    /// lint names a literal pattern, and for records it matched on no line it shows those records' printed lines as replay
    /// reads them (the model sees CU's paragraphs; replay reads lines rebuilt from the words, with parts split at wide gaps).
    /// </summary>
    internal static IEnumerable<string> ClaimNumberLineFieldIssues(CarrierFormat spec, ReplayResult replay)
    {
        if (spec.TableLocator.Records is not { Mode: "lines" } records || records.LineFields.FirstOrDefault(l => l.Target == "claim.claimNumber") is not { } field)
        {
            yield break;
        }

        var body = ValueGroup().Match(field.Pattern) is { Success: true } g ? g.Groups["body"].Value : AnchorsOnly().Replace(field.Pattern, "");
        if (IsLiteral(body))
        {
            yield return $"lint: lineFields claim.claimNumber pattern {field.Pattern} is one printed claim number, not a pattern: write the claim-number shape (letters, digit runs, separators) so it matches every claim's line";
        }

        if (replay.UnmatchedClaimNumberRecords.Count > 0)
        {
            var claims = replay.Document.PolicyPeriods.Sum(p => p.Claims.Count);
            var unmatched = replay.Document.PolicyPeriods.Sum(p => p.Claims.Count(c => c.ClaimNumber is null));
            yield return $"lint: lineFields claim.claimNumber pattern {field.Pattern} matched no line of {unmatched} of {claims} claim record(s). Their printed lines as replay reads them "
                + $"(\" / \" separates lines, \" | \" separates values printed apart on one line; the pattern is tried on each line and each part): {string.Join("; ", replay.UnmatchedClaimNumberRecords)}";
        }
    }

    /// <summary>The label just before the value group: words, then a colon and optional whitespace tokens.</summary>
    [GeneratedRegex(@"(?<words>(?:[A-Za-z][\w'#/&.]*[ ]?)+?)(?<rest>[ ]?:(?:\\s[*+?]?|[ ])*)$")]
    private static partial Regex LabelBeforeValue();

    [GeneratedRegex(@"^(?:\(\?[a-zA-Z]+\))?\^?")]
    private static partial Regex PatternHead();

    [GeneratedRegex(@"^(?:\\?#)+(?:\\s[*+?]?|[ ]+)?")]
    private static partial Regex MarkdownHeading();

    [GeneratedRegex(@"^(?i:page\s?(?:header|footer|number)|section\s?heading|title)[ ]*[:=][ ]*(?:\\s[*+?]?)?")]
    private static partial Regex RoleLabel();

    [GeneratedRegex(@"\(\?<value>(?<body>(?:[^()\\]|\\.)*)\)")]
    private static partial Regex ValueGroup();

    /// <summary>Regex syntax in a capture body: a class shorthand, a character class, a quantifier, alternation or a wildcard.</summary>
    [GeneratedRegex(@"(?<!\\)[.*+?\[\]{}|]|\\[dDwWsSpP]")]
    private static partial Regex RegexSyntax();

    [GeneratedRegex(@"\\s[*+?]?|[ ]+")]
    private static partial Regex WhitespaceToken();

    [GeneratedRegex(@"^\(\?[a-zA-Z]+\)|^\^|\$$")]
    private static partial Regex AnchorsOnly();

    [GeneratedRegex(@"(?<p>(?<!\\)[:;,#%]|\\[.:)\]\-])\\b")]
    private static partial Regex BoundaryAfterPunctuation();

    [GeneratedRegex(@"(?:^|\.)(?:date\w*|\w*Date|lastDayWorked)$")]
    private static partial Regex DateTarget();

    [GeneratedRegex(@"\.(?:paid|reserve|incurred|recovered|outstanding)$")]
    private static partial Regex MoneyTarget();
}
