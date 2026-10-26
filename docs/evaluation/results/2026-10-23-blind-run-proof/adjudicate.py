"""Scripted adjudication for the brief-14 proof, following blind-run.md section 8 on adjudication.xlsx, as the evaluator would
in Excel. The generator's truth says which document printed each claim. For a claim printed on a run document, the
script picks that Document; every decision is left blank, so the stated defaults apply: archive-correct once a
document is chosen, else exclude (eval truth then refuses the unattributed rows unless --accept-defaults). Run from the
repository root: python adjudicate.py <WORK>. Blind results are under <WORK>/blind/ (brief 30 item 26); CORPUS_V3 (default
eval-runs/corpus-v3) may point at another checkout's corpus."""
import json, os, re, sys, warnings
from datetime import date, datetime

warnings.filterwarnings('ignore', category=UserWarning, module='openpyxl')  # the dropped drop-down validations (see README); keeps local paths out of the log

import openpyxl

work = sys.argv[1]
repo = os.getcwd()
source = {  # fixture file -> corpus-v3 document it was copied from
    'Synthetic Mutual Insurance Co/2024-renewal-loss-run.pdf': 'c1-001-native-pdf', 'Synthetic Mutual Insurance Co/submission-package.pdf': 'c1-030-native-pdf-bundle',
    'Synthetic Mutual Insurance Co/protected-loss-run.pdf': 'c1-024-native-pdf-locked', 'Synthetic Mutual Insurance Co/faxed-loss-run.pdf': 'c1-002-scan-150dpi-fax',
    'Fictional Casualty Group/loss-run.pdf': 'c2-001-native-pdf', 'Fictional Casualty Group/scanned-loss-run.pdf': 'c2-002-scan-200dpi-rotated-noisy',
    'Notional Workers Comp Exchange/claims-export.xlsx': 'c4-001-xlsx', 'Placeholder Assurance Co/claims.csv': 'c5-001-csv',
}
manifest = json.load(open(os.path.join(work, 'corpus', 'manifest.json')))
ran = {f[:-5] for f in os.listdir(os.path.join(work, 'blind', 'results', 'build', 'docs'))}
corpus_v3 = os.environ.get('CORPUS_V3', os.path.join(repo, 'eval-runs', 'corpus-v3'))
digits = lambda s: re.sub(r'\D', '', str(s))
owner = {}  # (date of injury, claim digits) -> file of a run document that printed it
for e in manifest['entries']:
    if e['id'] in ran and e['file'] in source:
        truth = json.load(open(os.path.join(corpus_v3, 'truth', source[e['file']] + '.json')))
        for p in truth['document']['policyPeriods']:
            for c in p['claims']:
                owner[(c['dateOfInjury'], digits(c['claimNumber']))] = e['file']

path = os.path.join(work, 'blind', 'align', 'build', 'adjudication.xlsx')
wb = openpyxl.load_workbook(path)
ws = wb['Adjudication']
counts = {}
for row in ws.iter_rows(min_row=2):
    situation, doi, claim = row[1].value, row[6].value, row[5].value
    if situation is None:
        break
    doi = doi.date().isoformat() if isinstance(doi, (datetime, date)) else str(doi)[:10]
    if situation == 'In the archive, not extracted' and (doi, digits(claim)) in owner:
        row[2].value = owner[(doi, digits(claim))]  # the Document drop-down
        counts['attributed to a document'] = counts.get('attributed to a document', 0) + 1
    else:
        counts[situation] = counts.get(situation, 0) + 1
wb.save(path)
print('adjudication.xlsx filled:', counts)
