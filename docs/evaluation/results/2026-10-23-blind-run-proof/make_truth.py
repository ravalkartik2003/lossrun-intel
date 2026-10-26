"""The fixture's truth CSV in the blind-run format (section 1 of docs/operations/blind-run.md), from the corpus-v3 truth of
the documents the fixture was copied from. Stands in for the data team's export. CORPUS_V3 (default eval-runs/corpus-v3)
may point at another checkout's corpus."""
import json, os, sys

corpus_v3 = os.environ.get('CORPUS_V3', 'eval-runs/corpus-v3')

SOURCES = ['c1-001-native-pdf', 'c1-030-native-pdf-bundle', 'c1-024-native-pdf-locked', 'c1-002-scan-150dpi-fax', 'c2-001-native-pdf',
           'c2-002-scan-200dpi-rotated-noisy', 'c4-001-xlsx', 'c4-002-xlsx', 'c4-003-xlsx', 'c5-001-csv']
rows = ['carrier,policy_number,claim_number,date_of_injury,valuation_date,indemnity_paid,indemnity_reserve,medical_paid,medical_reserve,expense_paid,total_incurred']
value = lambda x: '' if x is None else str(x)
for source in SOURCES:
    d = json.load(open(os.path.join(corpus_v3, 'truth', f'{source}.json')))['document']
    for p in d['policyPeriods']:
        for c in p['claims']:
            money = lambda k, f: (c.get(k) or {}).get(f)
            rows.append(','.join([d['carrierName'], p['policyNumber'], c['claimNumber'], c['dateOfInjury'], d['valuationDate'],
                                  value(money('indemnity', 'paid')), value(money('indemnity', 'reserve')), value(money('medical', 'paid')),
                                  value(money('medical', 'reserve')), value(money('expense', 'paid')), value(money('total', 'incurred'))]))
open(sys.argv[1], 'w').write('\n'.join(rows) + '\n')
