using System.Text.Json;
using LossRun.Api.Contracts;
using LossRun.Contracts;
using LossRun.Data;
using static LossRun.Chat.Tests.ChatFixture;

namespace LossRun.Chat.Tests;

/// <summary>Turn plumbing: authorization by role and workspace owner, audit and metering, conversations, streaming.</summary>
public class ChatServiceTests
{
    private static ScriptedModel Answering(string text, params string[] refs) =>
        new(new ScriptedModel.Step(Answer: JsonSerializer.Serialize(new { text, citations = refs, notFound = false })));

    [Theory]
    [InlineData(Roles.Applicant, ChatScopes.Org, null, null, 403)]
    [InlineData(Roles.Applicant, ChatScopes.Quote, QuoteA, null, 403)]
    [InlineData(Roles.Underwriter, ChatScopes.Workspace, null, Workspace, 404)] // not the owner: indistinguishable from absent
    [InlineData(Roles.Underwriter, ChatScopes.Quote, "Q-A' or 1 eq 1", null, 400)]
    [InlineData(Roles.Underwriter, "everything", null, null, 400)]
    [InlineData(Roles.Underwriter, ChatScopes.Org, null, null, 403)] // 12 Answers: org-wide is UWManager and Admin only
    [InlineData(Roles.UWManager, ChatScopes.Org, null, null, 200)]
    [InlineData(Roles.Admin, ChatScopes.Org, null, null, 200)]
    public async Task Callers_may_ask_only_in_scopes_their_role_and_ownership_allow(string role, string scope, string? quote, string? ws, int status)
    {
        var f = await CreateAsync();
        var outcome = await f.Service(Answering("x")).RunTurnAsync("uw-1", role, new ChatTurnRequest(scope, "How many claims?", quote, ws));
        Assert.Equal(status, outcome.Status);
    }

    [Fact]
    public async Task A_turn_stops_at_six_tool_calls_and_twelve_chunks_and_says_so()
    {
        var f = await CreateAsync();
        var search = ("search_documents", "{\"query\":\"claims\",\"top\":6}");
        var model = new ScriptedModel(
            new ScriptedModel.Step(Calls: [search, search, search]), // 18 chunks asked for: the third search gets none
            new ScriptedModel.Step(Calls: [("query_claims", "{}"), ("query_claims", "{}"), ("query_claims", "{}"), ("query_claims", "{}")]),
            new ScriptedModel.Step(Answer: "{\"text\":\"Done.\",\"citations\":[],\"notFound\":true}"));

        var outcome = await f.Service(model).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "Everything?", QuoteA));

        var r = outcome.Result!;
        var calls = r.ToolTrace.Where(t => t.Tool != ChatMemory.LastResultTool).ToList(); // brief 24: plus the turn's last-result entry
        Assert.Equal(7, calls.Count); // 3 searches (the third cut to 0 chunks) + 3 claim queries + the refused 7th, all traced
        Assert.Equal(1, r.ToolTrace.Count(t => t.Note?.Contains("chunk cap") == true));
        Assert.Contains("tool-call cap", calls[^1].Note);
        Assert.True(r.ToolTrace.Where(t => t.Tool == "search_documents").Sum(t => t.Results) <= ChatTools.MaxChunksPerTurn);
        Assert.Contains("chunk cap reached", r.CapNote); // the first cap hit names the turn's note
        Assert.Contains(model.ToolResultsSeen, x => x.Contains("cap reached"));
    }

    [Fact]
    public async Task The_workspace_owner_may_ask_until_it_expires()
    {
        var f = await CreateAsync();
        var ok = await f.Service(Answering("1 claim.")).RunTurnAsync(WorkspaceOwner, Roles.Applicant, new ChatTurnRequest(ChatScopes.Workspace, "How many claims?", WorkspaceId: Workspace));
        f.Clock.Now = f.Clock.Now.AddHours(73);
        var expired = await f.Service(Answering("x")).RunTurnAsync(WorkspaceOwner, Roles.Applicant, new ChatTurnRequest(ChatScopes.Workspace, "And now?", WorkspaceId: Workspace));

        Assert.Equal(200, ok.Status);
        Assert.Equal(410, expired.Status);
    }

    [Fact]
    public async Task A_turn_is_audited_metered_priced_and_kept_in_its_conversation()
    {
        var f = await CreateAsync();
        var model = new ScriptedModel(
            new ScriptedModel.Step(Calls: [("query_claims", "{\"filter\":{\"status\":\"open\"}}")]),
            new ScriptedModel.Step(Answer: "{\"text\":\"One open claim, WC-A-001 [C1].\",\"citations\":[\"C1\"],\"notFound\":false}"));
        var events = new List<string>();

        var outcome = await f.Service(model).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "Which claims are open?", QuoteA),
            (name, _) => { events.Add(name); return Task.CompletedTask; });

        var r = outcome.Result!;
        Assert.Equal("One open claim, WC-A-001 [C1].", r.Text);
        var citation = Assert.Single(r.Citations);
        Assert.Equal(("claim", "doc-a1", 1), (citation.Kind, citation.DocumentId, citation.Page));
        Assert.Contains("tool", events);
        Assert.Contains("delta", events);
        Assert.Equal(2, r.Usage.ModelCalls);
        Assert.Equal(2400, r.Usage.InputTokens);
        Assert.True(r.Usage.CostUsd > 0);
        Assert.Contains(r.Usage.Lines, l => l.Service == "AzureOpenAI");
        Assert.Single(r.ToolTrace, t => t.Tool == "query_claims");

        var conversation = await f.Records.ConversationAsync(r.ConversationId);
        Assert.Equal(r.TurnId, Assert.Single(conversation).TurnId);
        var trail = await f.Records.AuditTrailAsync(null, QuoteA, 50);
        Assert.Contains(trail.Entries, e => e.Action == "chat.turn" && e.Rationale == "Which claims are open?");

        // The next turn continues the conversation; a different user cannot.
        var next = await f.Service(Answering("Yes.")).RunTurnAsync("uw-1", Roles.Underwriter,
            new ChatTurnRequest(ChatScopes.Quote, "Is it litigated?", QuoteA, ConversationId: r.ConversationId));
        var stranger = await f.Service(Answering("x")).RunTurnAsync("uw-2", Roles.Underwriter,
            new ChatTurnRequest(ChatScopes.Quote, "Show me that.", QuoteA, ConversationId: r.ConversationId));
        var otherScope = await f.Service(Answering("x")).RunTurnAsync("uw-1", Roles.Underwriter,
            new ChatTurnRequest(ChatScopes.Quote, "And quote B?", QuoteB, ConversationId: r.ConversationId));
        Assert.Equal(200, next.Status);
        Assert.Equal(409, stranger.Status);
        Assert.Equal(409, otherScope.Status);
        Assert.Equal(2, (await f.Records.ConversationAsync(r.ConversationId)).Count);
    }

    [Fact]
    public async Task Not_found_answers_are_flagged()
    {
        var f = await CreateAsync();
        var model = new ScriptedModel(new ScriptedModel.Step(Answer: "{\"text\":\"That is not in these documents.\",\"citations\":[],\"notFound\":true}"));
        var r = (await f.Service(model).RunTurnAsync("uw-1", Roles.Underwriter, new ChatTurnRequest(ChatScopes.Quote, "What is the claimant's shoe size?", QuoteA))).Result!;
        Assert.True(r.NotFound);
        Assert.Empty(r.Citations);
    }

    [Theory]
    [InlineData(new[] { "{\"text\":\"Total is $1,2", "34.50 [M1]\\nNext line \\\"q", "uoted\\\" \\u00e9", "\",\"citations\":[\"M1\"]}" }, "Total is $1,234.50 [M1]\nNext line \"quoted\" é")]
    [InlineData(new[] { "{\"text\"", ":\"a\\", "nb\",\"x\":1}" }, "a\nb")]
    public void The_stream_parser_decodes_the_text_field_across_deltas(string[] deltas, string expected)
    {
        var p = new AnswerStreamParser();
        var text = string.Concat(deltas.Select(p.Push));
        Assert.Equal(expected, text);
    }

    [Fact]
    public void Indexing_builds_one_chunk_per_page_with_the_upload_scope_and_kept_items_too()
    {
        var manifest = new UploadManifest
        {
            UploadId = "up-1", Submission = new SubmissionInfo("Q-9", "Acme", Roles.Underwriter, "uw"),
            Items = [new ManifestItem { DocumentId = "doc-1-p2-2", FileName = "bundle.pdf", Classification = "Payroll", Status = ItemStatuses.KeptNotProcessed, ParentDocumentId = "doc-1", PageRange = "2-2" }],
        };
        var pages = new[] { new GraphPage { Number = 2, Markdown = "Payroll by class 2023: 8810 clerical $1,200,000" }, new GraphPage { Number = 2, Markdown = new string('x', 9000), SheetName = "S" } };

        var chunks = ChatIndexer.Build(manifest, manifest.Items[0], pages, null, null);

        Assert.Equal(3, chunks.Count); // the 9,000-char sheet splits into two parts
        Assert.All(chunks, c => Assert.Equal(("quote", "Q-9", "Payroll"), (c.Scope, c.QuoteId, c.Classification)));
        Assert.Equal(chunks.Count, chunks.Select(c => c.ChunkId).Distinct().Count());
        Assert.Equal(ChatIndexer.ChunkId("doc-1-p2-2", "2", 0), chunks[0].ChunkId); // stable: re-indexing replaces in place
    }
}

/// <summary>Workspace expiry (12 Answers): index, files and extraction JSON go; one workspace.expired audit lists them.</summary>
public class WorkspaceExpiryTests
{
    [Fact]
    public async Task Expiry_removes_the_workspace_files_and_extractions_and_audits_the_list()
    {
        var f = await CreateAsync();
        await f.Store.PutAsync(LossRun.Orchestration.Containers.Uploads, "up-w/lossrun.pdf", BinaryData.FromString("%PDF synthetic"));
        await f.Store.PutAsync(LossRun.Orchestration.Containers.Uploads, "up-a/lossrun.pdf", BinaryData.FromString("%PDF synthetic"));
        var workspaces = new ChatWorkspaces(f.Records, null, f.Clock, 72, (u, ct) => LossRun.Orchestration.WorkspacePurge.UploadAsync(f.Store, u, ct));

        Assert.Equal(0, await workspaces.SweepAsync()); // not yet
        f.Clock.Now = f.Clock.Now.AddHours(73);
        Assert.Equal(1, await workspaces.SweepAsync());

        Assert.Empty(await f.Store.ListAsync(LossRun.Orchestration.Containers.Uploads, "up-w/"));
        Assert.Empty(await f.Store.ListAsync(LossRun.Orchestration.Containers.Extractions, "uploads/up-w/"));
        Assert.NotEmpty(await f.Store.ListAsync(LossRun.Orchestration.Containers.Uploads, "up-a/")); // a quote's upload is untouched
        Assert.NotEmpty(await f.Store.ListAsync(LossRun.Orchestration.Containers.Extractions, "uploads/up-a/"));

        var audit = (await f.Records.AuditTrailAsync(null, null, 100)).Entries.Single(e => e.Action == "workspace.expired");
        Assert.Contains("uploads/up-w/lossrun.pdf", audit.NewValue);
        Assert.Contains("extractions/uploads/up-w/manifest.json", audit.NewValue);
        Assert.NotNull((await f.Records.GetWorkspaceAsync(Workspace))!.DeletedAt);
        Assert.Equal(0, await workspaces.SweepAsync()); // once
    }
}
