"""Stakeholder architecture diagram: draw.io XML + PDF from one layout, plus the services table.
Run: python3 build_architecture.py"""
import html, math, pathlib, xml.sax.saxutils as sx
from reportlab.lib.pagesizes import landscape, A3
from reportlab.pdfgen import canvas
from reportlab.lib import colors
from reportlab.platypus import Table, TableStyle, Paragraph
from reportlab.lib.styles import getSampleStyleSheet
from reportlab.lib.units import mm

OUT = pathlib.Path(__file__).parent
INK, INK2, LINE = "#15233B", "#3C4858", "#C9D1DA"
ACTION, VERIFIED, REVIEW, LEARNED = "#1E5AA8", "#1F7A4D", "#B4620B", "#5B4BB5"
SURF, RAISED, GROUP = "#F7F8F6", "#FFFFFF", "#EEF2F7"
TITLE = "Loss Run Intelligence — proof-of-concept architecture on Azure"

# canvas 1900 x 620 (draw.io units)
groups = [  # id, label, x, y, w, h
  ("users", "Users",                      20,  60, 200, 300),
  ("web",   "Web",                        260, 60, 240, 300),
  ("app",   "Application",                540, 60, 280, 500),
  ("ai",    "AI services",                920, 60, 380, 230),
  ("data",  "Data",                       920, 330, 380, 230),
  ("integ", "Integration",                1340, 60, 280, 230),
  ("plat",  "Platform, identity, security", 1340, 330, 280, 230),
  ("client","Client systems (after green light)", 1650, 60, 250, 500),
]
BUS_X = 870
BUS_Y = 310   # corridor between the two service rows
nodes = [  # id, label, x, y, w, h, stroke
  ("uw",  "Underwriters\nworkbench in the browser",            40, 110, 160, 70, INK),
  ("ap",  "Applicants\nsubmission upload",                      40, 220, 160, 70, INK),
  ("wb",  "Workbench web app\nReact · accessible (WCAG 2.1 AA)\nStatic Web Apps\noffline demo mode available", 280, 110, 200, 110, ACTION),
  ("fa",  "Loss Run API & pipeline\nAzure Functions · .NET 10\nDurable orchestrations\nupload · classify · format memory\nreplay / learn · rules · review · chat", 560, 110, 240, 150, INK),
  ("dts", "Durable Task Scheduler\nworkflow state · human-review waits", 560, 300, 240, 70, INK2),
  ("xc",  "Built into every step\ncost meter · cell-level lineage\nappend-only audit ledger", 560, 410, 240, 80, INK2),
  ("cu",  "Microsoft Foundry · Content Understanding\nlayout · classification · bundle split\nper-field confidence + page coordinates", 940, 110, 340, 80, LEARNED),
  ("llm", "Language models (Data Zone · US)\ngpt-5.4-mini: learn a new carrier format once\ntext-embedding-3-large: format fingerprint",  940, 200, 340, 70, LEARNED),
  ("st",  "Blob Storage\noriginal documents (immutable) · extractions\nformat registry · cost exports", 940, 380, 340, 70, INK),
  ("sql", "Azure SQL\nclaims · lineage · ledger audit (append-only)\ntemporal history · Liquibase-style scripts", 940, 455, 340, 62, VERIFIED),
  ("kv",  "Key Vault\nsecrets, no keys in application settings", 940, 525, 340, 28, INK2),
  ("as",  "Azure AI Search\nscoped chat over documents and claims", 1360, 110, 240, 60, INK),
  ("sb",  "Service Bus\ntransactional outbox · idempotent commands", 1360, 180, 240, 50, INK),
  ("eg",  "Event Grid\nevents: upload completed · review decided", 1360, 240, 240, 40, INK),
  ("ent", "Microsoft Entra ID\nroles: Applicant · Underwriter\nUW Manager · Admin · managed identities", 1360, 360, 240, 58, INK2),
  ("mon", "Application Insights · Log Analytics\ntelemetry · p95 latency · cost per document", 1360, 426, 240, 50, INK2),
  ("lt",  "Azure Load Testing\n25 concurrent underwriters · p95 evidence", 1360, 484, 240, 40, INK2),
  ("def", "Defender for Cloud · AI threat protection", 1360, 530, 240, 24, INK2),
  ("pas", "Policy / quote management system\nreceives flags, annotations, memos\nsame adapter as the POC mock", 1670, 110, 210, 90, REVIEW),
  ("arc", "Historical loss-run archive\n50,000 keyed documents\nsealed blind-set evaluation\n(the number that counts)", 1670, 230, 210, 100, REVIEW),
  ("tenant", "Organisation Azure subscription\nenterprise quota · private networking\nretention locks · APIM / WAF\nsame Terraform, same code", 1670, 360, 210, 100, REVIEW),
]
# simple edges (drawn as straight orthogonal lines between neighbours); the bus handles app → services
edges = [
  ("uw","wb",""), ("ap","wb",""), ("wb","fa","HTTPS"),
  ("fa","dts",""),
]
bus_targets = ["ai","data","integ","plat"]   # groups fed by the bus
dashed = []


# one line per component: why it is used and its role in the overall solution
roles = [
 ("Workbench web app", "The one screen for applicants and underwriters: upload, review queue, cell-level trace, analytics, what-if and chat; accessible by design (WCAG 2.1 AA)."),
 ("Static Web Apps", "Hosts the workbench with no servers to run; the offline demo mode lets the UI be shown without the backend."),
 ("Loss Run API & pipeline", "All business logic: classify what was uploaded, recognise the carrier format, replay or learn it, validate against printed totals, write results and audit."),
 ("Durable Task Scheduler", "Holds each document's workflow state so a run can wait for a human review decision and resume exactly where it stopped."),
 ("Built into every step", "Every billable call is metered to the document, every value keeps its page/cell origin, every change lands in an append-only ledger."),
 ("Content Understanding", "Reads PDFs and scans once: page layout, which document is which inside a bundle, and per-field confidence with page coordinates for lineage."),
 ("Language models", "Used only when a carrier format is new or has drifted: learn the layout once, then replay it with zero model calls; embeddings fingerprint formats."),
 ("Blob Storage", "Keeps original uploads immutable as evidence, plus extractions, the learned format registry and cost exports."),
 ("Azure SQL", "System of record: claims, cell-level lineage, append-only ledger audit and temporal history; tamper-evident and queryable."),
 ("Key Vault", "Secrets and connection strings; the application never holds keys in its settings."),
 ("Azure AI Search", "Index over documents and claims so chat can answer questions scoped to one quote, a workspace or all history, with citations."),
 ("Service Bus", "Reliable hand-off of results to the quote system; the outbox pattern means nothing is lost or sent twice."),
 ("Event Grid", "Tells other systems when an upload completes or a review is decided."),
 ("Microsoft Entra ID", "Sign-in and role-based access (Applicant, Underwriter, UW Manager, Admin); managed identities between services, no passwords."),
 ("Application Insights", "Telemetry for latency, errors and cost per document; the evidence behind the acceptance criteria."),
 ("Azure Load Testing", "Proves the 25-concurrent-underwriter target with measured p95 figures."),
 ("Defender for Cloud", "Threat protection on the AI endpoints and the rest of the environment."),
 ("Policy / quote system", "Client-provided: the production target for flags, annotations and memos; the POC uses a mock with the same contract."),
 ("Historical archive", "Client-provided: the sealed blind-set evaluation against keyed documents produces the accuracy number that counts."),
 ("Organisation subscription", "Client-provided: enterprise quota, private networking and retention locks; the same Terraform and code deploy there unchanged."),
]

# ---------- draw.io ----------
def vcell(id_, value, x, y, w, h, style):
    return (f'<mxCell id="{id_}" value="{sx.escape(value)}" style="{style}" vertex="1" parent="1">'
            f'<mxGeometry x="{x}" y="{y}" width="{w}" height="{h}" as="geometry"/></mxCell>')
def ecell(id_, src, tgt, label="", dash=False):
    d = "dashed=1;" if dash else ""
    return (f'<mxCell id="{id_}" value="{sx.escape(label)}" style="edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;{d}strokeColor={INK2};strokeWidth=1.3;fontSize=10;fontColor={INK2};endArrow=blockThin;endFill=1;labelBackgroundColor=#FFFFFF;" edge="1" parent="1" source="{src}" target="{tgt}"><mxGeometry relative="1" as="geometry"/></mxCell>')
cells = ['<mxCell id="0"/>', '<mxCell id="1" parent="0"/>']
cells.append(vcell("title", TITLE, 20, 10, 1200, 30, f"text;html=1;align=left;fontSize=18;fontStyle=1;fontColor={INK};"))
for gid, label, x, y, w, h in groups:
    cells.append(vcell("g_"+gid, label, x, y, w, h, f"rounded=1;arcSize=3;whiteSpace=wrap;html=1;fillColor={GROUP};strokeColor={LINE};fontColor={INK2};fontSize=13;fontStyle=1;verticalAlign=top;align=left;spacingLeft=12;spacingTop=6;"))
for nid, label, x, y, w, h, stroke in nodes:
    cells.append(vcell(nid, label.replace("\n","<br>"), x, y, w, h, f"rounded=1;arcSize=8;whiteSpace=wrap;html=1;fillColor={RAISED};strokeColor={stroke};strokeWidth=1.5;fontColor={INK};fontSize=11;align=center;"))
for i,(a,b,l) in enumerate(edges): cells.append(ecell(f"e{i}", a, b, l))
def pcell(id_, pts, arrow=True, width=1.3):
    (x0,y0),(x1,y1) = pts[0], pts[-1]; mids = "".join(f'<mxPoint x="{x}" y="{y}"/>' for x,y in pts[1:-1])
    arr = "endArrow=blockThin;endFill=1;" if arrow else "endArrow=none;"
    return (f'<mxCell id="{id_}" style="edgeStyle=none;rounded=0;html=1;strokeColor={INK2};strokeWidth={width};{arr}" edge="1" parent="1">'
            f'<mxGeometry relative="1" as="geometry"><mxPoint x="{x0}" y="{y0}" as="sourcePoint"/><mxPoint x="{x1}" y="{y1}" as="targetPoint"/>'
            f'<Array as="points">{mids}</Array></mxGeometry></mxCell>')
G = {g[0]: g for g in groups}
corridor_end = G["integ"][2] + G["integ"][4]/2
cells.append(pcell("bus", [(800, 185), (BUS_X, 185), (BUS_X, BUS_Y), (corridor_end, BUS_Y)], arrow=False, width=2))
for i, g in enumerate(bus_targets):
    _, _, gx, gy, gw, gh = G[g]; cx = gx + gw/2
    cells.append(pcell(f"eb{i}", [(cx, BUS_Y), (cx, gy if gy > BUS_Y else gy + gh)]))
for i,(a,b,l) in enumerate(dashed): cells.append(ecell(f"ed{i}", a, b, l, dash=True))
cells.append(vcell("legend", "Every billable call is metered · every extracted value carries page-and-cell lineage · every change is an append-only audit event · all infrastructure is Terraform.   Amber = provided by the client after green light.", 20, 595, 1860, 25, f"text;html=1;align=left;fontSize=11;fontColor={INK2};"))
xml = ('<mxfile host="app.diagrams.net"><diagram id="lossrun-arch" name="Architecture"><mxGraphModel dx="1400" dy="800" grid="1" gridSize="10" page="1" pageWidth="1930" pageHeight="640" background="#FFFFFF"><root>'
       + "".join(cells) + '</root></mxGraphModel></diagram></mxfile>')
(OUT/"lossrun-architecture.drawio").write_text(xml)

# ---------- services table ----------
rows = [
 ("Capability","POC environment (deployed)","Recommended in the organisation's Azure subscription","Why it differs"),
 ("Document layout, classification, bundle split","Azure AI Content Understanding (GA); inline analyze (preview) for ≤5-page documents to shorten demo latency","Same service, GA async API only; primary + paired region; Private Link; customer-managed keys where supported","The preview inline API has no SLA"),
 ("Language models","gpt-5.4-mini (Data Zone · US) for learning new carrier formats, delta repair, classification and chat; 100K tokens/min; one format learned at a time","gpt-5.2 (Data Zone) for learning, gpt-5.4-mini for delta/classification/chat; 300K tokens/min; three concurrent learns; a dedicated chat deployment with Prompt Shields","Enterprise quota and a second deployment were not available in the POC subscription"),
 ("Embeddings","text-embedding-3-large (Data Zone)","Same","—"),
 ("Orchestration","Azure Functions (Flex Consumption) + Durable Task Scheduler (consumption); one always-ready instance for demos","Same; dedicated scheduler tier; two or more always-ready instances; deployment slots; zone redundancy","Avoids a ~15 s cold start"),
 ("System of record","Azure SQL serverless; ledger (append-only) and temporal tables; Liquibase-format change scripts","Azure SQL General Purpose / Business Critical, zone-redundant, in the application region; ledger digests in a locked immutable store (7-year retention)","The POC database runs in a different region than the application (adds ~50 ms per round trip)"),
 ("Documents and extraction data","Blob Storage with versioning and immutability on originals","Same with locked retention policy, Private Endpoint and customer-managed keys","—"),
 ("Search and chat","Azure AI Search (Basic); chat agent with scope enforced server-side; shared model deployment","Azure AI Search (Standard); dedicated chat deployment with Prompt Shields; Defender for AI alerts routed to the security team","Dedicated deployment pending enterprise quota"),
 ("Integration with quote management","Service Bus with transactional outbox and idempotent commands; Event Grid events; a mock quote system","Same pattern against the real policy/quote system; API Management in front of the API","—"),
 ("Web hosting","Static Web Apps; Entra ID sign-in (basic sign-in available for demos)","Static Web Apps Standard or App Service behind API Management / Application Gateway with WAF; Entra app roles only; private endpoints","—"),
 ("Identity and secrets","Managed identities on every hop; Key Vault; no keys in application settings","Same, plus Privileged Identity Management and Conditional Access","—"),
 ("Observability and testing","Application Insights, Log Analytics, Azure Load Testing","Same, plus alert rules (queue depth, learn failures, drift rate, throttling) and a cost workbook","—"),
 ("Security posture","Defender for Cloud AI threat protection; CORS restricted; default abuse monitoring","Modified abuse monitoring via the Microsoft account team; SOC 2 / ISO 27001 inherited from Azure; private networking end to end","Eligibility is per subscription"),
 ("Cost tracking","Every billable call metered; daily cost export; reconciliation against the bill","Same, with per-environment tags and budget alerts","—"),
 ("Infrastructure as code","Terraform (azurerm, azapi); remote state in Azure Storage","Same, in the organisation's pipeline with environment promotion","—"),
]
md = ["# Azure services — POC environment vs organisation subscription\n", "| " + " | ".join(rows[0]) + " |", "|---|---|---|---|"]
md += ["| " + " | ".join(r) + " |" for r in rows[1:]]
(OUT/"azure-services-table.md").write_text("\n".join(md) + "\n")

# ---------- PDF ----------
pdf = OUT/"lossrun-architecture.pdf"
W, H, DIAG_H = 1190.0, 640.0, 400.0
c = canvas.Canvas(str(pdf), pagesize=(W, H))
scale = min((W - 40) / 1910.0, (DIAG_H - 44) / 570.0)
def P(x, y, h=0): return 20 + x*scale, H - 10 - (y + h)*scale
def hexc(s): return colors.HexColor(s)
centers = {}
def text_block(x, y, w, h, text, size, color, bold_first=False):
    lines = text.split("\n"); lh = size*1.3; total = lh*len(lines)
    px, py = P(x, y, h); cy = py + (h*scale - total)/2 + total - size
    for i, ln in enumerate(lines):
        c.setFont("Helvetica-Bold" if (bold_first and i == 0) else "Helvetica", size); c.setFillColor(hexc(color))
        c.drawCentredString(px + w*scale/2, cy, ln); cy -= lh
c.setFont("Helvetica-Bold", 15); c.setFillColor(hexc(INK)); c.drawString(20, H-24, TITLE)
for gid, label, x, y, w, h in groups:
    px, py = P(x, y, h); c.setFillColor(hexc(GROUP)); c.setStrokeColor(hexc(LINE)); c.setLineWidth(0.8)
    c.roundRect(px, py, w*scale, h*scale, 5, fill=1, stroke=1)
    c.setFillColor(hexc(INK2)); c.setFont("Helvetica-Bold", 9); c.drawString(px+8, py + h*scale - 13, label)
    centers["g_"+gid] = (px, py, w*scale, h*scale)
for nid, label, x, y, w, h, stroke in nodes:
    px, py = P(x, y, h); c.setFillColor(hexc(RAISED)); c.setStrokeColor(hexc(stroke)); c.setLineWidth(1.1)
    c.roundRect(px, py, w*scale, h*scale, 4, fill=1, stroke=1)
    text_block(x, y, w, h, label, 6.8, INK, bold_first=True)
    centers[nid] = (px, py, w*scale, h*scale)
def mid(nid): px, py, w, h = centers[nid]; return px + w/2, py + h/2, px, py, w, h
def arrow(x, y, dx, dy):
    L=6; a=math.atan2(dy,dx); c.line(x,y,x-L*math.cos(a-0.5),y-L*math.sin(a-0.5)); c.line(x,y,x-L*math.cos(a+0.5),y-L*math.sin(a+0.5))
def label_at(x, y, lab):
    if not lab: return
    c.setFont("Helvetica", 6); tw = c.stringWidth(lab, "Helvetica", 6); c.setFillColor(hexc("#FFFFFF")); c.rect(x-tw/2-2, y-2.5, tw+4, 8, fill=1, stroke=0)
    c.setFillColor(hexc(INK2)); c.drawCentredString(x, y, lab)
c.setStrokeColor(hexc(INK2)); c.setLineWidth(0.9)
def connect(a, b, lab="", dash=False):
    c.setDash([3,2] if dash else [])
    acx, acy, ax, ay, aw, ah = mid(a); bcx, bcy, bx, by, bw, bh = mid(b)
    if bx > ax + aw - 1:       # b to the right
        sx_, sy_, ex, ey = ax+aw, acy, bx, bcy; mx = (sx_+ex)/2
        c.line(sx_, sy_, mx, sy_); c.line(mx, sy_, mx, ey); c.line(mx, ey, ex, ey); arrow(ex, ey, 1, 0); label_at(mx, max(sy_, ey)+5 if abs(sy_-ey) > 10 else sy_+6, lab)
    elif ax > bx + bw - 1:     # b to the left
        sx_, sy_, ex, ey = ax, acy, bx+bw, bcy; mx = (sx_+ex)/2
        c.line(sx_, sy_, mx, sy_); c.line(mx, sy_, mx, ey); c.line(mx, ey, ex, ey); arrow(ex, ey, -1, 0); label_at(mx, max(sy_, ey)+5, lab)
    elif by + bh < ay:         # b below (pdf y grows up)
        sx_, sy_, ex, ey = acx, ay, bcx, by+bh; c.line(sx_, sy_, ex, ey); arrow(ex, ey, 0, -1); label_at(sx_+20, (sy_+ey)/2, lab)
    else:                      # b above
        sx_, sy_, ex, ey = acx, ay+ah, bcx, by; c.line(sx_, sy_, ex, ey); arrow(ex, ey, 0, 1); label_at(sx_+20, (sy_+ey)/2, lab)
    c.setDash([])
for a, b, l in edges: connect(a, b, l)
# bus: vertical line right of the application; app → bus; bus → each group's left edge at the group's vertical centre
facx, facy, fax, fay, faw, fah = mid("fa")
bx_pdf, by_pdf = P(BUS_X, BUS_Y); ex_pdf, _ = P(corridor_end, 0)
c.setLineWidth(1.6)
c.line(fax+faw, facy, bx_pdf, facy); c.line(bx_pdf, facy, bx_pdf, by_pdf); c.line(bx_pdf, by_pdf, ex_pdf, by_pdf)
c.setLineWidth(0.9)
for g in bus_targets:
    px, py, w, h = centers["g_"+g]; cx = px + w/2
    if py + h < by_pdf:   # group above the corridor (pdf y grows up): stub goes up into its bottom edge
        c.line(cx, by_pdf, cx, py + h); arrow(cx, py + h, 0, 1)
    else:
        c.line(cx, by_pdf, cx, py); arrow(cx, py, 0, -1)
for a, b, l in dashed: connect(a, b, l, dash=True)
c.setFont("Helvetica", 7.5); c.setFillColor(hexc(INK2))
c.drawString(20, H - DIAG_H + 8, "Every billable call is metered · every extracted value carries page-and-cell lineage · every change is an append-only audit event · all infrastructure is Terraform.   Amber = provided by the client after green light.")
# component roles under the diagram
c.setFont("Helvetica-Bold", 10); c.setFillColor(hexc(INK)); c.drawString(20, H - DIAG_H - 14, "What each component does in the solution")
rs = getSampleStyleSheet()["BodyText"]; rs.fontSize = 7.2; rs.leading = 9
rdata = [[Paragraph(f"<b>{html.escape(n)}</b>", rs), Paragraph(html.escape(r), rs)] for n, r in roles]
half = (len(rdata) + 1) // 2
left_tbl, right_tbl = Table(rdata[:half], colWidths=[120, 445]), Table(rdata[half:], colWidths=[120, 445])
for t in (left_tbl, right_tbl):
    t.setStyle(TableStyle([("VALIGN",(0,0),(-1,-1),"TOP"),("LINEBELOW",(0,0),(-1,-1),0.3,hexc(LINE)),("LEFTPADDING",(0,0),(-1,-1),2),("RIGHTPADDING",(0,0),(-1,-1),6),("TOPPADDING",(0,0),(-1,-1),2.5),("BOTTOMPADDING",(0,0),(-1,-1),2.5)]))
    tw_, th_ = t.wrapOn(c, 565, H)
    t.drawOn(c, 20 if t is left_tbl else 605, H - DIAG_H - 22 - th_)
c.showPage()
# page 2: table
c.setPageSize(landscape(A3)); W2, H2 = landscape(A3)
styles = getSampleStyleSheet(); body = styles["BodyText"]; body.fontSize = 8; body.leading = 10
data = [[Paragraph(f"<b>{html.escape(t)}</b>", body) for t in rows[0]]] + [[Paragraph(html.escape(t), body) for t in r] for r in rows[1:]]
tbl = Table(data, colWidths=[58*mm, 120*mm, 125*mm, 70*mm], repeatRows=1)
tbl.setStyle(TableStyle([("BACKGROUND",(0,0),(-1,0),hexc(GROUP)),("GRID",(0,0),(-1,-1),0.4,hexc(LINE)),("VALIGN",(0,0),(-1,-1),"TOP"),
    ("ROWBACKGROUNDS",(0,1),(-1,-1),[hexc(RAISED),hexc(SURF)]),("LEFTPADDING",(0,0),(-1,-1),5),("RIGHTPADDING",(0,0),(-1,-1),5),("TOPPADDING",(0,0),(-1,-1),4),("BOTTOMPADDING",(0,0),(-1,-1),4)]))
c.setFont("Helvetica-Bold", 15); c.setFillColor(hexc(INK)); c.drawString(20, H2-30, "Azure services — POC environment and the recommended organisation setup")
c.setFont("Helvetica", 9); c.setFillColor(hexc(INK2)); c.drawString(20, H2-46, "Unchanged between POC and production: the code, Terraform, database scripts, format registry, rule and analytics catalogs, and the measurement method.  What changes: quota, networking, retention locks, the learning model, and subscription ownership.")
w_, h_ = tbl.wrapOn(c, W2-40, H2-80); tbl.drawOn(c, 20, H2 - 64 - h_)
c.save()
# pages 3+: the service rationale, built by build_service_rationale.py, merged into the same PDF
import subprocess, sys
subprocess.run([sys.executable, str(OUT/"build_service_rationale.py")], check=True, capture_output=True)
from pypdf import PdfWriter
w = PdfWriter()
for f in (pdf, OUT/"azure-services-rationale.pdf"): w.append(str(f))
with open(pdf, "wb") as fh: w.write(fh)
import shutil; shutil.copy(pdf, OUT.parent.parent/"solution-overview"/"lossrun-architecture.pdf"); shutil.copy(OUT/"azure-services-table.md", OUT.parent.parent/"solution-overview"/"azure-services-table.md")
print("ok")
