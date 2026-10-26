# Pricing memo — Word working copy, PDF for the final package

The analytics work:
- **Word (`.docx`) is the working deliverable.** The underwriter edits it.
- **The final package adds a PDF.**
- **QuestPDF is not used**, because of its license.

The memo is produced in one place, `LossRun.Analytics.Memo` (Open XML SDK, MIT). Three things call it:
- `lossrun report`;
- `GET /quotes/{quoteId}/memo` (UI "Export pricing memo");
- the tests.

A PDF is made from that `.docx` in one of two ways. Both are documented here; option A is implemented.

## A. LibreOffice headless (implemented: `lossrun report --pdf`, pipeline container)

```bash
lossrun report --quote <quoteId> --out out/memo.docx --pdf
```

The command:
1. writes `memo.docx` and `memo.json`;
2. runs `soffice --headless --norestore --convert-to pdf --outdir out out/memo.docx`, which writes `out/memo.pdf`.

To find the binary, it checks in order:
1. the `LOSSRUN_SOFFICE` variable;
2. `C:\Program Files\LibreOffice\program\soffice.exe` on Windows;
3. `soffice` on `PATH`.

Exit code 3 with a message means LibreOffice is missing or the conversion failed. The `.docx` is still written.

**Pipeline container.** The Functions host is Flex Consumption, which can't install LibreOffice. So the conversion runs in a separate container image, `docker/memo-pdf/Dockerfile`:
- based on Debian's `libreoffice-writer-nogui` package and the `lossrun` CLI;
- in Azure, run as a Container Apps job (or locally with Docker) that receives a quote id and writes the PDF next to the DOCX in Blob.

The job isn't wired to the API in the POC (`GET /quotes/{id}/memo?format=pdf` returns 400 and names this step). That wiring belongs to the quote-system integration (ADR 0014), if the client chooses option A.

**License.**
- LibreOffice is MPL-2.0.
- It runs as a separate process, and nothing is linked into our code.
- MPL obligations attach to modified LibreOffice source files, which we don't distribute.

Checked locally: `docker build -t lossrun-memo-pdf docker/memo-pdf`, then converting the sample memo (`docs/analytics/samples/memo.docx`) to `memo.pdf` (ADR 0012).

## B. Word → PDF at the client

The client's underwriters already have Microsoft 365, so Word itself can render the PDF. Its fidelity is the reference anyway.
- **By hand:** File → Save As → PDF (or Export → Create PDF/XPS).
- **Automated in their tenant:** with the memo in SharePoint or OneDrive, Microsoft Graph converts on download: `GET /drives/{drive-id}/items/{item-id}/content?format=pdf`. This needs a delegated or app permission in *their* tenant. We would ask for it only if they want the pipeline to do it.

## Which to use

| | A. LibreOffice headless | B. Word at the client |
|---|---|---|
| Fidelity | Very close; tables and fonts render the same with Public Sans or Liberation fallback. | Reference (Word renders its own format). |
| Runs | In our pipeline (container job) or the CLI. | In the client's M365 tenant. |
| Cost | Container job seconds per memo. | None extra. |
| Dependency | LibreOffice image to patch. | Graph permission (automated) or a person (manual). |

Recommendation:
- **POC: option A**, since it is self-contained and demoable.
- **Production:** whichever the client's document-retention process prefers. If memos are filed in SharePoint, B avoids a second rendering engine.
