"""50 MB mixed batch (brief 13 follow-up): one multipart upload of corpus-v3 files mixing every input type, timed from
the POST to every item terminal. Credentials from LOSSRUN_API_USER / LOSSRUN_API_PASSWORD (never printed)."""
import base64, json, os, sys, time, uuid, urllib.request

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..', '..', '..'))
B = os.environ.get('LOSSRUN_API_BASE', 'https://<poc-host>/api/v1')
AUTH = 'Basic ' + base64.b64encode(f"{os.environ['LOSSRUN_API_USER']}:{os.environ['LOSSRUN_API_PASSWORD']}".encode()).decode()
TARGET = 50 * 1024 * 1024
quote = sys.argv[1]

m = json.load(open(os.path.join(REPO, 'eval-runs', 'corpus-v3', 'manifest.json')))
by = {}
for e in m['entries']:
    if 'password' in e['strata']:
        continue
    by.setdefault(e['inputType'], []).append(e)
# Mix: the small types in full measure first, then 200 dpi scans and faxes, then 300 dpi scans until the batch passes 50 MB.
plan = [('xlsx', 6), ('csv', 6), ('native-pdf', 16), ('scan-150dpi-fax', 10), ('scan-200dpi-rotated-noisy', 14), ('scan-300dpi-clean', 999)]
chosen, size = [], 0
for kind, n in plan:
    for e in sorted(by[kind], key=lambda e: e['id'])[:n]:
        if kind == 'scan-300dpi-clean' and size >= TARGET:
            break
        chosen.append(e); size += e['bytes']
boundary = uuid.uuid4().hex
body = bytearray()
for e in chosen:
    data = open(os.path.join(REPO, 'eval-runs', 'corpus-v3', 'docs', e['file']), 'rb').read()
    body += f'--{boundary}\r\nContent-Disposition: form-data; name="file"; filename="{e["file"]}"\r\nContent-Type: application/octet-stream\r\n\r\n'.encode() + data + b'\r\n'
body += f'--{boundary}\r\nContent-Disposition: form-data; name="quoteId"\r\n\r\n{quote}\r\n--{boundary}--\r\n'.encode()
mix = {}
for e in chosen:
    mix[e['inputType']] = mix.get(e['inputType'], 0) + 1
pages = sum(e['pages'] for e in chosen)
print(f'batch: {len(chosen)} files, {size / 1024 / 1024:.1f} MB of files ({len(body) / 1024 / 1024:.1f} MB request), {pages} pages, mix {mix}', flush=True)

req = urllib.request.Request(f'{B}/uploads', data=bytes(body), method='POST', headers={
    'Authorization': AUTH, 'Content-Type': f'multipart/form-data; boundary={boundary}', 'Idempotency-Key': f'batch50-{quote}'})
t0 = time.time()
with urllib.request.urlopen(req, timeout=600) as r:
    accepted = json.loads(r.read())
t_acc = time.time() - t0
print(f'202 accepted after {t_acc:.1f} s: {accepted["uploadId"]}', flush=True)

first = None
seen = {}
while True:
    time.sleep(3)
    with urllib.request.urlopen(urllib.request.Request(f'{B}/uploads/{accepted["uploadId"]}', headers={'Authorization': AUTH}), timeout=60) as r:
        man = json.loads(r.read())
    now = time.time() - t0
    for it in man['items']:
        if it['status'] in ('processed', 'kept-not-processed', 'unsupported', 'awaiting-review', 'failed') and it['documentId'] not in seen:
            seen[it['documentId']] = (now, it['status'], it.get('routingTier'))
            if first is None and it['status'] == 'processed':
                first = now
    if man['status'] in ('completed', 'awaiting-review', 'failed') or now > 1800:
        break
total = time.time() - t0
statuses = {}
for _, (t, s, tier) in seen.items():
    statuses[f'{s}/{tier}'] = statuses.get(f'{s}/{tier}', 0) + 1
done = sorted(t for t, _, _ in seen.values())
result = {'quote': quote, 'uploadId': accepted['uploadId'], 'files': len(chosen), 'fileMB': round(size / 1024 / 1024, 1), 'requestMB': round(len(body) / 1024 / 1024, 1),
          'pages': pages, 'mix': mix, 'acceptedSeconds': round(t_acc, 1), 'firstProcessedSeconds': round(first, 1) if first else None,
          'p50ItemSeconds': round(done[len(done) // 2], 1) if done else None, 'totalSeconds': round(total, 1), 'uploadStatus': man['status'],
          'items': len(man['items']), 'statuses': statuses}
print(json.dumps(result, indent=1))
json.dump(result, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'result.json'), 'w'), indent=1)
