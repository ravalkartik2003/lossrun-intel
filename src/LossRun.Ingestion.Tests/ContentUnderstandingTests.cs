using System.Text.Json;
using LossRun.Contracts;
using LossRun.Ingestion;
using LossRun.Ingestion.ContentUnderstanding;
using LossRun.Ingestion.Metering;

namespace LossRun.Ingestion.Tests;

/// <summary>Mapping, metering and pipeline behaviour against recorded CU responses (no network).</summary>
public class ContentUnderstandingTests
{
    private static JsonElement Layout => JsonDocument.Parse(Fixture.Recorded("prebuilt-layout.lossrun-native.json")).RootElement;
    private static JsonElement Classifier => JsonDocument.Parse(Fixture.Recorded("lossrun_classifier_v1.bundle.json")).RootElement;

    private static DocumentGraph MapLayout() =>
        CuResultMapper.Map(Layout, "doc-native", new SourceFile { FileName = "lossrun-native.pdf", Kind = FileKind.Pdf }, "prebuilt-layout", CanonicalData.Glossary);

    [Fact]
    public void Layout_maps_pages_tables_and_grounding()
    {
        var graph = MapLayout();

        var page = Assert.Single(graph.Pages);
        Assert.Equal((1, 11.0, 8.5, 0.0), (page.Number, page.Width, page.Height, page.Angle));
        Assert.Equal("inch", graph.Unit);
        Assert.Contains("Valued as of: 09/30/2024", page.Markdown);
        Assert.Equal(2, graph.Tables.Count);
        Assert.Equal(["Claim #", "Claimant", "DOI", "Reported", "Status", "Type", "Ind Paid", "Ind Reserve", "Med Paid", "Med Reserve", "Exp Paid", "Total Incurred"],
            graph.Tables[0].HeaderTexts);
        Assert.Equal("claim.indemnity.paid", graph.Tables[0].HeaderFields[6]);
        // Glossary 0.1.2 added "Reported" and the "Exp" component word, so every header now resolves.
        Assert.Equal("claim.dateReported", graph.Tables[0].HeaderFields[3]);
        Assert.Equal("claim.expense.paid", graph.Tables[0].HeaderFields[10]);
        Assert.All(graph.Tables[0].HeaderFields, Assert.NotNull);
    }

    [Fact]
    public void Cells_carry_page_polygon_lineage_and_typed_values()
    {
        var cells = MapLayout().Cells.Where(c => c.TableId == "t0").ToList();

        var claimNumber = cells.Single(c => c.Row == 1 && c.Column == 0);
        Assert.Equal(("WC-22-0001", 1, 8), (claimNumber.RawValue, claimNumber.Page, claimNumber.Polygon!.Count));
        var doi = cells.Single(c => c.Row == 1 && c.Column == 2);
        Assert.Equal(("2022-09-15", CellValueType.Date), (doi.TypedValue, doi.ValueType));
        var paid = cells.Single(c => c.Row == 1 && c.Column == 6);
        Assert.Equal(("12000.00", CellValueType.Number), (paid.TypedValue, paid.ValueType));
        Assert.Equal("columnHeader", cells.Single(c => c.Row == 0 && c.Column == 0).Kind);
    }

    [Fact]
    public void Words_keep_confidence_and_paragraph_roles_survive()
    {
        var graph = MapLayout();

        Assert.True(graph.Words.Count > 100);
        Assert.All(graph.Words, w => Assert.InRange(w.Confidence!.Value, 0.0, 1.0));
        Assert.Equal("title", graph.Paragraphs.First(p => p.Text == "Synthetic Mutual Insurance Co.").Role);
        Assert.Contains(graph.Paragraphs, p => p.Role == "pageFooter");
    }

    [Fact]
    public void Fingerprint_inputs_are_extracted_once()
    {
        var inputs = MapLayout().FingerprintInputs;

        Assert.Equal("Synthetic Mutual Insurance Co.", inputs.TitleText);
        Assert.Contains("Valued as of: 09/30/2024", inputs.HeaderParagraphs);
        Assert.DoesNotContain("Claim #", inputs.HeaderParagraphs);
        Assert.Equal(12, inputs.FirstTableHeaderCells.Count);
        Assert.NotNull(inputs.FirstPageMarkdown);
    }

    [Theory]
    [InlineData("D(1,0.4908,0.3889,1.3605,0.3867,1.3612,0.5925,0.4918,0.5964)", 1, 8)]
    [InlineData("D(3,1,2,3,4,5,6,7,8);D(4,1,2,3,4,5,6,7,8)", 3, 8)]
    [InlineData(null, null, null)]
    public void Grounding_sources_parse(string? source, int? page, int? points)
    {
        var (p, polygon) = CuResultMapper.ParseSource(source);

        Assert.Equal(page, p);
        Assert.Equal(points, polygon?.Count);
    }

    [Fact]
    public void Classifier_segments_map_in_page_order_without_confidence()
    {
        var segments = CuResultMapper.MapSegments(Classifier);

        Assert.Equal([("LossRun", 1, 1), ("Payroll", 2, 2), ("Correspondence", 3, 3)], segments.Select(s => (s.Category, s.StartPage, s.EndPage)));
        Assert.All(segments, s => Assert.Null(s.Confidence)); // CU GA does not return a segment confidence
    }

    [Fact]
    public void Usage_becomes_one_event_per_meter()
    {
        var events = CuUsage.ToEvents(Classifier, "doc-b", "run-1", "classify", "lossrun_classifier_v1", "eastus2", "DataZoneStandard", "op-9", DateTimeOffset.UnixEpoch);

        Assert.Equal(
            [
                ("ContentUnderstanding", "documentPagesStandard", 3m, "page", "lossrun_classifier_v1"),
                ("ContentUnderstanding", "contextualizationTokens", 3000m, "token", "lossrun_classifier_v1"),
                ("AzureOpenAI", "inputTokens", 1400m, "token", "gpt-5.4-mini"),
                ("AzureOpenAI", "outputTokens", 56m, "token", "gpt-5.4-mini"),
            ],
            events.Select(e => (e.Service, e.Meter, e.Quantity, e.Unit, e.ModelOrAnalyzer!)));
        Assert.All(events, e => Assert.Equal(("doc-b", "run-1", "classify", "eastus2", "op-9"), (e.DocumentId, e.RunId, e.Stage, e.Region, e.CorrelationId)));
        Assert.Equal("DataZoneStandard", events.Single(e => e.Meter == "outputTokens").DeploymentType);
    }

    [Fact]
    public void Cost_rolls_up_from_the_price_table()
    {
        var events = CuUsage.ToEvents(Classifier, "doc-b", "run-1", "classify", "lossrun_classifier_v1", "eastus2", "DataZoneStandard", "op-9", DateTimeOffset.UnixEpoch);

        var cost = CostCalculator.Compute("doc-b", "run-1", "cu:lossrun_classifier_v1", events, PriceTable.Embedded);

        // 3 pages × $0.005 + 3000 × $0.000001 + 1400 × $0.000000825 + 56 × $0.00000495
        Assert.Equal(0.0194322m, cost.CostUsd);
        Assert.Equal((1, 3, 1400L, 56L), (cost.LlmCalls, cost.Pages, cost.TokensIn, cost.TokensOut));
        Assert.All(cost.Breakdown, l => Assert.NotEqual("missing price", l.PriceSource));
        Assert.Equal("$0.019432 · CU classifier 3 pages · 1 LLM call", CostCalculator.Summary(cost, "CU classifier"));
    }

    [Fact]
    public void Cached_input_tokens_are_billed_as_a_subset_of_input()
    {
        // Measured on the POC AI resource: a repeat classification reported 1,400 input of which 1,280 cached.
        UsageEvent Event(string meter, decimal quantity) =>
            new("doc-c", "run-2", "classify", "AzureOpenAI", meter, quantity, "token", "gpt-5.4-mini", "DataZoneStandard", "eastus2", DateTimeOffset.UnixEpoch, "op-1");

        var cost = CostCalculator.Compute("doc-c", "run-2", "cu:lossrun_classifier_v1",
            [Event("inputTokens", 1400), Event("cachedInputTokens", 1280), Event("outputTokens", 56)], PriceTable.Embedded);

        // 120 uncached × $0.000000825 + 1280 × $0.0000000825 (cached, as billed: brief 13 reconciliation) + 56 × $0.00000495
        Assert.Equal(0.000099m + 0.0001056m + 0.0002772m, cost.CostUsd);
        Assert.Equal((1400L, 56L, 1), (cost.TokensIn, cost.TokensOut, cost.LlmCalls));
    }

    [Fact]
    public void Price_table_entries_cite_their_source()
    {
        Assert.Equal("eastus2", PriceTable.Embedded.Region);
        Assert.All(PriceTable.Embedded.Prices, p =>
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Source));
            Assert.True(DateOnly.TryParseExact(p.RetrievedOn, "yyyy-MM-dd", out _), p.RetrievedOn);
            Assert.True(p.UnitPrice > 0);
        });
    }

    [Fact]
    public async Task Every_cu_call_fires_the_meter_and_stores_the_raw_result()
    {
        var harness = new Harness();

        var result = await harness.Pipeline().IngestAsync("lossrun-native.pdf", Fixture.Bytes("lossrun-native.pdf"));

        var graph = Assert.Single(result.Documents);
        var usage = Assert.Single(harness.Meter.Events);
        Assert.Equal((graph.DocumentId, "layout", "documentPagesStandard", 1m), (usage.DocumentId, usage.Stage, usage.Meter, usage.Quantity));
        Assert.Equal($"extractions/{graph.DocumentId}/layout.json", graph.SourceFile.RawResultBlobPath);
        Assert.True(harness.Store.Items.ContainsKey(graph.SourceFile.RawResultBlobPath!));
        var cost = Assert.Single(result.Costs);
        Assert.Equal((0.005m, 0, 1), (cost.CostUsd, cost.LlmCalls, cost.Pages));
        Assert.Equal("$0.005 · CU layout 1 page · 0 LLM calls", CostCalculator.Summary(cost, "CU layout"));
    }

    [Fact]
    public async Task Bundles_are_classified_once_and_loss_run_segments_become_documents()
    {
        var harness = new Harness();

        var result = await harness.Pipeline().IngestAsync("bundle.pdf", Fixture.Bytes("bundle.pdf"));

        Assert.Equal(["lossrun_classifier_v2"], harness.Cu.Ensured);
        Assert.Equal(["lossrun_classifier_v2"], harness.Cu.Calls.Select(c => c.AnalyzerId)); // no second layout call
        Assert.Equal(2, result.Documents.Count);
        var parent = result.Documents[0];
        var child = result.Documents[1];
        Assert.Equal(3, parent.Pages.Count);
        Assert.Equal($"{parent.DocumentId}-p1-1", child.DocumentId);
        Assert.Equal((parent.DocumentId, 1, 1), (child.SourceFile.ParentDocumentId, child.SourceFile.FirstPage, child.SourceFile.LastPage));
        Assert.Equal([1], child.Pages.Select(p => p.Number));
        Assert.All(child.Cells, c => Assert.Equal(1, c.Page));
        Assert.Equal(child.DocumentId, parent.Segments[0].ChildDocumentId);
        Assert.Equal(parent.DocumentId, Assert.Single(result.Costs).DocumentId); // cost attributed to the uploaded file
        var decision = Assert.Single(result.ClassifierDecisions);
        Assert.False(decision.Skipped);
        Assert.Contains("page 2 does not continue", decision.Reason);
    }

    [Fact]
    public async Task Segments_carry_a_derived_confidence_from_header_resolution()
    {
        var harness = new Harness();

        var result = await harness.Pipeline().IngestAsync("bundle.pdf", Fixture.Bytes("bundle.pdf"));

        var segments = result.Documents[0].Segments;
        Assert.Equal("header-resolution", segments[0].DerivedConfidenceMethod);
        Assert.All(segments, s => Assert.StartsWith("header-resolution", s.DerivedConfidenceMethod));
        Assert.Equal((1.0, "accept"), (segments[0].DerivedConfidence, segments[0].DerivedDecision));
        Assert.Null(segments[1].DerivedDecision); // decisions apply to LossRun segments only
        Assert.Equal("accept", Assert.Single(result.Documents[1].Segments).DerivedDecision); // the child document carries it too
    }

    [Theory]
    [InlineData(true, 0.9, "accept")]
    [InlineData(true, 0.6, "review")]
    [InlineData(true, 0.2, "notLossRun")]
    [InlineData(false, 0.0, "review")] // narrative statement / scan CU read no table on: extracted and reviewed, not dropped (ADR 0009)
    public void A_classified_loss_run_segment_is_dropped_only_on_table_evidence(bool hasTable, double confidence, string expected)
    {
        Assert.Equal(expected, IngestionPipeline.SegmentDecision(hasTable, confidence, new IngestionOptions()));
    }

    [Fact]
    public async Task A_multi_page_single_loss_run_skips_the_classifier()
    {
        var harness = new Harness();
        var twoPages = TwoPageLossRun();

        var result = await harness.Pipeline().IngestAsync("two-page.pdf", twoPages);

        Assert.Equal(["prebuilt-layout"], harness.Cu.Calls.Select(c => c.AnalyzerId));
        var decision = Assert.Single(result.ClassifierDecisions);
        Assert.True(decision.Skipped, decision.Reason);
        Assert.True(decision.HeaderResolution >= 0.8);
    }

    [Fact]
    public void Scanned_pdfs_never_skip_the_classifier()
    {
        var check = FirstPageCheck.Evaluate(Fixture.Bytes("lossrun-scanned.pdf"), CanonicalData.Glossary);

        Assert.False(check.IsSingleLossRun);
        Assert.Contains("no text layer", check.Reason);
    }

    private static byte[] TwoPageLossRun()
    {
        using var input = new MemoryStream(Fixture.Bytes("lossrun-native.pdf"));
        using var source = PdfSharp.Pdf.IO.PdfReader.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        using var output = new PdfSharp.Pdf.PdfDocument();
        output.AddPage(source.Pages[0]);
        output.AddPage(source.Pages[0]);
        using var stream = new MemoryStream();
        output.Save(stream, closeStream: false);
        return stream.ToArray();
    }

    [Fact]
    public async Task Spreadsheet_only_runs_cost_nothing_and_call_nothing()
    {
        var harness = new Harness();

        var result = await harness.Pipeline().IngestAsync("lossrun-stacked.xlsx", Fixture.Bytes("lossrun-stacked.xlsx"));

        Assert.Empty(harness.Cu.Calls);
        Assert.Empty(harness.Meter.Events);
        var cost = Assert.Single(result.Costs);
        Assert.Equal((0m, 0, 0, "spreadsheet-native"), (cost.CostUsd, cost.LlmCalls, cost.Pages, cost.Path));
    }

    [Fact]
    public async Task Small_pdfs_skip_classification_and_options_can_disable_it()
    {
        var harness = new Harness();

        await harness.Pipeline(new IngestionOptions { ClassifyBundles = false }).IngestAsync("bundle.pdf", Fixture.Bytes("bundle.pdf"));

        Assert.Equal(["prebuilt-layout"], harness.Cu.Calls.Select(c => c.AnalyzerId));
        Assert.Empty(harness.Cu.Ensured);
    }

    [Fact]
    public void Graph_round_trips_through_json()
    {
        var graph = MapLayout();

        var json = JsonSerializer.Serialize(graph, CanonicalJson.Options);
        var back = JsonSerializer.Deserialize<DocumentGraph>(json, CanonicalJson.Options)!;

        Assert.Equal(json, JsonSerializer.Serialize(back, CanonicalJson.Options));
        Assert.Contains("\"graphVersion\":\"graph-0.2.0\"", json);
        Assert.Contains("\"kind\":\"pdf\"", json);
    }

    [Fact]
    public void Classifier_definition_is_checked_in_and_uses_the_model_alias()
    {
        var definition = JsonDocument.Parse(AnalyzerDefinitions.Get("lossrun_classifier_v1")).RootElement;

        Assert.Equal("prebuilt-analyzer-completion-mini", definition.GetProperty("models").GetProperty("completion").GetString());
        Assert.True(definition.GetProperty("config").GetProperty("enableSegment").GetBoolean());
        Assert.Equal(["LossRun", "ExperienceModWorksheet", "Payroll", "AcordApplication", "Correspondence", "Other"],
            definition.GetProperty("config").GetProperty("contentCategories").EnumerateObject().Select(p => p.Name));
    }
}

/// <summary>Brief 17 (real samples): header composition drops report-title banners and value cells, keeps group headers.</summary>
public class HeaderCompositionTests
{
    private static List<JsonElement> Cells(string json) => [.. JsonDocument.Parse(json).RootElement.EnumerateArray()];

    [Fact]
    public void A_banner_spanning_the_table_is_not_part_of_each_column_header()
    {
        var cells = Cells("""
            [{"rowIndex":0,"columnIndex":0,"columnSpan":4,"content":"Accident Date Range: 11/01/1999 to 12/23/1999"},
             {"rowIndex":1,"columnIndex":0,"content":"Claim Number"},{"rowIndex":1,"columnIndex":1,"content":"Date of Loss"},
             {"rowIndex":1,"columnIndex":2,"content":"Loss Paid"},{"rowIndex":1,"columnIndex":3,"content":"Loss Reserve"}]
            """);
        Assert.Equal(["Claim Number", "Date of Loss", "Loss Paid", "Loss Reserve"], CuResultMapper.ComposeHeaderTexts(cells, 4));
    }

    [Fact]
    public void A_totals_row_read_as_a_header_row_does_not_leak_its_values_into_the_header()
    {
        var cells = Cells("""
            [{"rowIndex":0,"columnIndex":0,"content":"Paid"},{"rowIndex":0,"columnIndex":1,"content":"Reserve"},{"rowIndex":0,"columnIndex":2,"content":"Incurred"},
             {"rowIndex":1,"columnIndex":0,"content":"$160,005.37"},{"rowIndex":1,"columnIndex":1,"content":"$0.00"},{"rowIndex":1,"columnIndex":2,"content":"12/31/2021"}]
            """);
        Assert.Equal(["Paid", "Reserve", "Incurred"], CuResultMapper.ComposeHeaderTexts(cells, 3));
    }

    [Fact]
    public void A_group_header_over_some_columns_stays()
    {
        var cells = Cells("""
            [{"rowIndex":0,"columnIndex":0,"content":"Claim No"},{"rowIndex":0,"columnIndex":1,"columnSpan":2,"content":"Indemnity"},
             {"rowIndex":0,"columnIndex":3,"columnSpan":2,"content":"Medical"},
             {"rowIndex":1,"columnIndex":1,"content":"Paid"},{"rowIndex":1,"columnIndex":2,"content":"Reserve"},
             {"rowIndex":1,"columnIndex":3,"content":"Paid"},{"rowIndex":1,"columnIndex":4,"content":"Reserve"}]
            """);
        Assert.Equal(["Claim No", "Indemnity Paid", "Indemnity Reserve", "Medical Paid", "Medical Reserve"], CuResultMapper.ComposeHeaderTexts(cells, 5));
    }

    [Fact]
    public void A_form_row_above_the_header_is_dropped_and_logged()
    {
        // Shape of okc-ownrisk-wc-selfins (brief 17, P4): a form's "Label:" row merged into the header. Header strings only;
        // the form's values are placeholders.
        var cells = Cells("""
            [{"rowIndex":0,"columnIndex":0,"content":"Client Name:"},{"rowIndex":0,"columnIndex":1,"columnSpan":2,"content":"Example County"},
             {"rowIndex":0,"columnIndex":3,"columnSpan":2,"content":"Own Risk # 00000"},
             {"rowIndex":1,"columnIndex":0,"content":"Claimant Name"},{"rowIndex":1,"columnIndex":1,"content":"Court claim #"},
             {"rowIndex":1,"columnIndex":2,"content":"Date of Injury/death"},{"rowIndex":1,"columnIndex":3,"content":"Medical Paid to date"},
             {"rowIndex":1,"columnIndex":4,"content":"Indemnity Paid to date"}]
            """);
        var repairs = new List<HeaderRepair>();

        var texts = CuResultMapper.ComposeHeaderTexts(cells, 5, CanonicalData.Glossary, repairs);

        Assert.Equal(["Claimant Name", "Court claim #", "Date of Injury/death", "Medical Paid to date", "Indemnity Paid to date"], texts);
        Assert.Equal(5, repairs.Count);
        Assert.Equal(new HeaderRepair(0, "Client Name: Claimant Name", "Claimant Name", "form-row-dropped"), repairs[0]);
        Assert.Equal("claim.medical.paid", CanonicalData.Glossary.ResolveHeader(texts[3])?.Field);
    }

    [Fact]
    public void A_header_row_with_a_label_cell_is_kept_when_dropping_it_resolves_nothing_more()
    {
        // Shape of axis-cameron-county page 4: the upper header row carries the column labels, one of them ending in ':'.
        var cells = Cells("""
            [{"rowIndex":0,"columnIndex":0,"content":"Claimant"},{"rowIndex":0,"columnIndex":1,"content":"Totals:"},{"rowIndex":0,"columnIndex":2,"content":"Status"},
             {"rowIndex":1,"columnIndex":1,"content":"Expenses Paid"},{"rowIndex":1,"columnIndex":2,"content":""}]
            """);
        var repairs = new List<HeaderRepair>();

        var texts = CuResultMapper.ComposeHeaderTexts(cells, 3, CanonicalData.Glossary, repairs);

        Assert.Equal(["Claimant", "Totals: Expenses Paid", "Status"], texts);
        Assert.Empty(repairs);
    }

    [Fact]
    public void A_merged_cell_of_known_headers_is_split_per_column_and_a_group_header_stays()
    {
        // Shape of sf-hacp-wc-add1 (brief 17, P4): CU merged three adjacent headers into one cell, one per printed line.
        var cells = Cells("""
            [{"rowIndex":0,"columnIndex":0,"content":"Claim Number"},{"rowIndex":0,"columnIndex":1,"columnSpan":3,"content":"Incident Date\nEmployee Name\nState"},
             {"rowIndex":0,"columnIndex":4,"content":"Injury Type"},{"rowIndex":0,"columnIndex":5,"columnSpan":3,"content":"Claim Cost Breakdown"},
             {"rowIndex":1,"columnIndex":5,"content":"Paid"},{"rowIndex":1,"columnIndex":6,"content":"Reserves"},{"rowIndex":1,"columnIndex":7,"content":"Incurred"}]
            """);
        var repairs = new List<HeaderRepair>();

        var texts = CuResultMapper.ComposeHeaderTexts(cells, 8, CanonicalData.Glossary, repairs);

        Assert.Equal(["Claim Number", "Incident Date", "Employee Name", "State", "Injury Type", "Claim Cost Breakdown Paid", "Claim Cost Breakdown Reserves",
            "Claim Cost Breakdown Incurred"], texts);
        Assert.Equal([1, 2, 3], repairs.Select(r => r.Column));
        Assert.All(repairs, r => Assert.Equal("merged-cell-split", r.Reason));
    }

    [Theory]
    [InlineData("Claim Number Claim Type", "claim.claimNumber", "claim.claimType")]
    [InlineData("Claimant Name\nStatus", "claim.claimantName", "claim.status")]
    public void A_header_printing_two_fields_stacked_resolves_to_both(string header, string first, string second)
    {
        // Shape of collin-county-wc (brief 17, P3): one cell, two printed fields.
        string[] headers = [header, "Paid", "Outstanding"];
        var resolution = CanonicalData.Glossary.ResolveTableHeaders(headers);

        var stacked = CuResultMapper.StackedHeaders(headers, resolution, CanonicalData.Glossary);

        Assert.Equal([first, second], stacked[0]);
        Assert.False(stacked.ContainsKey(1));
    }

    [Fact]
    public void A_merged_cell_that_does_not_divide_into_known_headers_is_left_alone()
    {
        var cells = Cells("""
            [{"rowIndex":0,"columnIndex":0,"columnSpan":2,"content":"Claim Number Something Unknown"},{"rowIndex":0,"columnIndex":2,"content":"Status"}]
            """);
        var repairs = new List<HeaderRepair>();

        Assert.Equal(["Claim Number Something Unknown", "Claim Number Something Unknown", "Status"], CuResultMapper.ComposeHeaderTexts(cells, 3, CanonicalData.Glossary, repairs));
        Assert.Empty(repairs);
    }
}
