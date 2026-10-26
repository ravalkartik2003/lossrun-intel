using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LossRun.Contracts;
using LossRun.Formats;
using LossRun.Formats.Replay;
using LossRun.Rules;

namespace LossRun.Learning;

/// <summary>
/// Deterministic checks on a candidate spec that the totals and rule checks cannot see (found on the first live learn
/// run, brief 06 report): targets the replay never reads, malformed derived-rule paths, constants that assert facts
/// the document does not print, an expense treatment that contradicts the column map, and document-field captures that
/// run into the next printed label. Each finding is a verification issue fed back to the model; none is auto-fixed.
/// </summary>
public static partial class SpecLint
{
    /// <summary>Document fields a spreadsheet's column map may fill from a column repeated on every row.</summary>
    public static readonly string[] RowLevelDocumentFields = ["document.valuationDate", "document.reportRunDate", "document.carrierName", "document.insuredName"];

    /// <summary>Document fields a format may declare as a constant (facts about the layout, not about the account).</summary>
    public static readonly string[] ConstAllowed = ["document.expenseTreatment", "document.carrierName", "document.lineOfBusiness", "document.noLossesStatement"];

    private static readonly Lazy<(HashSet<string> Claim, HashSet<string> Document)> Paths = new(BuildPaths);

    /// <summary>The canonical target a misplaced row-level column most likely meant (pilot run: claim.valuationDate, claim.policyNumber).</summary>
    private static string? Suggest(CarrierFormat spec, ColumnMapping column)
    {
        var leaf = column.Target[(column.Target.LastIndexOf('.') + 1)..];
        if (leaf is "policyNumber" or "effectiveDate" or "expirationDate")
        {
            return $"a policy value repeated on every row is period.{leaf}, with periods.source = \"column\" and periods.column = the policy number header";
        }

        if (leaf is "rawCarrier" or "carrier")
        {
            leaf = "carrierName";
        }

        var document = $"document.{leaf}";
        return RowLevelDocumentFields.Contains(document) && spec.FileKind == "spreadsheet"
            ? $"a document value repeated on every row maps to {document}"
            : null;
    }

    /// <summary>
    /// Brief 08b (learn-reliability study: the CSV carrier's failed learns mapped repeated columns to claim.valuationDate /
    /// claim.carrierName / claim.policyNumber in 6 of 16 failed trials despite the lint hint): on a spreadsheet, a column map
    /// entry whose target is not canonical but whose leaf is a row-level document or period value is retargeted to that
    /// value, without a model call. A policy-number column also makes periods come from that column. Returns what changed.
    /// </summary>
    /// <summary>
    /// Brief 08c (08b Answers 2, generalized to learned specs): CU drops separator punctuation when a total-row label spans
    /// cells ("Account Total - 57 Claims" read as "Account Total" | "57 Claims"; "GRAND TOTAL (50 claims" without its "(").
    /// On a PDF spec, a row-kind regex that requires a literal "-", "(" or ")" then misses the row, which is read as a claim
    /// row instead. Those separators are made optional in row-kind and counts regexes, without a model call, as long as the
    /// regex still compiles. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> RelaxRowKindPunctuation(CarrierFormat spec)
    {
        var notes = new List<string>();
        if (spec.FileKind != "pdf")
        {
            return notes;
        }

        foreach (var kind in spec.TableLocator.RowKinds.Where(k => k.Kind is "subtotal" or "grandTotal"))
        {
            foreach (var (name, get, set) in new (string, Func<string?>, Action<string>)[]
                     {
                         ("regex", () => kind.Regex, v => kind.Regex = v),
                         ("countsRegex", () => kind.CountsRegex, v => kind.CountsRegex = v),
                     })
            {
                if (get() is not { } pattern)
                {
                    continue;
                }

                var relaxed = RelaxedSeparators().Replace(pattern, m => m.Value switch
                {
                    @"\(" => @"\(?",
                    @"\)" => @"\)?",
                    _ => @"\s*-?\s*",
                });
                if (relaxed == pattern || !Compiles(relaxed))
                {
                    continue;
                }

                set(relaxed);
                notes.Add($"rowKinds {kind.Kind}.{name}: separators optional ({pattern} → {relaxed})");
            }
        }

        return notes;
    }

    private static bool Compiles(string pattern)
    {
        try
        {
            _ = new Regex(pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A literal "(" or ")" not already optional, or a required dash between optional/required spaces.</summary>
    [GeneratedRegex(@"\\\((?![?*])|\\\)(?![?*])|\\s[*+]-\\s[*+]|\\s[*+]-(?![?*])|-\\s[*+]")]
    private static partial Regex RelaxedSeparators();

    private static bool IsRowLevelTarget(CarrierFormat spec, ColumnMapping c) =>
        RowLevelDocumentFields.Contains(c.Target) || c.Target is "period.policyNumber" or "period.effectiveDate" or "period.expirationDate";

    /// <summary>
    /// Brief 31 (ADR 0026): a column whose header has value-shape conditional aliases ("Injury Type") is mapped by the shape of the values the learning
    /// document prints under it: codes → <c>claim.injuryTypeCode</c> (code normalizer, stat-plan table), words → <c>claim.claimType</c> (enum).
    /// A learned mapping to the other field is retargeted, without a model call. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> RetargetByValueShape(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        var notes = new List<string>();
        foreach (var column in spec.ColumnMap.Where(c => c.Target.StartsWith("claim.", StringComparison.Ordinal)))
        {
            var values = ColumnValues(graph, column.Source);
            if (values.Count == 0 || !IsShapePair(glossary, column.Source, column.Target) || glossary.ResolveHeader(column.Source, values) is not { } match
                || match.Field == column.Target)
            {
                continue;
            }

            notes.Add($"columnMap \"{column.Source}\": {column.Target} → {match.Field} (the values printed under it are {Glossary.ShapeOf(values)}s)");
            column.Target = match.Field;
            column.Normalizer = match.Field == "claim.injuryTypeCode"
                ? new NormalizerSpec { Type = "code", CodeSystem = "statPlan.injuryType" }
                : new NormalizerSpec { Type = "enum", GlossaryField = match.Field };
        }

        return notes;
    }

    private static bool IsShapePair(Glossary glossary, string header, string target) =>
        glossary.Fields.Where(f => f.Value.Aliases.Any(a => a.ValueShape is not null && CanonicalComparer.NormalizeText(a.Header) == CanonicalComparer.NormalizeText(header)))
            .Select(f => f.Key).Contains(target, StringComparer.Ordinal);

    /// <summary>The non-blank values printed under a header in the graph's tables (data rows only).</summary>
    internal static List<string> ColumnValues(DocumentGraph graph, string header)
    {
        var key = CanonicalComparer.NormalizeText(header);
        var values = new List<string>();
        foreach (var table in graph.Tables)
        {
            var index = table.HeaderTexts.FindIndex(h => CanonicalComparer.NormalizeText(h) == key);
            if (index < 0)
            {
                continue;
            }

            values.AddRange(graph.Cells.Where(c => c.TableId == table.Id && c.Column == index && !table.HeaderRows.Contains(c.Row) && c.RawValue.Trim().Length > 0)
                .Select(c => c.RawValue.Trim()));
        }

        return values;
    }

    /// <summary>
    /// Brief 31 item 7 (brief 30 item 20): a <c>column</c>-sourced spec whose learning document shows the fill-down layout declares
    /// <c>periods.fillDown</c>, without a model call: the policy cell is blank on two or more claim rows, every blank is under a printed
    /// policy (or under another blank of that run), a period's first row is never blank, and a printed policy is never repeated on the
    /// next row (a layout that repeats the policy has gaps, not fill-down). Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> DeclareFillDown(CarrierFormat spec, DocumentGraph graph)
    {
        if (spec.Periods.Source != "column" || spec.Periods.FillDown || spec.Periods.Column is not { Length: > 0 } header)
        {
            return [];
        }

        var key = CanonicalComparer.NormalizeText(header);
        var blanks = 0;
        foreach (var table in graph.Tables)
        {
            var index = table.HeaderTexts.FindIndex(h => CanonicalComparer.NormalizeText(h) == key);
            if (index < 0)
            {
                continue;
            }

            var rows = graph.Cells.Where(c => c.TableId == table.Id && !table.HeaderRows.Contains(c.Row)).GroupBy(c => c.Row).OrderBy(g => g.Key)
                .Where(g => g.Count(c => c.RawValue.Trim().Length > 0) >= 2 && !g.Any(c => TotalLabel().IsMatch(c.RawValue)))
                .Select(g => g.FirstOrDefault(c => c.Column == index)?.RawValue.Trim() ?? "").ToList();
            string? previous = null;
            var lastBlank = false;
            foreach (var policy in rows)
            {
                if (policy.Length == 0)
                {
                    if (previous is null)
                    {
                        return []; // a period's first row is blank: a gap, not fill-down
                    }

                    blanks++;
                    lastBlank = true;
                    continue;
                }

                if (!lastBlank && previous is not null && CanonicalComparer.NormalizeText(policy) == CanonicalComparer.NormalizeText(previous))
                {
                    return []; // the layout repeats the policy on the next row
                }

                (previous, lastBlank) = (policy, false);
            }
        }

        if (blanks < 2)
        {
            return [];
        }

        spec.Periods.FillDown = true;
        return [$"periods.fillDown: true (the learning document leaves the \"{header}\" cell blank on {blanks} claim rows under a printed policy)"];
    }

    [GeneratedRegex(@"^\s*(grand\s+|sub\s*)?totals?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TotalLabel();

    /// <summary>
    /// Brief 13 (corpus v3): a WCIO code column mapped to the whole coded field (<c>claim.natureOfInjury</c> with an
    /// <c>integer</c> normalizer) failed the type check on every row and all five learn iterations, because the canonical
    /// field is a {code, text, codeSystem} object. Such a mapping is retargeted to <c>.code</c> with a <c>code</c> normalizer
    /// and the WCIO code system (or to <c>.text</c> when it was read as text), without a model call. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> RetargetCodedColumns(CarrierFormat spec)
    {
        var notes = new List<string>();
        foreach (var column in spec.ColumnMap)
        {
            if (!CodeSystems.TryGetValue(column.Target, out var system))
            {
                continue;
            }

            var asText = column.Normalizer.Type is "text" or "enum";
            var target = $"{column.Target}.{(asText ? "text" : "code")}";
            if (spec.ColumnMap.Any(c => c != column && c.Target == target))
            {
                continue;
            }

            notes.Add($"columnMap \"{column.Source}\": {column.Target} ({column.Normalizer.Type}) → {target}{(asText ? "" : " (code)")}");
            column.Target = target;
            if (!asText)
            {
                column.Normalizer = new NormalizerSpec { Type = "code", CodeSystem = system, GlossaryField = column.Normalizer.GlossaryField };
            }
        }

        return notes;
    }

    /// <summary>
    /// Brief 13 (corpus v3, then the live system): a learned workbook spec read its summary sheet for period dates only
    /// (<c>periods.lookup.subtotals</c> empty), so it was admitted by the weaker spreadsheet-rules gate and a workbook whose
    /// printed policy total was $100 off its claim rows was delivered. When the lookup sheet prints count or total-incurred
    /// columns the glossary names unambiguously, they are mapped as subtotals without a model call; verification then ties
    /// them (R-10/R-11) and the spreadsheet-totals gate applies. Returns what changed.
    /// </summary>
    internal static IReadOnlyList<string> MapLookupSubtotals(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        var notes = new List<string>();
        // Brief 19 (corpus v3 regression): also when the model mapped some summary columns but not others (claims and open,
        // not total incurred): the gaps are filled, the model's mappings kept, so a printed total is still reconciled.
        if (spec.FileKind != "spreadsheet" || spec.Periods.Lookup is not { } lookup
            || graph.Tables.FirstOrDefault(t => t.SheetName == lookup.Sheet) is not { } table)
        {
            return notes;
        }

        string[] used = [lookup.KeyHeader, lookup.RangeHeader ?? "", lookup.EffectiveHeader ?? "", lookup.ExpirationHeader ?? ""];
        foreach (var header in table.HeaderTexts.Where(h => !used.Any(u => CanonicalComparer.NormalizeText(u) == CanonicalComparer.NormalizeText(h))))
        {
            var path = SummaryFields.Where(f => glossary.Fields.TryGetValue(f.Field, out var entry)
                    && entry.Aliases.Any(a => a.Header != "TOTAL" && CanonicalComparer.NormalizeText(a.Header) == CanonicalComparer.NormalizeText(header)))
                .Select(f => f.Path).FirstOrDefault();
            if (path is not null && !lookup.Subtotals.ContainsValue(path) && !lookup.Subtotals.Keys.Any(k => CanonicalComparer.NormalizeText(k) == CanonicalComparer.NormalizeText(header)))
            {
                lookup.Subtotals[header] = path;
                notes.Add($"periods.lookup.subtotals \"{header}\" → {path} (summary sheet {lookup.Sheet})");
            }
        }

        return notes;
    }

    /// <summary>Summary-sheet columns that map to one subtotal without ambiguity (money other than total incurred is left to the model).</summary>
    private static readonly (string Field, string Path)[] SummaryFields =
    [
        ("period.subtotals.claimCount", "claimCount"), ("document.reportTotals.claimCount", "claimCount"),
        ("period.subtotals.openCount", "openCount"), ("document.reportTotals.openCount", "openCount"),
        ("period.subtotals.closedCount", "closedCount"), ("document.reportTotals.closedCount", "closedCount"),
        ("claim.total.incurred", "total.incurred"),
    ];

    private static readonly Dictionary<string, string> CodeSystems = new(StringComparer.Ordinal)
    {
        ["claim.natureOfInjury"] = "WCIO-Nature", ["claim.causeOfInjury"] = "WCIO-Cause", ["claim.partOfBody"] = "WCIO-PartOfBody",
    };

    internal static IReadOnlyList<string> RetargetRowLevelColumns(CarrierFormat spec)
    {
        var notes = new List<string>();
        if (spec.FileKind != "spreadsheet")
        {
            return notes;
        }

        var (claimPaths, _) = Paths.Value;
        foreach (var column in spec.ColumnMap.Where(c => !claimPaths.Contains(c.Target) && !IsRowLevelTarget(spec, c) && (c.Target.StartsWith("claim.", StringComparison.Ordinal)
                     || c.Target.StartsWith("document.", StringComparison.Ordinal))).ToList())
        {
            var leaf = column.Target[(column.Target.LastIndexOf('.') + 1)..];
            leaf = leaf is "rawCarrier" or "carrier" ? "carrierName" : leaf;
            // "claim.periodEffectiveDate" / "policyEffectiveDate" → the period value.
            foreach (var prefix in new[] { "period", "policy" })
            {
                if (leaf.Length > prefix.Length && leaf.StartsWith(prefix, StringComparison.Ordinal) && char.IsUpper(leaf[prefix.Length]) && leaf != "policyNumber")
                {
                    leaf = char.ToLowerInvariant(leaf[prefix.Length]) + leaf[(prefix.Length + 1)..];
                }
            }

            string? target = null;
            if (column.Target.StartsWith("document.", StringComparison.Ordinal) && claimPaths.Contains($"claim.{leaf}"))
            {
                target = $"claim.{leaf}"; // a value printed per row (e.g. line of business) belongs to the claim
            }
            else if (leaf == "policyNumber")
            {
                target = "period.policyNumber";
                if (spec.Periods.Source != "column")
                {
                    (spec.Periods.Source, spec.Periods.Column) = ("column", column.Source);
                }
                else
                {
                    spec.Periods.Column ??= column.Source;
                }
            }
            else if (leaf is "effectiveDate" or "expirationDate")
            {
                target = $"period.{leaf}";
            }
            else if (RowLevelDocumentFields.Contains($"document.{leaf}"))
            {
                target = $"document.{leaf}";
            }

            if (target is not null && !spec.ColumnMap.Any(c => c != column && c.Target == target))
            {
                notes.Add($"columnMap \"{column.Source}\": {column.Target} → {target}");
                column.Target = target;
            }
        }

        // A document value read from a column on every row needs no second, cell-level locator (c5: a labelRight locator
        // for "Valuation Date" read the header row's neighbour, "Claim Number", while the column map read the right value).
        foreach (var field in spec.DocumentFields.Where(f => RowLevelDocumentFields.Contains(f.Target) && spec.ColumnMap.Any(c => c.Target == f.Target)).ToList())
        {
            spec.DocumentFields.Remove(field);
            notes.Add($"documentFields {field.Target}: dropped ({field.Locator.Type} locator); the column map reads it on every row");
        }

        return notes;
    }

    public static IReadOnlyList<string> Check(CarrierFormat spec, ReplayResult replay, DocumentGraph? graph = null, Glossary? glossary = null)
    {
        var issues = new List<string>();
        var (claimPaths, documentPaths) = Paths.Value;

        // The policy column of a periods.source = "column" format may be declared as period.policyNumber (the hand xlsx spec does).
        bool PeriodColumn(ColumnMapping c) =>
            c.Target == "period.policyNumber" && spec.Periods is { Source: "column", Column: { } col } && CanonicalComparer.NormalizeText(col) == CanonicalComparer.NormalizeText(c.Source)
            // Exports that repeat period dates / document values on every row (RowLevelValues in the replay engine).
            || c.Target is "period.effectiveDate" or "period.expirationDate" && spec.Periods.Source == "column"
            || RowLevelDocumentFields.Contains(c.Target) && spec.FileKind == "spreadsheet"
            // ADR 0017: total rows read period.subtotals.* (counts, recoveries by kind); a period-summary row is a period, so
            // its label, dates and policy number are period values.
            // A claims-shaped spec reads money on total rows through the claim.* money columns; a period.subtotals money
            // target there takes the column away from the claims (brief 19 corpus v3 regression: "Tot. Inc").
            || c.Target.StartsWith("period.subtotals.", StringComparison.Ordinal) && SubtotalPath().IsMatch(c.Target["period.subtotals.".Length..])
                && (spec.TableLocator.RowShape == "periodSummary" || !MoneyPath().IsMatch(c.Target["period.subtotals.".Length..]))
            || spec.TableLocator.RowShape == "periodSummary" && c.Target is "period.periodLabel" or "period.policyNumber" or "period.effectiveDate" or "period.expirationDate";
        foreach (var column in spec.ColumnMap.Where(c => !claimPaths.Contains(c.Target) && !PeriodColumn(c) && !ReplayEngine.MeasureComponents.Contains(c.Target)))
        {
            issues.Add(Suggest(spec, column) is { } hint
                ? $"lint: columnMap \"{column.Source}\" → {column.Target} is not a canonical path; {hint}"
                : $"lint: columnMap \"{column.Source}\" → {column.Target}: replay reads only claim.* canonical paths ({(column.Target.StartsWith("period.", StringComparison.Ordinal) ? "period values come from blockAnchor/periods, subtotal rows from rowKinds" : "unknown path")}); remove it or map the header to a claim field, or list the header in ignoredHeaders");
        }

        // Two printed columns read into one value: the last one read wins and the other is silently lost (brief 18 real-sample
        // run: six recovery columns of a summary-only run all mapped to claim.recoverySubrogation).
        foreach (var group in spec.ColumnMap.Where(c => c.Target != "claim.rawStatus").GroupBy(c => c.Target)
                     .Where(g => g.Select(c => CanonicalComparer.NormalizeText(c.Source)).Distinct().Count() > 1))
        {
            issues.Add($"lint: columnMap {string.Join(", ", group.Select(c => $"\"{c.Source}\""))} all map to {group.Key}; one column per target — map each recovery kind to period.subtotals.recoveries.deductible|reinsurance|sif|other (subrogation to claim.recoverySubrogation), and list columns the schema does not model in ignoredHeaders");
        }

        // Summary-sheet subtotals land in the period's subtotals, so their targets are subtotal-relative (eval run, c4:
        // "document.reportTotals.claimCount" / "claim.total.incurred" published and every subtotal scored wrong).
        foreach (var (header, target) in spec.Periods.Lookup?.Subtotals ?? [])
        {
            if (!SubtotalPath().IsMatch(target))
            {
                var leaf = target.Replace("document.reportTotals.", "", StringComparison.Ordinal).Replace("period.subtotals.", "", StringComparison.Ordinal)
                    .Replace("claim.", "", StringComparison.Ordinal);
                issues.Add($"lint: periods.lookup.subtotals \"{header}\" → {target}: targets are relative to the period's subtotals (claimCount, openCount, closedCount, total.incurred, medical.paid, …){(SubtotalPath().IsMatch(leaf) ? $"; use {leaf}" : "")}");
            }
        }

        foreach (var rule in spec.DerivedRules)
        {
            foreach (var path in rule.Sum.Prepend(rule.Target).Where(p => !MoneyPath().IsMatch(p)))
            {
                issues.Add($"lint: derivedRules path \"{path}\" must be relative to a claim or totals block, e.g. indemnity.incurred (component.measure, no 'claim.' prefix)");
            }
        }

        foreach (var field in spec.DocumentFields)
        {
            if (field.Target is "period.policyNumber" or "period.effectiveDate" or "period.expirationDate")
            {
                // Brief 23 §1: a single-period run's header block prints its policy period.
                if (field.Locator.Type != "headerLabel")
                {
                    issues.Add($"lint: documentFields target {field.Target} is a period field: only a headerLabel locator reads it (a single-period run's header block)");
                }

                continue;
            }

            if (!documentPaths.Contains(field.Target))
            {
                issues.Add($"lint: documentFields target {field.Target} is not a canonical document field");
            }

            if (field.Locator.Type == "const" && !ConstAllowed.Contains(field.Target))
            {
                issues.Add($"lint: {field.Target} is declared as a constant, but constants may only state layout facts ({string.Join(", ", ConstAllowed)}); locate it in the document or omit it — never assume it");
            }
        }

        issues.AddRange(LabelIssues(spec, glossary ?? CanonicalData.Glossary));

        // Brief 35 §3 at learn time: the carrier name read from the page and the spec's own carrier name are the same carrier, or one of
        // them is another party's name (brief 34 wake, c8: the insured read as the carrier on every replay).
        if (replay.Document.CarrierName is { Length: > 0 } printed && spec.CarrierName is { Length: > 0 } own
            && !LossRun.Formats.FormatFingerprinter.KnowsCarrier(spec, printed))
        {
            issues.Add($"lint: document.carrierName reads \"{printed}\" from the page, but the spec's carrierName is \"{own}\"; one of them is another party's name (the insured, the agent): read the carrier from its own label or omit document.carrierName");
        }

        if (graph is not null)
        {
            issues.AddRange(PrintedConstants(spec, graph, glossary ?? CanonicalData.Glossary));
        }

        issues.AddRange(ConstructIssues(spec, glossary ?? CanonicalData.Glossary));

        var expenseColumns = spec.ColumnMap.Any(c => c.Target.StartsWith("claim.expense.", StringComparison.Ordinal));
        var treatment = spec.DocumentFields.FirstOrDefault(f => f.Target == "document.expenseTreatment" && f.Locator.Type == "const")?.Locator.Value?.ToString();
        if (expenseColumns && treatment is not null && treatment != "separate")
        {
            issues.Add($"lint: expenseTreatment is \"{treatment}\" but the column map reads expense columns ({string.Join(", ", spec.ColumnMap.Where(c => c.Target.StartsWith("claim.expense.", StringComparison.Ordinal)).Select(c => c.Source))}); expense printed in its own columns is \"separate\" even when only expense paid is printed");
        }

        // A text capture that contains a date or another "Label:" ran past the end of its value (merged paragraphs).
        foreach (var path in new[] { "carrierName", "insuredName", "tpaName" })
        {
            var value = path switch { "carrierName" => replay.Document.CarrierName, "insuredName" => replay.Document.InsuredName, _ => replay.Document.TpaName };
            var locator = spec.DocumentFields.FirstOrDefault(f => f.Target == $"document.{path}")?.Locator;
            if (value is not null && locator?.Type == "regex" && (DateLike().IsMatch(value) || LabelLike().IsMatch(value)))
            {
                issues.Add($"lint: document.{path} captured \"{value}\", which runs into another printed label or date; end the capture before the next label (e.g. a lookahead)");
            }
        }

        issues.AddRange(ClaimNumberLineFieldIssues(spec, replay));
        if (spec.TableLocator.RowShape == "periodSummary" && graph?.LayoutEvidence?.FirstOrDefault(e => e.Construct == "claims-with-period-totals") is { } periodTotals)
        {
            issues.Add($"lint: rowShape periodSummary, but this document prints claim rows ({periodTotals.Evidence}): claim rows are the records; read the period rows as totals to tie against (rowKinds subtotal / grandTotal or periods.lookup.subtotals)");
        }
        return issues;
    }

    /// <summary>
    /// 06 Answers 3: a constant is allowed only when its value is not printed. If the value — or a glossary value alias
    /// of it (e.g. "Workers Compensation" for lineOfBusiness WC) — appears on page 1, the spec must locate it with a regex.
    /// Phrases shorter than 4 characters are not searched ("WC" also occurs inside policy numbers).
    /// </summary>
    internal static IEnumerable<string> PrintedConstants(CarrierFormat spec, DocumentGraph graph, Glossary glossary) =>
        PrintedConstantMatches(spec, graph, glossary).Select(m =>
            $"lint: {m.Field.Target} is declared as the constant \"{m.Value}\" but \"{m.Printed}\" is printed on page 1; locate it with a regex locator (constants only for values the document does not print)");

    /// <summary>
    /// Brief 08b (learn-reliability study: 15 of 20 failed learns ended on this lint, often with an otherwise verified spec):
    /// a constant whose value is printed on page 1 is rewritten — without a model call — as a regex locator that captures
    /// the printed phrase, so the value comes from the document as 06 Answers 3 requires (a later document that does not
    /// print it reads the field as missing instead of assuming it). Returns what was rewritten.
    /// </summary>
    internal static IReadOnlyList<string> LocatePrintedConstants(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        var notes = new List<string>();
        foreach (var (field, value, printed) in PrintedConstantMatches(spec, graph, glossary).ToList())
        {
            // A date differs per document (brief 13: a learned valuation date "2024-11-10" became a regex for that literal,
            // which on the next workbook matched a claim's date). Locate it by the label printed left of it, or not at all.
            if (CanonicalComparer.NormalizeDate(value) is { } date)
            {
                // Brief 30 item 45: the label left of the date must be one the glossary knows for this field ("Valuation Date"), so a claim row
                // whose date of injury happens to equal it (its claim number to the left) is never taken for the header label.
                var labels = glossary.Fields.TryGetValue(field.Target, out var known)
                    ? known.Aliases.Select(a => CanonicalComparer.NormalizeText(a.Header)).ToHashSet(StringComparer.Ordinal) : [];
                var label = graph.Cells.Where(c => CanonicalComparer.NormalizeDate(c.RawValue) == date && c.Column > 0)
                    .Select(cell => graph.Cells.FirstOrDefault(c => c.Page == cell.Page && c.Row == cell.Row && c.Column == cell.Column - 1
                        && CanonicalComparer.NormalizeDate(c.RawValue) is null && !string.IsNullOrWhiteSpace(c.RawValue))?.RawValue)
                    .FirstOrDefault(l => l is not null && labels.Contains(CanonicalComparer.NormalizeText(l)));
                if (label is null)
                {
                    continue; // left to the model (the lint still names it)
                }

                field.Locator = new FieldLocator { Type = "labelRight", Label = label.Trim() };
                field.Normalizer = new NormalizerSpec { Type = "date", GlossaryField = field.Target };
                field.Reason = $"autofix: the date constant \"{value}\" is printed right of \"{label.Trim()}\"; located by that label";
                notes.Add($"{field.Target}: date constant \"{value}\" → labelRight \"{label.Trim()}\"");
                continue;
            }

            var pattern = "(?i)(?<value>" + string.Join("[^A-Za-z0-9]+", printed.Split(' ').Select(System.Text.RegularExpressions.Regex.Escape)) + ")";
            field.Locator = new FieldLocator { Type = "regex", Pattern = pattern };
            field.Normalizer = glossary.Fields.TryGetValue(field.Target, out var entry) && entry.ValueAliases is { Count: > 0 }
                ? new NormalizerSpec { Type = "enum", GlossaryField = field.Target }
                : new NormalizerSpec { Type = "text" };
            field.Reason = $"autofix: the constant \"{value}\" is printed on page 1 as \"{printed}\"; located instead of assumed";
            notes.Add($"{field.Target}: constant \"{value}\" → located \"{printed}\"");
        }

        return notes;
    }

    /// <summary>A period's dates may share one label: "Policy Period 01/01/2024 – 01/01/2025" prints both.</summary>
    private static readonly string[][] SharedLabelTargets = [["period.effectiveDate", "period.expirationDate"]];

    /// <summary>
    /// The fields a header-block label names in the glossary: aliases that apply to labels (no <c>appliesTo</c>, or <c>label</c>), without a
    /// value-shape condition (ADR 0026: a table-dependent meaning is not a label's meaning).
    /// </summary>
    internal static IReadOnlyList<string> LabelMeanings(Glossary glossary, string? label)
    {
        var key = CanonicalComparer.NormalizeText(label);
        return key.Length == 0 ? [] : glossary.Fields
            .Where(f => f.Value.Aliases.Any(a => a.AppliesTo is null or "label" && a.ValueShape is null && CanonicalComparer.NormalizeText(a.Header) == key))
            .Select(f => f.Key).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Brief 35 §1–2: one label is one field. Two document fields read from one label (or from two labels the glossary gives the same meaning)
    /// put one printed value into two fields; a field read from a label the glossary gives another header-block field reads that field's value
    /// (brief 34 wake, c8: <c>document.carrierName</c> and <c>document.insuredName</c> both on "Insured").
    /// </summary>
    private static IEnumerable<string> LabelIssues(CarrierFormat spec, Glossary glossary)
    {
        var labelled = spec.DocumentFields
            .Where(f => f.Locator.Type is "labelRight" or "labelNext" or "headerLabel" && CanonicalComparer.NormalizeText(f.Locator.Label).Length > 0)
            .Select(f => (Field: f, Key: CanonicalComparer.NormalizeText(f.Locator.Label), Meanings: LabelMeanings(glossary, f.Locator.Label)))
            .ToList();
        bool Shared(IEnumerable<string> targets) => SharedLabelTargets.Any(pair => targets.All(pair.Contains));
        string Aliases(string target) => glossary.Fields.TryGetValue(target, out var g) && g.Aliases.Count > 0
            ? string.Join(", ", g.Aliases.Where(a => a.AppliesTo is null or "label").Take(4).Select(a => $"\"{a.Header}\""))
            : "its own label";

        foreach (var group in labelled.GroupBy(x => x.Key).Where(g => g.Select(x => x.Field.Target).Distinct().Count() > 1))
        {
            var targets = group.Select(x => x.Field.Target).Distinct().ToList();
            if (!Shared(targets))
            {
                yield return $"lint: documentFields {string.Join(" and ", targets)} all read the label \"{group.First().Field.Locator.Label}\"; one label is one field: "
                    + string.Join("; ", targets.Select(t => $"{t} is printed under {Aliases(t)}")) + " — read each from its own label, or omit the one the page does not print";
            }
        }

        foreach (var group in labelled.Where(x => x.Meanings.Count == 1).GroupBy(x => x.Meanings[0])
                     .Where(g => g.Select(x => x.Key).Distinct().Count() > 1 && g.Select(x => x.Field.Target).Distinct().Count() > 1))
        {
            var targets = group.Select(x => x.Field.Target).Distinct().ToList();
            if (!Shared(targets))
            {
                yield return $"lint: documentFields {string.Join(", ", group.Select(x => $"{x.Field.Target} (\"{x.Field.Locator.Label}\")"))} read labels the glossary gives one meaning, {group.Key}; only {group.Key} is printed there";
            }
        }

        foreach (var (field, _, meanings) in labelled)
        {
            // Only header-block meanings: a label the glossary knows as a claim column ("Total Incurred") may still head a report total.
            if (meanings.Count > 0 && !meanings.Contains(field.Target) && meanings.All(m => m.StartsWith("document.", StringComparison.Ordinal) || m.StartsWith("period.", StringComparison.Ordinal)))
            {
                yield return $"lint: documentFields {field.Target} reads the label \"{field.Locator.Label}\", which the glossary maps to {string.Join(" / ", meanings)}; {field.Target} is printed under {Aliases(field.Target)} — read it from its own label, or omit it";
            }
        }
    }

    private static IEnumerable<(DocumentFieldSpec Field, string Value, string Printed)> PrintedConstantMatches(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        var first = graph.Pages.OrderBy(p => p.Number).FirstOrDefault()?.Number;
        var page = " " + string.Join(" ", graph.Paragraphs.Where(p => p.Page == first).Select(p => p.Text)
            .Concat(graph.Cells.Where(c => c.Page == first).Select(c => c.RawValue))
            .Select(CanonicalComparer.NormalizeText)) + " ";
        foreach (var field in spec.DocumentFields.Where(f => f.Locator.Type == "const" && f.Locator.Value is { ValueKind: System.Text.Json.JsonValueKind.String }))
        {
            var value = field.Locator.Value!.Value.GetString()!;
            var phrases = new List<string> { value };
            if (glossary.Fields.TryGetValue(field.Target, out var entry) && entry.ValueAliases?.FirstOrDefault(v => CanonicalComparer.NormalizeText(v.Key) == CanonicalComparer.NormalizeText(value)) is { Value: { } aliases })
            {
                phrases.AddRange(aliases.Select(a => a.Value));
            }

            var printed = phrases.Select(CanonicalComparer.NormalizeText).Where(p => p.Length >= 4).FirstOrDefault(p => page.Contains($" {p} ", StringComparison.Ordinal));
            if (printed is not null)
            {
                yield return (field, value, printed);
            }
        }
    }

    /// <summary>
    /// Brief 19 (corpus v3 regression, c4): <c>periods.lookup.subtotals</c> written target → header ("claimCount": "Claims")
    /// instead of header → target. An entry whose key is a subtotal path and whose value is not is swapped. No model call.
    /// </summary>
    internal static IReadOnlyList<string> FixInvertedLookupSubtotals(CarrierFormat spec)
    {
        var notes = new List<string>();
        if (spec.Periods.Lookup is not { } lookup)
        {
            return notes;
        }

        foreach (var (key, value) in lookup.Subtotals.ToList())
        {
            if (SubtotalPath().IsMatch(key) && !SubtotalPath().IsMatch(value) && !lookup.Subtotals.ContainsKey(value))
            {
                lookup.Subtotals.Remove(key);
                lookup.Subtotals[value] = key;
                notes.Add($"periods.lookup.subtotals \"{key}\": \"{value}\" → \"{value}\": \"{key}\" (header → subtotal path)");
            }
        }

        return notes;
    }

    /// <summary>
    /// Brief 19 (corpus v3 regression, c2): a total-row regex anchored at the end of its label ("^Policy Total … Claims?$")
    /// never matches, because the row's text continues with the amounts; the total rows are then read as claims. A trailing
    /// <c>$</c> is dropped when that alone makes the regex match rows of this document. No model call.
    /// </summary>
    internal static IReadOnlyList<string> UnanchorTotalRowKinds(CarrierFormat spec, DocumentGraph graph)
    {
        var notes = new List<string>();
        var rows = graph.Cells.Where(c => c.TableId is not null).GroupBy(c => (c.TableId, c.Row))
            .Select(g => string.Join(" ", g.OrderBy(c => c.Column).Select(c => c.RawValue.Trim()).Where(t => t.Length > 0))).ToList();
        foreach (var kind in spec.TableLocator.RowKinds.Where(k => k.Kind is "subtotal" or "grandTotal" && k.Regex.EndsWith('$') && !k.Regex.EndsWith(@"\$", StringComparison.Ordinal)))
        {
            var unanchored = kind.Regex[..^1];
            if (Compiles(kind.Regex) && Compiles(unanchored) && !rows.Any(r => Regex.IsMatch(r, kind.Regex)) && rows.Any(r => Regex.IsMatch(r, unanchored)))
            {
                notes.Add($"rowKinds {kind.Kind}: trailing $ dropped (the row's amounts follow its label)");
                kind.Regex = unanchored;
            }
        }

        return notes;
    }

    /// <summary>
    /// Brief 19 (constructs run): parts of a spec that can never take effect are removed instead of failing every iteration —
    /// <c>records.lineFields</c> in rows mode (only lines mode reads them), and a constant for an optional document field
    /// outside <see cref="ConstAllowed"/> (the lint forbids assuming it; omitting it loses nothing the gate requires). No model call.
    /// </summary>
    internal static IReadOnlyList<string> DropInertParts(CarrierFormat spec)
    {
        var notes = new List<string>();
        if (spec.TableLocator.Records is { Mode: "rows", LineFields.Count: > 0 } records)
        {
            notes.Add($"records.lineFields ({records.LineFields.Count}) removed: rows mode does not read them");
            records.LineFields.Clear();
        }

        var required = LearnGate.Default.RequiredFields.ToHashSet(StringComparer.Ordinal);
        foreach (var field in spec.DocumentFields.Where(f => f.Locator.Type == "const" && !ConstAllowed.Contains(f.Target) && !required.Contains(f.Target)).ToList())
        {
            spec.DocumentFields.Remove(field);
            notes.Add($"{field.Target}: constant removed (not printed, optional, never assumed)");
        }

        return notes;
    }

    /// <summary>ADR 0018: the record, stacked-cell and component-sum constructs used consistently.</summary>
    private static IEnumerable<string> ConstructIssues(CarrierFormat spec, Glossary glossary)
    {
        var records = spec.TableLocator.Records;
        var sources = spec.ColumnMap.Where(c => c.Line == 0).Select(c => CanonicalComparer.NormalizeText(c.Source)).Concat(spec.IgnoredHeaders.Select(CanonicalComparer.NormalizeText))
            .ToHashSet(StringComparer.Ordinal);
        if (records is { Mode: not ("rows" or "lines" or "measures") })
        {
            yield return $"lint: tableLocator.records.mode \"{records.Mode}\" must be rows, lines or measures";
        }

        // Brief 22 Answers 1: a component without a measure is read per measure row, so only in records mode measures.
        var components = spec.ColumnMap.Where(c => ReplayEngine.MeasureComponents.Contains(c.Target)).ToList();
        if (records is { Mode: "measures" })
        {
            if (components.Count == 0)
            {
                yield return "lint: records.mode measures reads the component columns per measure row: map each component column to the component without a measure (claim.indemnity, claim.medical, claim.expense, claim.total)";
            }

            foreach (var column in spec.ColumnMap.Where(c => c.Line > 0))
            {
                yield return $"lint: columnMap \"{column.Source}\" → {column.Target} has line {column.Line}; records.mode measures reads measure rows by their label, not by line";
            }

            foreach (var (measure, pattern) in (records.MeasureLabels ?? new MeasureLabels()).Patterns())
            {
                if (!Compiles(pattern))
                {
                    yield return $"lint: records.measureLabels.{measure} does not compile";
                }
            }
        }
        else
        {
            foreach (var column in components)
            {
                yield return $"lint: columnMap \"{column.Source}\" → {column.Target} names a component without a measure, which only records.mode measures reads; add the measure (e.g. {column.Target}.paid)";
            }
        }

        // Brief 22: a header the spec ignores is not also read into a field (a published summary spec read an analysis column as a recovery).
        var ignored = spec.IgnoredHeaders.Select(CanonicalComparer.NormalizeText).ToHashSet(StringComparer.Ordinal);
        foreach (var column in spec.ColumnMap.Where(c => ignored.Contains(CanonicalComparer.NormalizeText(c.Source))).DistinctBy(c => CanonicalComparer.NormalizeText(c.Source)))
        {
            yield return $"lint: columnMap \"{column.Source}\" → {column.Target} is also in ignoredHeaders; map the column or ignore it, not both";
        }

        if (records is { Mode: "rows", KeySource: { } key } && !sources.Contains(CanonicalComparer.NormalizeText(key)))
        {
            yield return $"lint: tableLocator.records.keySource \"{key}\" is not a columnMap source on line 0";
        }

        foreach (var column in spec.ColumnMap.Where(c => c.Line > 0))
        {
            if (records is not { Mode: "rows" })
            {
                yield return $"lint: columnMap \"{column.Source}\" → {column.Target} has line {column.Line}, which needs tableLocator.records.mode = rows";
            }
            else if (column.Under is { } under && !sources.Contains(CanonicalComparer.NormalizeText(under)))
            {
                yield return $"lint: columnMap \"{column.Source}\" → {column.Target}: under \"{under}\" must be a line-0 header the spec maps or ignores";
            }
        }

        if (records is { LineFields.Count: > 0, Mode: not "lines" })
        {
            yield return "lint: tableLocator.records.lineFields are read only with records.mode = lines";
        }

        foreach (var field in records?.LineFields ?? [])
        {
            if (!Compiles(field.Pattern))
            {
                yield return $"lint: records.lineFields {field.Target}: pattern does not compile";
            }
        }

        foreach (var group in spec.ColumnMap.Where(c => c.Part is not null).GroupBy(c => CanonicalComparer.NormalizeText(c.Source)))
        {
            // claim.rawStatus is the printed companion of claim.status and may read the same part (brief 20, corpus v4 c8).
            var parts = group.Where(c => c.Target != "claim.rawStatus" || !group.Any(s => s.Target == "claim.status" && s.Part == c.Part)).ToList();
            if (parts.Select(c => c.Part).Distinct().Count() != parts.Count)
            {
                yield return $"lint: columnMap \"{group.First().Source}\": each part of a stacked cell maps once";
            }

            foreach (var column in group.Where(c => c.PartPattern is { } p && (!Compiles(p) || !p.Contains($"<p{c.Part}>", StringComparison.Ordinal))))
            {
                yield return $"lint: columnMap \"{column.Source}\" part {column.Part}: partPattern must compile and capture group p{column.Part}";
            }
        }

        foreach (var column in spec.ColumnMap.Where(c => c.Components is not null))
        {
            var named = glossary.ResolveHeader(column.Source) is { Method: "component-sum", Components: { } c } ? c : null;
            if (!column.Target.StartsWith("claim.total.", StringComparison.Ordinal))
            {
                yield return $"lint: columnMap \"{column.Source}\": components apply to a claim.total.* column only";
            }
            else if (named is not null && !named.Order(StringComparer.Ordinal).SequenceEqual(column.Components!.Order(StringComparer.Ordinal)))
            {
                yield return $"lint: columnMap \"{column.Source}\": the header names {string.Join(" + ", named)}, not {string.Join(" + ", column.Components!)}";
            }
        }

        if (spec.StatusFrom is { } from)
        {
            if (from is not ("closeDate" or "unknown"))
            {
                yield return $"lint: statusFrom \"{from}\" must be closeDate or unknown";
            }
            else if (spec.ColumnMap.Any(c => c.Target == "claim.status"))
            {
                yield return "lint: statusFrom applies only when no column maps claim.status";
            }
            else if (from == "closeDate" && spec.ColumnMap.All(c => c.Target != "claim.dateClosed"))
            {
                yield return "lint: statusFrom closeDate needs a column mapped to claim.dateClosed";
            }
        }

        foreach (var component in spec.AbsentComponents)
        {
            if (spec.ColumnMap.Any(c => c.Target.StartsWith($"claim.{component}.", StringComparison.Ordinal)))
            {
                yield return $"lint: absentComponents lists {component} but a column maps claim.{component}.*";
            }
        }
    }

    /// <summary>
    /// ADR 0018 (brief 18 real-sample run): a document date printed as a label paragraph followed by a value paragraph
    /// ("Valuation Date" / "9/30/2024") cannot be read by a label-and-value regex. When the spec's locator finds nothing and
    /// such a label (a glossary alias of the field) is printed with a date after it, the field is located by
    /// <c>labelNext</c>. No model call.
    /// </summary>
    internal static IReadOnlyList<string> LocateLabelNextDates(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        var notes = new List<string>();
        if (spec.FileKind != "pdf")
        {
            return notes;
        }

        foreach (var (target, isDate) in new[] { ("document.valuationDate", true), ("document.reportRunDate", true), ("document.insuredName", false), ("document.carrierName", false) })
        {
            var field = spec.DocumentFields.FirstOrDefault(f => f.Target == target);
            if (field is { Locator.Type: "regex", Locator.Pattern: { } pattern } && Compiles(pattern) && graph.Paragraphs.Any(p => Regex.IsMatch(p.Text, pattern)))
            {
                continue; // located
            }

            if (field is not null && field.Locator.Type is not ("regex" or "const"))
            {
                continue;
            }

            if (!isDate && field is null)
            {
                continue; // a name the spec does not ask for is not added
            }

            var aliases = glossary.Fields.TryGetValue(target, out var entry) ? entry.Aliases.Select(a => CanonicalComparer.NormalizeText(a.Header)).ToHashSet(StringComparer.Ordinal) : [];
            bool Value(string text) => isDate ? CanonicalComparer.NormalizeDate(text.Trim()) is not null
                : text.Trim().Length > 1 && glossary.ResolveHeader(text) is null && !glossary.IsLabel(text) && CanonicalComparer.NormalizeDate(text.Trim()) is null;
            for (var i = 0; i < graph.Paragraphs.Count; i++)
            {
                var label = graph.Paragraphs[i];
                if (!aliases.Contains(CanonicalComparer.NormalizeText(label.Text))
                    || graph.Paragraphs.Skip(i + 1).Where(p => p.Page == label.Page).Take(isDate ? 3 : 1).All(p => !Value(p.Text)))
                {
                    continue;
                }

                field ??= AddField(spec, target);
                field.Locator = new FieldLocator { Type = "labelNext", Label = label.Text.Trim() };
                field.Normalizer = new NormalizerSpec { Type = isDate ? "date" : "text" };
                field.Reason = $"autofix: \"{label.Text.Trim()}\" and its value are printed as separate paragraphs; located by labelNext";
                notes.Add($"{target}: labelNext \"{label.Text.Trim()}\"");
                break;
            }
        }

        return notes;
    }

    private static DocumentFieldSpec AddField(CarrierFormat spec, string target)
    {
        var field = new DocumentFieldSpec { Target = target };
        spec.DocumentFields.Add(field);
        return field;
    }

    /// <summary>
    /// ADR 0018 (brief 18 real-sample run): periods come from a column, the claim table also prints the period's inception and expiration dates
    /// in their own columns (headers that are glossary aliases of period.effectiveDate / expirationDate, confidence ≥ 0.8),
    /// and the spec reads neither. They are mapped as row-level period values. No model call.
    /// </summary>
    internal static IReadOnlyList<string> MapPeriodDateColumns(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        var notes = new List<string>();
        if (spec.Periods.Source != "column" || spec.Periods.Column is not { } periodColumn)
        {
            return notes;
        }

        var key = CanonicalComparer.NormalizeText(periodColumn);
        var table = graph.Tables.FirstOrDefault(t => t.HeaderTexts.Any(h => CanonicalComparer.NormalizeText(h) == key));
        if (table is null)
        {
            return notes;
        }

        foreach (var target in new[] { "period.effectiveDate", "period.expirationDate" })
        {
            if (spec.ColumnMap.Any(c => c.Target == target))
            {
                continue;
            }

            var header = table.HeaderTexts.FirstOrDefault(h => CanonicalComparer.NormalizeText(h) != key
                && glossary.ResolveHeader(h) is { Method: "alias", Confidence: >= 0.8 } m && m.Field == target);
            if (header is null)
            {
                continue;
            }

            spec.ColumnMap.Add(new ColumnMapping { Source = header, Target = target, Normalizer = new NormalizerSpec { Type = "date" } });
            spec.IgnoredHeaders.RemoveAll(h => CanonicalComparer.NormalizeText(h) == CanonicalComparer.NormalizeText(header));
            notes.Add($"\"{header}\" → {target} (row-level period date)");
        }

        return notes;
    }

    /// <summary>
    /// Brief 19 Answers 2: a run that prints no status column. Status from the close date is a carrier convention (some print
    /// a close date on reopened claims), so it is kept only when the run proves it: the replay with <c>statusFrom: closeDate</c>
    /// reconciles to printed open/closed counts (R-11 or R-13 pass, none fails). Otherwise <c>statusFrom: unknown</c>: every
    /// claim's status is unknown and R-30 flags it. A status the model set beside a status column is removed. No model call.
    /// </summary>
    internal static IReadOnlyList<string> DeclareStatusFrom(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        if (spec.ColumnMap.Any(c => c.Target is "claim.status" or "claim.rawStatus"))
        {
            if (spec.StatusFrom is null)
            {
                return [];
            }

            spec.StatusFrom = null;
            return ["statusFrom removed: a column maps claim.status"];
        }

        var claimSources = spec.ColumnMap.Where(c => c.Target.StartsWith("claim.", StringComparison.Ordinal)).Select(c => CanonicalComparer.NormalizeText(c.Source)).ToHashSet(StringComparer.Ordinal);
        var tables = graph.Tables.Where(t => t.HeaderTexts.Count(h => claimSources.Contains(CanonicalComparer.NormalizeText(h))) >= 2).ToList();
        if (tables.Count == 0 || tables.Any(t => t.HeaderFields.Contains("claim.status") || t.HeaderTexts.Any(h => glossary.ResolveHeader(h)?.Field == "claim.status")))
        {
            return []; // a status column is printed: the model should map it (left to the feedback)
        }

        var before = spec.StatusFrom;
        if (spec.ColumnMap.Any(c => c.Target == "claim.dateClosed"))
        {
            spec.StatusFrom = "closeDate";
            var replay = Formats.Replay.ReplayEngine.Execute(graph, spec, glossary: glossary); // brief 30 item 18: the glossary the lint was given
            var counts = replay.RuleOutcomes.Where(o => o.RuleId is "R-11" or "R-13").ToList();
            var printedCounts = replay.Document.PolicyPeriods.Any(p => p.Subtotals is { OpenCount: not null } or { ClosedCount: not null })
                || replay.Document.ReportTotals is { OpenCount: not null } or { ClosedCount: not null };
            if (printedCounts && counts.Any(o => o.Status == RuleStatus.Pass) && counts.All(o => o.Status != RuleStatus.Fail))
            {
                return before == "closeDate" ? [] : ["statusFrom closeDate: verified against the printed open/closed counts"];
            }
        }

        spec.StatusFrom = "unknown";
        return before == "unknown" ? [] : ["statusFrom unknown: no status column, and the close date is not verified by printed open/closed counts"];
    }

    /// <summary>
    /// Brief 19 Answers 1: the claim tables print no medical column at all (no header resolves to a medical field) and the
    /// spec maps none: the spec declares <c>absentComponents: ["medical"]</c>, so the document records medical
    /// { present: false } and the gate does not require medical values. No model call.
    /// </summary>
    internal static IReadOnlyList<string> DeclareAbsentComponents(CarrierFormat spec, DocumentGraph graph, Glossary glossary)
    {
        if (spec.AbsentComponents.Contains("medical") || spec.ColumnMap.Any(c => c.Target.StartsWith("claim.medical.", StringComparison.Ordinal))
            || spec.TableLocator.RowShape == "periodSummary")
        {
            return [];
        }

        var claimSources = spec.ColumnMap.Where(c => c.Target.StartsWith("claim.", StringComparison.Ordinal)).Select(c => CanonicalComparer.NormalizeText(c.Source)).ToHashSet(StringComparer.Ordinal);
        var tables = graph.Tables.Where(t => t.HeaderTexts.Count(h => claimSources.Contains(CanonicalComparer.NormalizeText(h))) >= 2).ToList();
        if (tables.Count == 0 || tables.Any(t => t.HeaderTexts.Any(h => glossary.ResolveHeader(h) is { } m && (m.Field.StartsWith("claim.medical.", StringComparison.Ordinal)
                || m.Components?.Contains("medical") == true || m.RequiresComponent == "medical"))))
        {
            return [];
        }

        spec.AbsentComponents.Add("medical");
        return ["absentComponents medical: the claim tables print no medical column"];
    }

    private static (HashSet<string>, HashSet<string>) BuildPaths()
    {
        var schema = JsonNode.Parse(CanonicalData.CanonicalSchemaJson)!;
        var defs = schema["$defs"]!.AsObject();
        static string? Ref(JsonNode? node) =>
            node?["$ref"]?.GetValue<string>() ?? (node?["anyOf"] as JsonArray)?.Select(a => a?["$ref"]?.GetValue<string>()).FirstOrDefault(r => r is not null);

        IEnumerable<string> Expand(string prefix, JsonObject properties) => properties.SelectMany(p =>
        {
            var reference = Ref(p.Value)?.Split('/').Last();
            return reference is not null && defs[reference]?["properties"] is JsonObject nested && reference is "MoneyTriplet" or "CodedValue"
                ? nested.Select(n => $"{prefix}{p.Key}.{n.Key}")
                : [$"{prefix}{p.Key}"];
        });

        var claim = Expand("claim.", defs["Claim"]!["properties"]!.AsObject()).ToHashSet(StringComparer.Ordinal);
        string[] structural = ["schemaVersion", "documentId", "policyPeriods", "lineage", "reportTotals"];
        var document = Expand("document.", schema["properties"]!.AsObject()).Where(p => !structural.Contains(p["document.".Length..].Split('.')[0]))
            .ToHashSet(StringComparer.Ordinal);
        return (claim, document);
    }

    [GeneratedRegex(@"^(indemnity|medical|expense|total)\.(paid|reserve|incurred)$")]
    private static partial Regex MoneyPath();

    [GeneratedRegex(@"\d{1,2}/\d{1,2}/\d{2,4}|\d{4}-\d{2}-\d{2}")]
    private static partial Regex DateLike();

    [GeneratedRegex(@"\b[A-Z][A-Za-z ]{2,30}:\s")]
    private static partial Regex LabelLike();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(claimCount|openCount|closedCount|recoverySubrogation|recoveries\.(deductible|reinsurance|sif|other)|(indemnity|medical|expense|total)\.(paid|reserve|incurred))$")]
    private static partial System.Text.RegularExpressions.Regex SubtotalPath();
}
