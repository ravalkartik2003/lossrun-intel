"""Five code-verified flow diagrams in one draw.io file (one page each):
1 Format memory & glossary · 2 Durable orchestration & multi-file sync · 3 Validation engine
4 End to end: new carrier format · 5 End to end: known carrier format.
Run: python3 build_flow_diagrams.py  ->  lossrun-flows.drawio"""
import html, pathlib, xml.sax.saxutils as _sx
class sx:
    @staticmethod
    def escape(s): return _sx.escape(s, {'"': "&quot;"})

OUT = pathlib.Path(__file__).parent / "lossrun-flows.drawio"
INK, INK2, LINE = "#15233B", "#3C4858", "#C9D1DA"
C = dict(proc="#FFFFFF", dec="#FFF4D6", store="#E8F1FB", ext="#F3E8FF", model="#FDE8E8", human="#FFE9D6", ok="#E3F4EA", note="#F7F8F6", lane="#F2F5F9")
S = dict(proc="#15233B", dec="#B4620B", store="#1E5AA8", ext="#5B4BB5", model="#B42318", human="#B4620B", ok="#1F7A4D", note="#C9D1DA", lane="#C9D1DA")

class Page:
    def __init__(self, name):
        self.name, self.cells, self.n = name, [], 2
    def _id(self): self.n += 1; return f"c{self.n}"
    def node(self, key, text, kind, x, y, w=200, h=70, align="center", fs=10):
        shape = {"dec": "rhombus;", "store": "shape=cylinder3;boundedLbl=1;backgroundOutline=1;size=10;", "ext": "", "lane": "", "note": ""}.get(kind, "")
        extra = "dashed=1;" if kind == "ext" else ""
        va = "top" if kind in ("lane", "note") else "middle"
        style = (f"{shape}rounded={0 if kind in ('dec','store','lane') else 1};arcSize=6;whiteSpace=wrap;html=1;fillColor={C[kind]};strokeColor={S[kind]};"
                 f"strokeWidth={1.2 if kind!='lane' else 1};fontColor={INK};fontSize={fs};align={align};verticalAlign={va};spacingLeft=6;spacingRight=6;spacingTop={6 if va=='top' else 0};{extra}"
                 + ("fontStyle=1;" if kind == "lane" else ""))
        cid = self._id(); self.ids = getattr(self, "ids", {}); self.ids[key] = cid
        self.cells.append(f'<mxCell id="{cid}" value="{sx.escape(text)}" style="{style}" vertex="1" parent="1"><mxGeometry x="{x}" y="{y}" width="{w}" height="{h}" as="geometry"/></mxCell>')
        return cid
    def edge(self, a, b, label="", kind="", exit=None, entry=None, pts=None):
        color = {"yes": "#1F7A4D", "no": "#B42318", "model": "#B42318", "dash": INK2}.get(kind, INK2)
        dash = "dashed=1;" if kind in ("dash", "model") else ""
        ex = f"exitX={exit[0]};exitY={exit[1]};exitDx=0;exitDy=0;" if exit else ""
        en = f"entryX={entry[0]};entryY={entry[1]};entryDx=0;entryDy=0;" if entry else ""
        style = f"edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;strokeColor={color};strokeWidth=1.3;endArrow=blockThin;endFill=1;fontSize=9;fontColor={INK2};labelBackgroundColor=#FFFFFF;{dash}{ex}{en}"
        cid = self._id()
        wp = "".join(f'<mxPoint x="{x}" y="{y}"/>' for x, y in (pts or []))
        geo = f'<mxGeometry relative="1" as="geometry">{"<Array as=\"points\">"+wp+"</Array>" if wp else ""}</mxGeometry>'
        self.cells.append(f'<mxCell id="{cid}" value="{sx.escape(label)}" style="{style}" edge="1" parent="1" source="{self.ids[a]}" target="{self.ids[b]}">{geo}</mxCell>')
    def title(self, text, sub, w=2000):
        self.node("_t", text, "note", 20, 10, w, 28, align="left", fs=16)
        self.node("_s", sub, "note", 20, 40, w, 36, align="left", fs=10)
        # override note styling for titles (no border)
        for i in (-2, -1):
            self.cells[i] = self.cells[i].replace(f"strokeColor={S['note']}", "strokeColor=none").replace(f"fillColor={C['note']}", "fillColor=none")
    def legend(self, x, y):
        items = [("proc", "Deterministic step (code, no model)"), ("model", "Model call (metered)"), ("dec", "Decision"), ("store", "Store (SQL · Blob · index)"), ("human", "Human step"), ("ok", "Published / straight-through"), ("ext", "External system")]
        for i, (k, t) in enumerate(items):
            self.node(f"_lg{i}", t, k, x, y + i * 30, 230, 24, fs=9)
    def xml(self):
        return (f'<diagram id="{self.name.replace(" ", "-")}" name="{sx.escape(self.name)}"><mxGraphModel dx="1600" dy="900" grid="1" gridSize="10" page="1" pageWidth="2200" pageHeight="1600" background="#FFFFFF"><root><mxCell id="0"/><mxCell id="1" parent="0"/>'
                + "".join(self.cells) + "</root></mxGraphModel></diagram>")

def L(*lines): return "<br>".join(lines)
def B(t): return f"<b>{t}</b>"
def sm(t): return f'<font style="font-size:8.5px;color:{INK2}">{t}</font>'

pages = []

# ------------------------------------------------------------------ Page 1
p = Page("1 Format memory and glossary"); pages.append(p)
p.title("1 · How a carrier format and the glossary come into existence when a file is uploaded",
        "Code-verified: FormatRouter.cs · Fingerprint.cs · FormatLearner.cs · SpecVerifier.cs · SpecLint.cs · GlossaryAliases.cs · ReferenceData.cs. Thresholds are the values in code/config today.")
p.legend(1760, 90)
# column 1: ingestion & header resolution
p.node("up", L(B("Upload accepted"), sm("POST /api/v1/uploads · uploadId = up-sha256(key)[..20]")), "proc", 40, 100, 240, 54)
p.node("ing", L(B("Ingestion (per file)"), sm("sniff · unlock · text layer / scan dpi"), sm("Content Understanding: layout or classifier (bundle split)"), sm("→ graph.json (pages, words, tables, polygons)")), "model", 40, 180, 240, 84)
p.node("hdr", L(B("Header resolution — no model"), sm("1 exact alias (NormalizeText) → 2 component-sum (A + B) →"), sm("3 component+measure → 4 conditional word → 5 measure-only ×0.8"), sm("seed glossary.json 0.4.3 + ACTIVE learned aliases only"), sm("header repair: form-row drop, merged-cell split; stacked 'A over B'")), "proc", 40, 290, 240, 100)
p.node("cond", L(B("Conditional alias?"), sm("'Loss' → indemnity only if a sibling"), sm("column resolves to claim.medical.*")), "dec", 40, 420, 240, 90)
p.node("amb", L(B("ambiguous-composite"), sm("header left unresolved; review unless"), sm("printed totals tie (R-10/R-12 pass)")), "proc", 40, 540, 240, 64)
p.node("lob", L(B("Scope gates before any learn"), sm("line of business ≠ WC → kept, not processed (ADR 0020)"), sm("loss-analysis / changes-only pages → kept (ADR 0021)"), sm("narrative PDF (0 tables) → review, never learned (ADR 0009)")), "proc", 40, 630, 240, 84)
p.edge("up", "ing"); p.edge("ing", "hdr"); p.edge("hdr", "cond"); p.edge("cond", "amb", "no sibling", "no"); p.edge("hdr", "lob", "", "", exit=(1, 0.5), entry=(1, 0.5), pts=[(300, 340), (300, 672)])
# column 2: fingerprint
p.node("fp", L(B("Fingerprint (free)"), sm("identityKey = sha256(page-1 title/pageHeader, digits stripped)"), sm("headerKey = sha256(first table header cells)"), sm("ShortKey fp:<12>:<12> = escalation dedupe key")), "proc", 340, 100, 260, 84)
p.node("d1", L(B("Exact match?"), sm("identityKey == && headerKey ==,"), sm("not retired; prefer carrierHint ≥0.8")), "dec", 340, 210, 260, 90)
p.node("d2", L(B("Identity match only?"), sm("= drift: header tuple changed")), "dec", 340, 330, 260, 80)
p.node("emb", L(B("Embedding tier (PDF only, 1 embedding)"), sm("text-embedding-3-large on 2,000 chars of page 1 (digits→0)"), sm("cosine ≥ min(adaptive max(0.90, mean−2σ), spec 0.95)"), sm("AND identity-token containment ≥ 0.8")), "model", 340, 440, 260, 84)
p.node("d3", L(B("Score ≥ threshold"), sm("and containment ≥ 0.8?")), "dec", 340, 550, 260, 80)
p.node("rec", L(B("Recovery replay (free)"), sm("replay every non-retired same-kind format with carrier"), sm("hint match or containment ≥ 0.5; rank 3: 0 hard & 0 unmapped,"), sm("2: 0 hard, 1: OCR-only failures; rows>0 required")), "proc", 340, 660, 260, 84)
p.node("d4", L(B("Any usable candidate?")), "dec", 340, 770, 260, 70)
p.edge("lob", "fp", "in scope", "", exit=(1, 0.2), entry=(0, 0.5), pts=[(310, 647), (310, 142)])
p.edge("fp", "d1"); p.edge("d1", "d2", "no", "no"); p.edge("d2", "emb", "no", "no"); p.edge("emb", "d3"); p.edge("d3", "rec", "no (miss)", "no"); p.edge("rec", "d4")
# column 3: replay / delta
p.node("rep", L(B("Replay (0 model calls, deterministic)"), sm("tables qualify at headerMatchMin 0.8 · normalizers · derived sums"), sm("scan OCR-tolerant headers (similarity ≥ 0.8) · every value gets"), sm("lineage: page+polygon or sheet+cell, formatId@version, confidence"), sm("rule catalog R-01…R-32 evaluated")), "proc", 660, 100, 300, 96)
p.node("route", L(B("FormatRouter.Decide — in order"), sm("1 carrier mismatch → Review · 2 no format & narrative → Review"), sm("3 no format → Learn · 4 drift, unmapped>0, soft≤3 → Delta"), sm("5 drift → Learn · 7 no replay → Review · 8 0 rows & no 'no losses' → Review"), sm("9 hard>0 all OCR-level → Review+pre-fill · 10/11 hard=0,unmapped=0,soft≤3 → Accept"), sm("12 unmapped>0, soft≤3 → Delta · 13 else Review"), sm("then: accepted but totals printed & none read → Review; ambiguous composite → Review; Learn & scan<280 dpi → Review")), "dec", 660, 230, 300, 150)
p.node("acc", L(B("ReplayAccepted → published"), sm("field success stats ++ · embedding hit recorded"), sm("alias evidence: RecordVerified(tie = R-10/R-12 non-fail)"), sm("shadow format: cleanReplays++ (3 → active)")), "ok", 660, 410, 300, 84)
p.node("delta", L(B("Delta repair (1 small-model call)"), sm("gpt-5.4-mini · schema column_map_delta · only unmapped headers"), sm("verify → publish v(n+1), inherits prior status"), sm("not clean → one full Learn with prior · scan<280 dpi → review")), "model", 660, 520, 300, 84)
p.node("rev", L(B("Review (document-review)"), sm("replay or best attempt as pre-fill · reviewer decides"), sm("hard failures after re-validation stay visible")), "human", 660, 630, 300, 70)
p.edge("d1", "rep", "yes", "yes", exit=(1, 0.5), entry=(0, 0.5), pts=[(630, 255), (630, 148)])
p.edge("d2", "rep", "yes: replay prior version", "yes", exit=(1, 0.5), entry=(0, 0.7), pts=[(640, 370), (640, 167)])
p.edge("d3", "rep", "yes (hit)", "yes", exit=(1, 0.5), entry=(0, 0.85), pts=[(650, 590), (650, 182)])
p.edge("d4", "rep", "yes: 'recovered'", "yes", exit=(1, 0.5), entry=(0, 0.95), pts=[(655, 805), (655, 191)])
p.edge("rep", "route"); p.edge("route", "acc", "Accept", "yes"); p.edge("route", "delta", "Delta", "", exit=(0.5, 1), entry=(0, 0.5), pts=[(640, 400), (640, 562)]); p.edge("route", "rev", "Review", "no", exit=(0.15, 1), entry=(0, 0.5), pts=[(690, 400), (620, 400), (620, 665)])
p.edge("delta", "acc", "clean", "yes", exit=(1, 0.5), entry=(1, 0.5), pts=[(985, 562), (985, 452)])
# column 4: learn loop
p.node("gate", L(B("LearnGate (Durable entity)"), sm("slots = Learn__MaxConcurrent (Terraform 1, code default 3)"), sm("lease 15 min · denied → durable timer 10 s · if it waited: Route again"), sm("(a document queued behind its own carrier replays the format learned meanwhile)")), "proc", 1020, 100, 320, 84)
p.node("prompt", L(B("Prompt (learn-v11, byte-identical prefix)"), sm("glossary fields + composite/conditional words + ignore list · rule catalog · strict JSON schema"), sm("document: first 3 pages markdown ≤12k chars, header cells with (glossary: field), 8 sample rows"), sm("INSTRUCTION lines from LayoutEvidenceDetector: records-rows/lines, measures-as-rows,"), sm("stacked, claims-with-period-totals · header-block fields (ADR 0023) · prior spec on drift")), "proc", 1020, 210, 320, 100)
p.node("llm", L(B("Model call"), sm("gpt-5.4-mini@2026-03-17 (config/models.json; gpt-5.2 when quota)"), sm("Responses API, strict json_schema, prompt cache key lossrun-learn-v11"), sm("429 retry ≤8 honouring retry-after · metered: in/cached/out tokens")), "model", 1020, 340, 320, 84)
p.node("lint", L(B("Lint autofixes (no model) — SpecLint"), sm("LocatePrintedConstants · RetargetRowLevelColumns · RetargetCodedColumns · MapLookupSubtotals"), sm("RelaxRowKindPunctuation · UnanchorTotalRowKinds · LocateLabelNextDates · MapPeriodDateColumns"), sm("FitNormalizers · KeepPrintedZeros · DropUnnamedComponents · FixPunctuationBoundaries"), sm("RepairStackedParts · RepairMeasureColumns · DeclareAbsentComponents · DeclareStatusFrom · LocateHeaderFields"), sm("+ lint CHECKS → issues (mapped-and-ignored, two columns→one target, const outside allow-list…)")), "proc", 1020, 450, 320, 110)
p.node("ver", L(B("Verifier = replay this spec on this document"), sm("hard failures · unmapped headers · issues · required fields (learn-gate.json 0.3.0)"), sm("totals tie: R-10/R-12 (summary-only: R-26, every amount checked)"), sm("document facts: 'prints no valuation date', 'losses window ≠ policy period'")), "proc", 1020, 590, 320, 84)
p.node("dgate", L(B("Gate passes?"), sm("0 hard · 0 unmapped · 0 issues · claimNumber present · and"), sm("pdf-totals / spreadsheet-totals: nothing missing & tie == true"), sm("spreadsheet-rules: spreadsheets only, coverage ≥ 0.8, carrier from submission")), "dec", 1020, 700, 320, 110)
p.node("iter", L(B("Attempt ≤ 5?"), sm("maxIterations pdf 5 / spreadsheet 5"), sm("(env LOSSRUN_LEARN_MAX_ITERATIONS)")), "dec", 1020, 840, 320, 80)
p.node("exh", L(B("Learn exhausted"), sm("best attempt by penalty → best-attempt.replay.json (reviewer pre-fill)"), sm("finalTier Review · document-review item"), sm("escalation learn-exhausted / delta-exhausted (ADR 0025)")), "human", 1020, 950, 320, 84)
p.edge("route", "gate", "Learn", "", exit=(1, 0.3), entry=(0, 0.5), pts=[(990, 275), (990, 142)])
p.edge("d4", "gate", "no: new carrier", "no", exit=(0.5, 1), entry=(0.5, 1), pts=[(470, 880), (1000, 880), (1000, 1060), (1180, 1060), (1180, 184)])
p.edge("gate", "prompt"); p.edge("prompt", "llm"); p.edge("llm", "lint", "", "model"); p.edge("lint", "ver"); p.edge("ver", "dgate")
p.edge("dgate", "iter", "no → feedback turn", "no"); p.edge("iter", "prompt", "yes", "yes", exit=(1, 0.5), entry=(1, 0.5), pts=[(1370, 880), (1370, 260)]); p.edge("iter", "exh", "no", "no")
# column 5: publish & lifecycle & glossary
p.node("pub", L(B("Publish spec (immutable version)"), sm("formatId = slug-pdf|xlsx|csv · version = max+1 · createdBy learn:model@ver"), sm("fingerprint keys + embedding centroid (1 embedding, running average)"), sm("learning record: model, prompt version, iterations, tokens, verificationGate"), sm("status: SHADOW for a new carrier; delta/relearn inherits prior status")), "ok", 1400, 700, 340, 96)
p.node("st", L(B("Registry"), sm("dbo.Format · dbo.FormatVersion (SpecJson)"), sm("dbo.FormatFieldStats · Blob formats/{id}/{v}.json"), sm("index.json · stats.json · centroid.json")), "store", 1400, 830, 340, 70)
p.node("cf", L(B("Confirm-format review (human)"), sm("learning document + every replay of a shadow format opens one"), sm("'Confirm format and approve document' → active (approval:user)"), sm("'Approve document only' → stays shadow · reject never activates")), "human", 1400, 930, 340, 84)
p.node("act", L(B("Active"), sm("also automatic after 3 clean ReplayAccepted"), sm("(a Review or hard failure resets the count)"), sm("retired versions excluded from matching, kept for lineage")), "ok", 1400, 1040, 340, 70)
p.edge("dgate", "pub", "yes", "yes"); p.edge("pub", "st"); p.edge("st", "cf"); p.edge("cf", "act")
# glossary lifecycle box
p.node("gl", L(B("Glossary alias lifecycle (ADR 0019)"), "", sm("① CANDIDATE — at publish, every column mapping the seed glossary does not already resolve to that field"), sm("   (confidence 0.9, provenance: format, document, model, prompt version, totals tie)"), sm("② EVIDENCE — every ReplayAccepted that uses the alias adds a document + format"), sm("③ ACTIVE — automatic at ≥3 documents across ≥2 formats (code default AliasPolicy; brief 21 asked 5/3 as config — not yet)"), sm("   or reviewer accept / re-map in the confirm-format review (implied accept for untouched candidates)"), sm("④ RESOLVES — only ACTIVE aliases resolve headers, after the seed, for every carrier"), sm("⑤ RETIRED — reviewer reject, or a correction on a value read through it (audit-linked); never re-learned"), sm("Hand base glossary.json (0.4.3): changed only by pull request; it is in the prompt prefix → corpus v5 prompt gate")), "note", 1400, 100, 340, 230, align="left")
p.edge("pub", "gl", "candidate aliases", "dash", exit=(1, 0.3), entry=(1, 1), pts=[(1760, 729), (1760, 330)])
p.edge("acc", "gl", "alias evidence", "dash", exit=(1, 0.5), entry=(0, 0.6), pts=[(1000, 452), (1000, 238)])
p.node("rules1", L(B("Rules that decide where a header mapping lives"), sm("Meaning of a word (true for any carrier) → glossary (global). Where/how this layout prints it → format spec (per fingerprint)."), sm("A header the glossary does not know starts INSIDE the spec as a candidate; it becomes global only with evidence across formats or a reviewer's confirmation."), sm("A header whose meaning depends on the table ('Loss' needs a medical sibling) is a conditional alias: global, with the condition attached."), sm("Replay never proposes anything; learn never publishes without the verifier; a human confirms every new format before it is active (or 3 clean replays do).")), "note", 40, 760, 580, 110, align="left")

# ------------------------------------------------------------------ Page 2
p = Page("2 Durable orchestration and multi-file sync"); pages.append(p)
p.title("2 · Durable Functions + Durable Task Scheduler: one upload with several files, from POST to the quote system",
        "Swimlanes = where the step runs. Orchestrator code is deterministic and replayed from DTS history; every activity is idempotent. Names are the real function/activity/table names.")
lanes = [("Browser", 0), ("HTTP API (Functions)", 1), ("IngestUploadOrchestrator (instance = uploadId)", 2), ("ProcessLossRun sub-orchestration ({uploadId}~{documentId}) ×M", 3), ("Activities", 4), ("Durable Task Scheduler", 5), ("Blob / SQL", 6), ("Service Bus · Event Grid · Quote system", 7)]
LW, LH, LX, LY = 2100, 130, 40, 100
for name, i in lanes:
    p.node(f"lane{i}", name, "lane", LX, LY + i * LH, LW, LH - 4, align="left", fs=11)
def at(lane, col, w=190, h=70): return LX + 180 + col * 215, LY + lane * LH + (LH - 4 - h) / 2, w, h
def N(key, text, kind, lane, col, w=190, h=76):
    x, y, w, h = at(lane, col, w, h); p.node(key, text, kind, x, y, w, h, fs=9)
N("b1", L(B("POST /uploads"), sm("multipart ×N files, quoteId, carrierHint,"), sm("valuationDate · Idempotency-Key ui-<uuid>")), "proc", 0, 0)
N("a1", L(B("CreateUpload"), sm("auth · body cap 100 MB (413 above)"), sm("uploadId = up-sha256(key:user:idemKey)[..20]")), "proc", 1, 0)
N("s1", L(B("Blob uploads/{id}/{file} (no overwrite)"), sm("SQL dbo.Upload status=received"), sm("+ AuditEvent upload.create")), "store", 6, 0)
N("d1", L(B("ScheduleNewOrchestrationInstance"), sm("GetInstance(uploadId) first → existing:true"), sm("instance id = uploadId (dedupe)")), "proc", 5, 1)
N("a2", L(B("202 Accepted"), sm("statusUrl · eventsUrl · existing")), "ok", 1, 1)
N("b2", L(B("SSE GET /uploads/{id}/events"), sm("progress / end · 500 ms poll of DTS"), sm("custom status, ≤200 s then reconnect")), "proc", 0, 2)
N("o1", L(B("ExpandUpload"), sm("status processing · one FileWork per file")), "proc", 2, 2)
N("o2", L(B("Task.WhenAll(ParseFile × N)"), sm("fan-out per file, no explicit cap")), "proc", 2, 3)
N("x1", L(B("ParseFile (parallel)"), sm("sniff · unlock · ZIP expand · spreadsheets native"), sm("CU layout or classifier (async LRO) → segments"), sm("MergeContinuations · LOB · report kind")), "model", 4, 3, h=84)
N("s2", L(B("Blob graph.json, layout/classifier.json"), sm("SQL dbo.UsageEvent per CU meter")), "store", 6, 3)
N("o3", L(B("RecordItems"), sm("manifest items: processing /"), sm("kept-not-processed / unsupported")), "proc", 2, 4)
N("s3", L(B("SQL dbo.UploadItem upsert"), sm("(RMW under UPDLOCK on dbo.Upload)")), "store", 6, 4)
N("o4", L(B("Task.WhenAll(ProcessLossRun × M)"), sm("one sub-orchestration per loss-run document")), "proc", 2, 5)
N("p1", L(B("Route"), sm("fingerprint → tier · routing.json / replay.json")), "proc", 3, 5)
N("p2", L(B("LearnGate.TryAcquire (entity)"), sm("slots Learn__MaxConcurrent · lease 15 min"), sm("denied → durable timer 10 s → retry"), sm("waited → Route again")), "proc", 3, 6, h=84)
N("d2", L(B("Entity LearnGate@learn"), sm("+ durable timers · history")), "store", 5, 6)
N("x2", L(B("Learn / Delta"), sm("model calls metered · publish spec"), sm("replay.json or best-attempt")), "model", 4, 7)
N("p3", L(B("ValidateAndPersist"), sm("rules on every document · finalTier"), sm("reviewKind: document-review | confirm-format")), "proc", 3, 8)
N("s4", L(B("SQL one transaction (idempotent)"), sm("Document · PolicyPeriod · Claim · ExtractedValue"), sm("RuleOutcome · DocumentCost · AuditEvent extraction.persist"), sm("ReviewItem pending · UploadItem · Outbox format.learned")), "store", 6, 8, h=84)
N("o5", L(B("CompleteUpload"), sm("awaiting = count(document-review)"), sm("status completed | awaiting-review")), "proc", 2, 9)
N("s5", L(B("SQL transaction: BumpQuoteRevision"), sm("Outbox upload.completed · quote.revised"), sm("quote.payload ONLY if awaiting == 0")), "store", 6, 9)
N("q1", L(B("OutboxRelay fast path + sweep 30 s"), sm("MessageId = CloudEvent id = Idempotency-Key = outbox id"), sm("Service Bus session = quoteId · dup detection 1 day")), "ext", 7, 9, h=80)
N("o6", L(B("Review wait loop"), sm("WaitForExternalEvent('ReviewDecision')"), sm("vs CreateTimer(started + 7 d)")), "dec", 2, 10, h=84)
N("b3", L(B("POST /reviews/{doc}/decision"), sm("reviewer decides (accept / override / reject)")), "human", 0, 11)
N("a3", L(B("RaiseEventAsync(uploadId,"), sm("'ReviewDecision') · 409 if not pending")), "proc", 1, 11)
N("o7", L(B("ApplyDecision"), sm("DocumentEdits.Apply → revision+1, re-validate"), sm("format approve if promote & not reject")), "proc", 2, 12)
N("s6", L(B("EXEC dbo.ApplyDecision (one round trip)"), sm("UPDLOCK on Document · revision check → 0/1"), sm("Override · AuditEvent · Claim history · RuleOutcome"), sm("ReviewItem decided · Outbox review.decided + quote.revised"), sm("+ quote.payload if last review · BumpQuoteRevision")), "store", 6, 12, h=96)
N("q2", L(B("QuoteCommandDispatch (sessions)"), sm("PUT quotes/{q} · 2xx delivered/replayed"), sm("409 stale-revision → superseded (no retry)"), sm("other 4xx → DLQ · 5xx → redeliver ≤10")), "ext", 7, 12, h=84)
N("q3", L(B("Quote system"), sm("Event Grid quote.revised → fetch"), sm("payload?revision=n · page SSE update")), "ext", 7, 13)
N("o8", L(B("Done"), sm("completed | reviews-expired (7 d → ExpireReviews)")), "ok", 2, 13)
# edges
E = p.edge
E("b1", "a1"); E("a1", "s1"); E("a1", "d1"); E("d1", "a2"); E("a2", "b2", "then"); E("d1", "o1", "start"); E("o1", "o2"); E("o2", "x1"); E("x1", "s2"); E("x1", "o3", "", "", exit=(1, 0.3), entry=(0, 0.5)); E("o3", "s3"); E("o3", "o4"); E("o4", "p1"); E("p1", "p2", "Learn / Delta tier"); E("p2", "d2"); E("p2", "x2"); E("x2", "p3"); E("p1", "p3", "replay tier", "", exit=(1, 0.8), entry=(0, 0.8), pts=[(LX+180+5*215+200, LY+3*LH+100), (LX+180+5*215+200, LY+3*LH+112), (LX+180+8*215-20, LY+3*LH+112)])
E("p3", "s4"); E("p3", "o5"); E("o5", "s5"); E("s5", "q1"); E("o5", "o6"); E("b3", "a3"); E("a3", "o6", "external event"); E("o6", "o7", "decision"); E("o7", "s6"); E("s6", "q2"); E("q2", "q3"); E("o7", "o8", "pending empty"); E("o6", "o8", "timer wins", "no", exit=(0.5, 1), entry=(0, 0.5), pts=[(LX+180+10*215+95, LY+2*LH+118), (LX+180+13*215-20, LY+2*LH+118)])
p.node("n2", L(B("Synchronisation rules"), sm("• Upload status is a stored manifest field; concurrent activities of one upload serialise on dbo.Upload (UPDLOCK, HOLDLOCK)."), sm("• A retried activity is harmless: PersistExtraction returns early if the document exists; RecordItems upserts; OpenReview inserts only if absent; blobs are rewritten identically."), sm("• No Durable retry policies on activities: an activity failure → FailUpload (status failed + unhandled-exception escalation) → orchestration Failed. Retries live lower: model client 8×, EF execution strategy."), sm("• Decisions: dbo.ApplyDecision locks the document row and refuses a stale revision; direct overrides retry ≤5 with jitter; a second decision on the same item → 409 not-pending."), sm("• Idempotency: uploadId from Idempotency-Key or content hash; documentId = sha256(uploadId:fileSha) — so re-POSTing the same package never creates a second upload."), sm("• document-review holds the upload status and the first quote.payload; confirm-format keeps the instance open but neither the status nor the push."), sm("• DTS: Consumption SKU, task hub 'lossrun', Flex Consumption FC1 (2 GB, max 40 instances), always-ready ×1 only in demo mode; functionTimeout 30 min; host.json storageProvider azureManaged.")), "note", 40, LY + 8 * LH + 20, 2100, 150, align="left")

# ------------------------------------------------------------------ Page 3
p = Page("3 Validation engine"); pages.append(p)
p.title("3 · Validation engine: every rule, where it runs, what it causes",
        "Catalog is data (rule-catalog.json 0.11.0, embedded); the evaluator is generic, pure and deterministic. Three-valued logic: a null operand → not applicable, never a failure. Severity: hard blocks straight-through · soft warns · flag routes.")
p.legend(1760, 90)
stages = [
 ("sa", "(a) Ingestion checks — no rules, no model", ["Classifier segment decision: no table → review · conf ≥0.8 accept · ≥0.5 review · else not a loss run", "MergeContinuations (same header, no header block, same carrier) · > 3 segments → escalation", "Line of business ≠ WC → kept, not processed (ADR 0020) · loss-analysis / changes-only → kept (ADR 0021)", "Output: graph.json + manifest items (processing / kept-not-processed + reason / unsupported)"], "proc"),
 ("sb", "(b) Learn gate — before a spec may publish", ["Replay the candidate on its own document → full catalog", "Refuse if: any hard failure · any unmapped header · any lint issue · claim.claimNumber missing", "Refuse if ambiguous composite and totals do not tie", "pdf-totals / spreadsheet-totals: nothing missing (learn-gate.json required fields) and tie == true", "spreadsheet-rules: spreadsheets only, coverage ≥ 0.8, carrier from submission", "summary-only: R-26 ties and EVERY printed amount is covered by a check"], "dec"),
 ("sc", "(c) Validation on every document — replay hits included", ["ReplayEngine.Execute → RuleEvaluator (R-01…R-32) · stored in ResultJson and dbo.RuleOutcome per revision", "FormatRouter.Decide consumes hard/soft/flag counts → tier (see chain)", "ValidateAndPersist re-runs the evaluator on the stored replay", "UI: RulesDock 'N failing of M', failingPaths outlined, click-to-source via affectedFields"], "proc"),
 ("sd", "(d) After an override — re-derive and re-validate", ["Only an 'override' decision may carry overrides (else 400)", "Value set · lineage → manual (rationale, confidence 1.0) · derived sums recomputed when a source changed", "Catalog re-evaluated → revision+1 RuleOutcome rows via dbo.ApplyDecision"], "proc"),
 ("se", "(e) Field status and quote readiness (derived, never stored)", ["Per field: reject → rejected · pending/reopened → unresolved · R-33 required & null → unresolved(R-33)", "manual override → approved (wins over a still-failing rule) · hard failure touching the path → unresolved(ruleId) · else approved", "Readiness: ready iff ≥1 counted document, none pending, 0 unresolved; rejected documents excluded and never block", "verified-only view: non-approved values → null in quote payload and memo; Loss Information cell 'needs input'"], "dec"),
 ("sf", "(f) Escalation sweep — patterns, hourly", ["immediate: learn-exhausted · delta-exhausted · replay-failed-active-format · cost > $0.15 · too-many-segments · unhandled-exception · manual flag", "hourly: same fingerprint reviewed ≥3 in 7 d · one rule on ≥80 % of a carrier's docs (min 5) · review-rate +20 pts vs 30 d · reopened >2", "dedupe (reason, fingerprint) → dbo.Escalation → Needs-investigation screen · Event Grid · alert rule"], "proc"),
]
y = 100
for key, t, lines, kind in stages:
    p.node(key, L(B(t), *[sm("• " + l) for l in lines]), kind if kind != "dec" else "proc", 40, y, 760, 26 + 15 * len(lines), align="left")
    y += 26 + 15 * len(lines) + 18
for a, b in zip([s[0] for s in stages], [s[0] for s in stages][1:]): p.edge(a, b)
# rule groups
groups = [
 ("Per-claim amounts", "hard", ["R-01 paid ≤ incurred (±$1) per component", "R-02 incurred = paid + reserve (not-informative when derived)", "R-03 no negative amounts", "R-05 medical-only → indemnity = 0", "R-20 total = ind+med+exp (soft; hard when expense printed separately)", "R-29 total = ind+med when header declares it (soft/hard)"]),
 ("Dates", "hard", ["R-06 DOI ≤ valuation date", "R-07 DOI ≤ reported (soft for cumulative trauma / CT codes 02,03)", "R-08 DOI within policy period (soft for CT)", "R-21 reported ≤ valuation · closed ≤ valuation · closed ≥ DOI"]),
 ("Identity", "hard", ["R-09 unique claim numbers (normalised)", "R-19 claim number present when incurred > $2,000"]),
 ("Ties to printed totals", "hard", ["R-10 period subtotals = Σ rows (or + kept rows) — also the learn-gate tie", "R-11 period counts tie (claims, open, closed)", "R-12 grand totals = Σ all rows (n/a if any period summary-only)", "R-13 grand counts tie", "R-26 Σ period subtotals = grand total (summary-only gate rule)"]),
 ("Periods", "hard/flag", ["R-14 'no losses' period → 0 rows (per period, ADR 0016)", "R-27 flag: empty period with no statement and no totals", "R-28 flag: 'no losses' statement tied to no period"]),
 ("Soft warnings", "soft", ["R-04 closed → reserve 0", "R-16 reopened → some reserve > 0", "R-22 net < gross needs a subrogation recovery", "R-15 valuation within 100 days of target effective (needs context — n/a in pipeline today)", "R-25 requested policy years present (needs context — n/a today)"]),
 ("Routing / analytics flags", "flag", ["R-17 jurisdiction CA · R-18 line of business WC", "R-23 report lag ≤ 30 d (emits lagDays) · R-24 large loss ≥ $100k", "R-30 status printed or verified (unknown → flag)", "R-31 kept rows but no printed total to reconcile", "R-32 valuation date came from the submission, not the page"]),
 ("Quote level (never routes a document)", "hard → readiness", ["R-33 required for quoting: carrier, valuation date, period dates (unless summary-only with label), claim number, DOI, status, total incurred", "→ field status unresolved(R-33) → readiness block → null in payload"]),
]
x, y = 840, 100
for i, (t, sev, lines) in enumerate(groups):
    kind = {"hard": "proc", "soft": "note", "flag": "note", "hard/flag": "proc", "hard → readiness": "dec"}[sev]
    h = 24 + 14 * len(lines)
    p.node(f"g{i}", L(B(f"{t} — {sev}"), *[sm("• " + l) for l in lines]), "proc" if kind == "dec" else kind, x, y, 420, h, align="left")
    y += h + 12
p.node("chain", L(B("Routing chain (FormatRouter.Decide, first match wins)"), sm("1 carrier mismatch → Review"), sm("2 no format & narrative PDF (0 tables) → Review, not learned"), sm("3 no format → Learn (after recovery replay failed)"), sm("4 drift & unmapped>0 & soft≤3 → ReplayWithDelta"), sm("5 drift → Learn (prior = matched spec)"), sm("7 no replay → Review"), sm("8 0 claim rows, no 'no losses', no summary-only period → Review"), sm("9 hard>0 and all failures OCR-level (scans) → Review with pre-fill"), sm("10 hard=0, unmapped=0, soft=0 → ReplayAccepted"), sm("11 hard=0, unmapped=0, soft≤3 → ReplayAccepted + warnings"), sm("12 unmapped>0, soft≤3 → ReplayWithDelta"), sm("13 else → Review (hard with all mapped, or soft>3)"), sm("post: Accepted but format prints totals and none read → Review"), sm("post: ambiguous composite without a clean R-10/R-12 pass → Review"), sm("post: Learn on a scan < 280 dpi → Review (not learned)")), "note", 1290, 100, 440, 260, align="left")
p.node("final", L(B("Final tier and what the user sees"), sm("ReplayAccepted → processed, no review item, 0 LLM calls badge"), sm("Learned (published) → processed + confirm-format item (shadow)"), sm("Delta (published) → processed, version n+1"), sm("Review → awaiting-review item (document-review), upload awaiting-review,"), sm("   quote.payload withheld until the last document-review is decided"), sm("kept-not-processed is decided only at stage (a), never by a rule")), "note", 1290, 380, 440, 120, align="left")
p.node("gaps", L(B("Known gaps found while verifying"), sm("• R-15 and R-25 need a RuleContext that no production path supplies → always n/a"), sm("• R-23's lagDays is emitted but only the eval CLI reads it"), sm("• R-31's condition is re-coded for the manifest reason (agrees today)"), sm("• Routing/gate checks without an R-id: empty replay, totals printed but none read, ambiguous composite, narrative, carrier mismatch, low dpi, OCR-level classification, unmapped headers, lint, coverage")), "note", 1290, 520, 440, 110, align="left")

# ------------------------------------------------------------------ Pages 4/5 helper
def e2e(p, new):
    cols = ["Browser / UI", "HTTP API", "Orchestration", "Ingestion", "Format memory", "Learn", "Validate & persist", "Downstream"]
    steps = []
    def S(col, key, text, kind, h=84):
        steps.append((col, key, text, kind, h))
    S(0, "u", L(B("Upload screen"), sm("POST /uploads (files, quoteId, carrierHint, valuationDate)"), sm("Idempotency-Key ui-<uuid> · then SSE /events")), "proc")
    S(1, "api", L(B("CreateUpload → 202"), sm("uploadId up-… · Blob uploads/{id}/{file}"), sm("dbo.Upload received · AuditEvent upload.create"), sm("start IngestUploadOrchestrator (instance = uploadId)")), "proc")
    S(2, "orc", L(B("ExpandUpload → ParseFile ×N → RecordItems"), sm("→ ProcessLossRun ×M → CompleteUpload"), sm("SSE progress: expand · parse · fingerprint · route · replay|learn · validate · done")), "proc")
    S(3, "ingest", L(B("Ingestion (CU, metered)"), sm("layout (single run) or classifier (bundle split) → graph.json"), sm("header repair · stacked headers · evidence detector · header-block fields"), sm("LOB ≠ WC / analysis / changes-only → kept, not processed · UsageEvent per page")), "model")
    if new:
        S(4, "fp", L(B("Fingerprint MISS"), sm("identity+header keys: no match · no drift"), sm("embedding tier: 1 embedding, cosine < threshold or containment < 0.8"), sm("recovery replay of same-kind formats: no usable candidate"), sm("→ tier Learn · routing.json")), "model")
        S(5, "learn", L(B("Learn (1–5 model calls, gpt-5.4-mini)"), sm("LearnGate slot (Terraform 1) → prompt with evidence → strict JSON spec"), sm("lint autofixes → verifier (replay + catalog + gate) → feedback loop"), sm("measured: p50 40 s, p95 92 s, mean $0.051/document")), "model", 96)
        S(5, "pub", L(B("Publish v1 as SHADOW"), sm("formatId slug-pdf · centroid embedding · learning record"), sm("candidate glossary aliases for unknown headers"), sm("Outbox format.learned · dbo.Format/FormatVersion")), "ok")
        S(6, "val", L(B("ValidateAndPersist"), sm("catalog on the published replay · finalTier Learned"), sm("reviewKind confirm-format (shadow) · ReviewState pending"), sm("SQL txn: Document, PolicyPeriod, Claim, ExtractedValue (lineage per field),"), sm("RuleOutcome, DocumentCost, AuditEvent extraction.persist · chat index queued")), "store", 96)
        S(2, "done", L(B("CompleteUpload"), sm("awaiting = 0 (confirm-format does not hold) → status completed"), sm("BumpQuoteRevision → 1 · Outbox upload.completed, quote.revised, quote.payload"), sm("payload at rev 1: document in excluded[] as pending-review — NO claims yet")), "proc", 96)
        S(0, "rvw", L(B("Confirm-format review (human)"), sm("alias review (candidate → accept / edit / reject)"), sm("'Confirm format and approve document' → promoteToFormat"), sm("'Approve document only' keeps it shadow · reject never activates")), "human", 96)
        S(2, "dec", L(B("ApplyDecision"), sm("revision 2 · re-validate · FormatLifecycle.Approve → ACTIVE"), sm("EXEC dbo.ApplyDecision: Override/AuditEvent review.accept + format.approve,"), sm("ReviewItem decided, Outbox review.decided + quote.revised + quote.payload, BumpQuoteRevision → 2")), "store", 96)
    else:
        S(4, "fp", L(B("Fingerprint HIT (deterministic, free)"), sm("identityKey == and headerKey == on a non-retired version"), sm("0 embeddings · carrierHint disambiguates CSVs sharing a header tuple"), sm("(drift → replay prior; unmapped>0 & soft≤3 → Delta, 1 small call)")), "proc")
        S(4, "rep", L(B("Replay (0 model calls)"), sm("tables qualify ≥0.8 header match · normalizers · derived sums"), sm("lineage per value: page+polygon | sheet+cell, formatId@v, confidence"), sm("catalog R-01…R-32 · hard=0, unmapped=0, soft≤3 → ReplayAccepted"), sm("measured: p50 0.3 s, p95 4.0 s, mean $0.0087/document (PDF $0.0063, spreadsheet $0)")), "ok", 96)
        S(4, "stats", L(B("Format memory side effects"), sm("field success stats ++ · embedding hit recorded"), sm("alias evidence RecordVerified (3 docs / 2 formats → active)"), sm("shadow format: clean replay count (3 → active)")), "proc")
        S(6, "val", L(B("ValidateAndPersist"), sm("catalog re-run on replay.json · finalTier ReplayAccepted · reviewKind none"), sm("SQL txn: Document (ReviewState null), PolicyPeriod, Claim, ExtractedValue,"), sm("RuleOutcome, DocumentCost, AuditEvent extraction.persist · manifest processed")), "store", 96)
        S(2, "done", L(B("CompleteUpload"), sm("awaiting = 0 → status completed · BumpQuoteRevision → 1"), sm("Outbox upload.completed, quote.revised, quote.payload (readiness, changedDocuments,"), sm("quotePayloadUrl ?revision=1) in ONE transaction with the audit row")), "proc", 96)
    S(7, "bus", L(B("OutboxRelay → Service Bus / Event Grid"), sm("fast path + 30 s sweep · MessageId = event id = Idempotency-Key = outbox id"), sm("session = quoteId · duplicate detection 1 day · DLQ after 10")), "ext")
    S(7, "disp", L(B("QuoteCommandDispatch → PUT quotes/{q}"), sm("Idempotency-Key header · 2xx delivered · 409 stale-revision → superseded"), sm("other 4xx → DLQ · 5xx → redeliver")), "ext")
    S(7, "qms", L(B("Quote system (mock)"), sm("stores lossRunAnalysis by highest revision · fetches"), sm("GET /quotes/{q}/quote-payload?revision=n (ETag q-n, 304, 409 superseded)"), sm("Event Grid lossrun.quote.revised also triggers the fetch · page SSE 'revision'")), "ext", 96)
    S(7, "grid", L(B("Loss Information page"), sm("one column per policy period · approved values fill · unresolved 'needs input'"), sm("rejected document → 'excluded' · premium/payroll/ex-mod blank (not from loss run)"), sm("live check: revision 2 visible 1 s after the decision")), "ok")
    S(7, "chat", L(B("Chat index (AI Search lossrun-docs)"), sm("one chunk per page/sheet, original page text only"), sm("embeddings metered to the upload · never overwritten by corrections")), "model")
    # layout: columns x, rows by order
    colx = {i: 40 + i * 245 for i in range(8)}
    y = 100
    for col, key, text, kind, h in steps:
        p.node(key, text, kind, colx[col], y, 230, h, fs=9); y += h + 22
    for i, c in enumerate(cols): p.node(f"h{i}", c, "lane", colx[i], 80, 230, 18, fs=9)
    keys = [s[1] for s in steps]
    for a, b in zip(keys, keys[1:]): p.edge(a, b)
    # SSE back-edge
    p.edge("done", "u", "SSE end · GET /uploads/{id}", "dash", exit=(0, 0.5), entry=(0.5, 1))

p = Page("4 End to end - new carrier format"); pages.append(p)
p.title("4 · End to end, case A: a carrier format never seen before — fingerprint miss → learn → shadow → confirm-format → active → quote system",
        "Model calls: 1 embedding (fingerprint) + 1–5 learn calls + 1 embedding (centroid) + chat-index embeddings. A human confirms the format once; until then the quote payload excludes the document as pending-review.")
e2e(p, True)
p.node("ab", L(B("What the two cases share and where they differ"), sm("Same: ingestion (CU pages metered), validation on every document, lineage row per value, audit on every write, outbox → Service Bus → PUT with idempotency key, Event Grid notification, payload fetched by revision, chat index."), sm("Differs: A spends 1–5 learn calls once per carrier format and opens a confirm-format review; B spends 0 model calls and no human. Learned $0.051 and 40 s p50 vs replay $0.0087 and 0.3 s p50 (results 2026-10-24-learn-loop-quality)."), sm("After case A's approval, every later document of that carrier is case B.")), "note", 40, 1000, 1900, 70, align="left")

p = Page("5 End to end - known carrier format"); pages.append(p)
p.title("5 · End to end, case B: a carrier format already active — fingerprint hit → replay with zero model calls → published → quote system",
        "No human involvement unless a routing rule sends the document to review (hard failure, 0 rows, totals printed but none read, ambiguous composite, carrier mismatch). The quote payload at revision 1 already carries the claims and both feature snapshots.")
e2e(p, False)
p.node("rvw2", L(B("If a rule sends it to review"), sm("document-review item · upload awaiting-review · quote.payload withheld until decided"), sm("reviewer: accept / save changes and approve / reject · overrides → revision+1, re-validate, lineage manual"), sm("then the same downstream chain at the new quote revision; a stale revision at the receiver gets 409 and is marked superseded")), "note", 40, 1000, 1900, 60, align="left")

xml = '<mxfile host="app.diagrams.net">' + "".join(pg.xml() for pg in pages) + "</mxfile>"
OUT.write_text(xml)
print("ok", OUT, len(pages), "pages")
