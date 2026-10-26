# c10 re-run after the replay fix (commit b0ab3d8; corpus v5 c10 slice, 6 documents)

| Input type | Docs | Delivered key-field accuracy | Review rate | Pre-fill key-field accuracy (reviewed docs) | Learn calls / doc | Commitment |
|---|---:|---|---|---|---:|---|
| construct: measures as rows (c10) | 6 | 100.0% [99.6%–100.0%] (n=1314, docs=6) | 0.0% [0.0%–44.3%] (n=6, docs=6) | — (none reviewed) | 0.167 | measured, not committed (construct coverage: first document learned, then replayed); review rate 0.0% |
