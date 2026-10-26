import { useState } from "react";
import * as Dialog from "@radix-ui/react-dialog";
import { CircleAlert, Flag, X } from "lucide-react";
import { HttpError, sendJson } from "../api/client.ts";

/**
 * Brief 26: "Flag for engineering" — a reviewer's note on a document that looks wrong in a way a correction does not fix
 * (a layout the format cannot read, a total that keeps failing). It joins the investigation queue with the document's
 * rule ids and counts; the note is the audit rationale.
 */
export function FlagForEngineering({ documentId }: { documentId: string }) {
  const [open, setOpen] = useState(false);
  const [note, setNote] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  return (
    <>
      <button type="button" className="lr-btn lr-btn--sm" onClick={() => setOpen(true)}><Flag size={14} aria-hidden="true" />Flag for engineering</button>
      {done ? <span className="lr-meta" role="status">{done}</span> : null}
      <Dialog.Root open={open} onOpenChange={(o) => { setOpen(o); if (!o) { setNote(""); setError(null); } }}>
        <Dialog.Portal>
          <Dialog.Overlay className="dialog__overlay" />
          <Dialog.Content className="dialog lr-panel" aria-describedby="flag-desc">
            <div className="dialog__head">
              <Dialog.Title>Flag for engineering</Dialog.Title>
              <Dialog.Close className="lr-btn lr-btn--sm lr-btn--quiet" aria-label="Close"><X size={16} aria-hidden="true" /></Dialog.Close>
            </div>
            <Dialog.Description id="flag-desc">
              Sends this document to the Needs investigation queue with its failing rules and counts (no values). Your decision on the review is separate.
            </Dialog.Description>
            <form
              className="dialog__form"
              noValidate
              onSubmit={async (e) => {
                e.preventDefault();
                if (note.trim().length < 8) return setError("Say what looks wrong in at least 8 characters.");
                setBusy(true);
                try {
                  const r = await sendJson<{ id: string; count: number }>("POST", `/documents/${encodeURIComponent(documentId)}/flag`, { note: note.trim() });
                  setDone(r.count > 1 ? `Flagged: added to an open case (${r.count} occurrences).` : "Flagged for engineering.");
                  setOpen(false);
                  setNote("");
                } catch (err) {
                  setError(err instanceof HttpError ? err.message : "The flag could not be saved.");
                } finally {
                  setBusy(false);
                }
              }}
            >
              <div className="lr-field">
                <label className="lr-label" htmlFor="flag-note">What looks wrong</label>
                <textarea id="flag-note" className="lr-textarea" value={note} onChange={(e) => setNote(e.target.value)} aria-invalid={!!error} aria-describedby={error ? "flag-err" : undefined} />
                {error ? <p id="flag-err" className="lr-error"><CircleAlert size={14} aria-hidden="true" />{error}</p> : null}
              </div>
              <button type="submit" className="lr-btn lr-btn--primary" disabled={busy}>Flag document</button>
            </form>
          </Dialog.Content>
        </Dialog.Portal>
      </Dialog.Root>
    </>
  );
}
