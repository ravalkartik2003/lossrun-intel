"""Render a docs/solution-overview/*.md page to PDF with the same styling as the architecture document.
Usage: python3 build_next_steps.py [next-steps-sandbox.md | sandbox-deployment-request.md]  (default: next-steps-sandbox.md)"""
import html, pathlib, re, sys
from reportlab.lib.pagesizes import A4
from reportlab.lib import colors
from reportlab.lib.units import mm
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, ListFlowable, ListItem, Table, TableStyle, Preformatted

SRC = pathlib.Path(__file__).parent.parent.parent / "solution-overview" / (sys.argv[1] if len(sys.argv) > 1 else "next-steps-sandbox.md")
OUT = SRC.with_suffix(".pdf")
INK, INK2 = "#15233B", "#3C4858"
ss = getSampleStyleSheet()
H1 = ParagraphStyle("h1", parent=ss["Title"], fontSize=17, leading=21, textColor=INK, alignment=0, spaceAfter=6)
H2 = ParagraphStyle("h2", parent=ss["Heading2"], fontSize=12.5, leading=16, textColor=INK, spaceBefore=12, spaceAfter=4)
B = ParagraphStyle("b", parent=ss["BodyText"], fontSize=9.6, leading=13.5, textColor=INK, spaceAfter=5)
LI = ParagraphStyle("li", parent=B, spaceAfter=3)
SUB = ParagraphStyle("sub", parent=LI, leftIndent=14)
CODE = ParagraphStyle("code", parent=B, fontName="Courier", fontSize=8.2, leading=10.5, backColor="#F3F5F8", borderPadding=4, leftIndent=4)
TITLE = None

SUBBULLET = "\x00"  # marks a sub-bullet break inside a list item; markup is added after escaping

def inline(t):
    t = html.escape(t).replace(SUBBULLET, "<br/>&nbsp;&nbsp;&nbsp;– ")
    t = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", t)
    t = re.sub(r"`(.+?)`", r"<font face='Courier' size='8.6'>\1</font>", t)
    return t

story, items, numbered, table, code = [], [], False, [], None
def flush():
    global items, table
    if items:
        story.append(ListFlowable([ListItem(Paragraph(inline(i), LI), leftIndent=12) for i in items],
                                  bulletType="1" if numbered else "bullet", start="1" if numbered else "•", bulletFontSize=8, leftIndent=14))
        items = []
    if table:
        rows = [r for r in table if not re.match(r"^\|?\s*-{3,}", r)]
        cells = [[Paragraph(inline(c.strip()), LI) for c in r.strip().strip("|").split("|")] for r in rows]
        n = max(len(r) for r in cells); w = (A4[0] - 40*mm) / n
        t = Table(cells, colWidths=[w]*n)
        t.setStyle(TableStyle([("LINEBELOW", (0,0), (-1,-1), 0.4, colors.HexColor("#C9D1DA")), ("VALIGN", (0,0), (-1,-1), "TOP"),
                               ("LEFTPADDING", (0,0), (-1,-1), 4), ("RIGHTPADDING", (0,0), (-1,-1), 4)]))
        story.append(t); story.append(Spacer(1, 4)); table = []
for line in SRC.read_text(encoding="utf-8").splitlines():
    if code is not None:
        if line.startswith("```"): story.append(Preformatted("\n".join(code), CODE)); story.append(Spacer(1, 4)); code = None
        else: code.append(line)
        continue
    if line.startswith("```"): flush(); code = []
    elif line.startswith("# "): flush(); TITLE = line[2:]; story.append(Paragraph(inline(line[2:]), H1))
    elif line.startswith("## "): flush(); story.append(Paragraph(inline(line[3:]), H2))
    elif line.startswith("|"): table.append(line)
    elif re.match(r"^\s{2,}- ", line) and items: items[-1] += SUBBULLET + line.strip()[2:]
    elif re.match(r"^- ", line):
        if items and numbered: flush()
        numbered = False; items.append(line[2:])
    elif re.match(r"^\d+\. ", line):
        if items and not numbered: flush()
        numbered = True; items.append(re.sub(r"^\d+\. ", "", line))
    elif line.strip() == "": flush()
    else: flush(); story.append(Paragraph(inline(line), B))
flush()

def footer(c, d):
    c.saveState(); c.setFont("Helvetica", 7.5); c.setFillColor(colors.HexColor(INK2))
    c.drawString(18*mm, 10*mm, "Loss Run Intelligence · proof of concept · synthetic data only"); c.drawRightString(A4[0]-18*mm, 10*mm, f"{d.page}"); c.restoreState()
SimpleDocTemplate(str(OUT), pagesize=A4, leftMargin=20*mm, rightMargin=20*mm, topMargin=18*mm, bottomMargin=18*mm,
                  title=TITLE or SRC.stem).build(story, onFirstPage=footer, onLaterPages=footer)
print("ok", OUT)
