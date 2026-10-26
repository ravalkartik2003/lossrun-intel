"""Render docs/architecture/flow-diagrams.md to lossrun-flows-explained.pdf."""
import html, pathlib, re
from reportlab.lib.pagesizes import A4
from reportlab.lib import colors
from reportlab.lib.units import mm
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.platypus import SimpleDocTemplate, Paragraph, ListFlowable, ListItem, Table, TableStyle, Spacer
HERE = pathlib.Path(__file__).parent
SRC = HERE.parent / "flow-diagrams.md"; OUT = HERE / "lossrun-flows-explained.pdf"
INK, INK2, LINE, GROUP = "#15233B", "#3C4858", "#C9D1DA", "#EEF2F7"
ss = getSampleStyleSheet()
H1 = ParagraphStyle("h1", parent=ss["Title"], fontSize=16, leading=20, textColor=INK, alignment=0, spaceAfter=6)
H2 = ParagraphStyle("h2", parent=ss["Heading2"], fontSize=12.5, leading=16, textColor=INK, spaceBefore=12, spaceAfter=4)
H3 = ParagraphStyle("h3", parent=ss["Heading3"], fontSize=10.5, leading=14, textColor=INK, spaceBefore=8, spaceAfter=3)
B = ParagraphStyle("b", parent=ss["BodyText"], fontSize=9, leading=12.5, textColor=INK, spaceAfter=4)
LI = ParagraphStyle("li", parent=B, spaceAfter=2); TC = ParagraphStyle("tc", parent=B, fontSize=7.8, leading=10)
def inline(t):
    codes = []
    def keep(m): codes.append(m.group(1)); return f"\x00{len(codes)-1}\x00"
    t = re.sub(r"`(.+?)`", keep, t)
    t = html.escape(t); t = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", t); t = re.sub(r"(?<!\w)\*([^*]+?)\*(?!\w)", r"<i>\1</i>", t)
    return re.sub(r"\x00(\d+)\x00", lambda m: f"<font face='Courier' size='7.8'>{html.escape(codes[int(m.group(1))])}</font>", t)
story, items, numbered, table = [], [], False, []
def flush():
    global items, table
    if items:
        story.append(ListFlowable([ListItem(Paragraph(inline(i), LI), leftIndent=10) for i in items], bulletType="1" if numbered else "bullet", start="1" if numbered else "•", bulletFontSize=8, leftIndent=14)); items = []
    if table:
        rows = [r for r in table if not re.match(r"^\|[-| ]+\|$", r)]
        data = [[Paragraph(inline(c.strip()), TC) for c in r.strip().strip("|").split("|")] for r in rows]
        n = max(len(r) for r in data); w = (A4[0] - 40*mm) / n
        t = Table(data, colWidths=[w]*n, repeatRows=1)
        t.setStyle(TableStyle([("BACKGROUND",(0,0),(-1,0),colors.HexColor(GROUP)),("GRID",(0,0),(-1,-1),0.3,colors.HexColor(LINE)),("VALIGN",(0,0),(-1,-1),"TOP"),("LEFTPADDING",(0,0),(-1,-1),4),("RIGHTPADDING",(0,0),(-1,-1),4),("TOPPADDING",(0,0),(-1,-1),3),("BOTTOMPADDING",(0,0),(-1,-1),3)]))
        story.append(t); story.append(Spacer(1, 6)); table = []
for line in SRC.read_text().splitlines():
    if line.startswith("|"): table.append(line); continue
    if table: flush()
    if line.startswith("# "): flush(); story.append(Paragraph(inline(line[2:]), H1))
    elif line.startswith("## "): flush(); story.append(Paragraph(inline(line[3:]), H2))
    elif line.startswith("### "): flush(); story.append(Paragraph(inline(line[4:]), H3))
    elif line.strip() == "---": flush()
    elif re.match(r"^\s*- ", line):
        if items and numbered: flush()
        numbered = False; items.append(re.sub(r"^\s*- ", "", line))
    elif re.match(r"^\d+\. ", line):
        if items and not numbered: flush()
        numbered = True; items.append(re.sub(r"^\d+\. ", "", line))
    elif line.strip() == "": flush()
    else:
        if items: items[-1] += " " + line.strip()
        else: story.append(Paragraph(inline(line), B))
flush()
def footer(c, d):
    c.saveState(); c.setFont("Helvetica", 7.5); c.setFillColor(colors.HexColor(INK2)); c.drawString(20*mm, 10*mm, "Loss Run Intelligence · flow diagrams explained · code-verified 2026-10-25"); c.drawRightString(A4[0]-20*mm, 10*mm, str(d.page)); c.restoreState()
SimpleDocTemplate(str(OUT), pagesize=A4, leftMargin=20*mm, rightMargin=20*mm, topMargin=18*mm, bottomMargin=18*mm, title="Flow diagrams explained").build(story, onFirstPage=footer, onLaterPages=footer)
print("ok", OUT)
