#!/usr/bin/env python3
"""tools/prompt-gate.py (brief 20 Answers 3): the learn-prompt artefact and its corpus-run gate.

  --write          recompute docs/evaluation/learn-prompt-artefact.json from config/learn-prompt-artefact.paths; when the
                   hash changes and the previous one never had a corpus run attached, the previous hash (and what it
                   superseded) is kept under "supersedes", so one run covers the chain (brief 31 Answers 3)
  --check          fail when the committed artefact is stale (a tracked file changed without --write)
  --pr <base>      fail when the artefact changed between <base> and HEAD and no corpus v5 results folder changed in the
                   same range carries a run.json whose learnPromptArtefact equals the artefact and whose gitSha is an
                   ancestor of HEAD (the run was made from this branch's code) and is clean (no "+dirty": brief 30
                   item 48). CI runs it on every pull request against the base branch and on every push to main
                   against the commit before the push.

The artefact hashes each tracked file with carriage returns removed (the same bytes on Windows and Linux checkouts) and
combines them as sha256 over "<sha256>  <path>\n" lines sorted by path. LossRun.Evaluation.PromptArtefact computes the
same value for run.json. Exit 1 on any failure, with the reason.
"""
import glob, hashlib, json, os, re, subprocess, sys

ROOT = subprocess.run(["git", "rev-parse", "--show-toplevel"], capture_output=True, text=True, check=True).stdout.strip()
PATHS = os.path.join(ROOT, "config", "learn-prompt-artefact.paths")
ARTEFACT = "docs/evaluation/learn-prompt-artefact.json"
VERSION = "learn-prompt-artefact-0.1.0"
MIN_DOCUMENTS = 332  # corpus v5 (corpus-gen 0.7.0 and later, --v5; brief 22 Answers 1: v4 plus c10)


def tracked():
    files = []
    for line in open(PATHS, encoding="utf-8"):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        matches = sorted(glob.glob(os.path.join(ROOT, line)))
        if not matches:
            sys.exit(f"prompt-gate: {line} matches no file")
        files += [os.path.relpath(m, ROOT).replace(os.sep, "/") for m in matches]
    return sorted(set(files))


def compute():
    hashes = {f: hashlib.sha256(open(os.path.join(ROOT, f), "rb").read().replace(b"\r", b"")).hexdigest() for f in tracked()}
    combined = hashlib.sha256("".join(f"{h}  {f}\n" for f, h in sorted(hashes.items())).encode()).hexdigest()
    prompt = open(os.path.join(ROOT, "src/LossRun.Learning/PromptBuilder.cs"), encoding="utf-8").read()
    version = re.search(r'public const string Version = "([^"]+)"', prompt)
    return {"artefactVersion": VERSION, "promptVersion": version.group(1) if version else None, "sha256": combined, "files": hashes}


def committed():
    path = os.path.join(ROOT, ARTEFACT)
    return json.load(open(path, encoding="utf-8")) if os.path.exists(path) else None


def has_run(sha):
    """A committed results folder carries a corpus run of this artefact."""
    for path in glob.glob(os.path.join(ROOT, "docs", "evaluation", "results", "**", "run.json"), recursive=True):
        try:
            if json.load(open(path, encoding="utf-8")).get("learnPromptArtefact") == sha:
                return True
        except (OSError, ValueError):
            continue
    return False


def git(*args):
    return subprocess.run(["git", *args], capture_output=True, text=True, cwd=ROOT)


def main():
    args = sys.argv[1:]
    if args[:1] == ["--write"]:
        artefact, stored = compute(), committed()
        if stored and stored.get("sha256") != artefact["sha256"]:
            chain = [] if has_run(stored["sha256"]) else [stored["sha256"], *stored.get("supersedes", [])]
        else:
            chain = (stored or {}).get("supersedes", [])
        if chain:
            artefact["supersedes"] = chain
        with open(os.path.join(ROOT, ARTEFACT), "w", encoding="utf-8", newline="\n") as f:
            json.dump(artefact, f, indent=2)
            f.write("\n")
        print(f"prompt-gate: {ARTEFACT} written ({artefact['promptVersion']}, {artefact['sha256'][:12]})")
        return 0
    if args[:1] == ["--check"]:
        artefact, stored = compute(), committed()
        if stored is None or stored.get("sha256") != artefact["sha256"]:
            changed = sorted(f for f, h in artefact["files"].items() if (stored or {}).get("files", {}).get(f) != h)
            print(f"prompt-gate: {ARTEFACT} is stale ({', '.join(changed) or 'missing'} changed). Run python tools/prompt-gate.py --write, "
                  "then attach a corpus v5 run made from this code (docs/operations/tuning-loop.md section 3).")
            return 1
        print(f"prompt-gate: artefact current ({artefact['promptVersion']}, {artefact['sha256'][:12]})")
        return 0
    if args[:1] == ["--pr"] and len(args) == 2:
        base = args[1]
        changed = git("diff", "--name-only", f"{base}...HEAD").stdout.split()
        if ARTEFACT not in changed:
            print("prompt-gate: the learn-prompt artefact is unchanged; no corpus run required")
            return 0
        sha = compute()["sha256"]
        runs = sorted({f for f in changed if re.match(r"docs/evaluation/results/[^/]+/(?:.+/)?run\.json$", f)})
        reasons = []
        for run in runs:
            meta = json.load(open(os.path.join(ROOT, run), encoding="utf-8"))
            git_sha = meta.get("gitSha") or ""
            commit = git_sha.split("+")[0]
            if "+" in git_sha:
                # Brief 30 item 48: "<sha>+dirty" means the run was made from uncommitted changes, so no commit holds the
                # code that produced it.
                reasons.append(f"{run}: gitSha {git_sha} was made from uncommitted changes (+{git_sha.split('+', 1)[1]}); re-run from a clean commit")
            elif meta.get("learnPromptArtefact") != sha:
                reasons.append(f"{run}: learnPromptArtefact {str(meta.get('learnPromptArtefact'))[:12]} is not {sha[:12]}")
            elif (meta.get("documents") or 0) < MIN_DOCUMENTS:
                reasons.append(f"{run}: {meta.get('documents')} documents, not a corpus v5 run (>= {MIN_DOCUMENTS})")
            elif not commit or git("merge-base", "--is-ancestor", commit, "HEAD").returncode != 0:
                reasons.append(f"{run}: gitSha {commit[:12] or '-'} is not an ancestor of HEAD")
            else:
                print(f"prompt-gate: {run} is a corpus v5 run of this artefact ({sha[:12]}, {meta.get('documents')} documents, from {commit[:12]})")
                return 0
        print("prompt-gate: the learn-prompt artefact changed, but no corpus v5 results folder from this code is attached "
              "(docs/operations/tuning-loop.md section 3)." + ("".join(f"\n  {r}" for r in reasons) if reasons else "\n  no results folder changed in this pull request"))
        return 1
    print(__doc__)
    return 2


if __name__ == "__main__":
    sys.exit(main())
