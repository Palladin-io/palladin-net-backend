---
name: pr-review
description: Reviews a pull request in the Palladin .NET 10 backend for architecture compliance, code quality, performance, security, and stability. Posts findings as a structured GitHub PR comment.
argument-hint: <pr-number>
disable-model-invocation: true
allowed-tools: Read Grep Glob Bash(gh pr view *) Bash(gh pr diff *) Bash(gh api *) Bash(gh repo *) Bash(git log *)
effort: high
---

# PR Review — Palladin .NET Backend

`PR_NUMBER` below is a symbolic placeholder. Parse the PR number from the explicit user request and replace the placeholder in every command before executing it. Never guess a PR number.

Before reviewing, materialize the context files referenced later in this workflow:

```bash
REPO=$(gh repo view --json nameWithOwner --jq '.nameWithOwner')
gh pr view $PR_NUMBER --json number,title,body,author,additions,deletions,changedFiles,baseRefName,headRefName,reviews > /tmp/pr_reviews.json
gh api "repos/$REPO/pulls/$PR_NUMBER/comments" > /tmp/pr_inline_comments.json
gh pr diff $PR_NUMBER > /tmp/pr_diff.patch
```

Fail the review setup if any command above fails; never continue with missing or stale context files.

## Pull Request Context

**Metadata:**
- Run: `gh pr view $PR_NUMBER --json number,title,body,author,additions,deletions,changedFiles,baseRefName,headRefName 2>/dev/null || echo "PR metadata unavailable"`

**Changed files:**
- Run: `gh pr diff $PR_NUMBER --name-only 2>/dev/null || echo "No changed files"`

**Diff:**
- Read `/tmp/pr_diff.patch`, materialized above. If it is too large for one read, inspect it in bounded chunks rather than fetching it again.

---

## How to Conduct the Review

0. **Sprawdź poprzednie komentarze** — zanim przejdziesz do nowego kodu, przeczytaj `/tmp/pr_reviews.json` i `/tmp/pr_inline_comments.json`. Dla każdego wątku REQUEST_CHANGES: ustal czy problem został zaadresowany w aktualnym diffie. Zanotuj co naprawiono, co wisi.
1. Read `AGENTS.md` — it is the source of truth for all conventions in this project.
2. Load [criteria.md](criteria.md) — it contains the detailed review checklist. Read it fully before starting.
3. For each changed file: use `Read`, `Grep`, `Glob` to explore beyond the diff when context is needed. Cross-reference with unchanged files that are touched by the change (e.g. module registrations, consumers, domain entities).
4. Cite **file path and line number** for every issue you raise.
5. Be concrete — one clear sentence per finding beats a paragraph.

## Review Focus Areas

Cover all sections from `criteria.md`:
- Vertical Slice Architecture & module boundaries
- Code quality: DRY, SRP, OCP, Clean Code
- Performance (N+1, async patterns, EF projections)
- Security (authorization, validation, sensitive data in logs)
- Stability (error handling, idempotency in consumers)
- Domain & analytics conventions (domain events, analytics via events, permissions, queues)
- Tests (naming, structure, coverage)
- Over-engineering check

## Output

Submit a proper GitHub pull request review — inline file comments + a final verdict. Do NOT use `gh pr comment`.

### Step 0 — obsłuż poprzednie komentarze

Dla każdego wątku z poprzednich review (`/tmp/pr_reviews.json`, `/tmp/pr_inline_comments.json`):

**Jeśli problem został zaadresowany** — odpowiedz na komentarz i rozwiąż wątek:
```bash
REPO=$(gh repo view --json nameWithOwner --jq '.nameWithOwner')
# Odpowiedz na komentarz (COMMENT_ID to .id z pr_inline_comments.json)
gh api "repos/${REPO}/pulls/$PR_NUMBER/comments/{COMMENT_ID}/replies" \
  --method POST --field body="✅ Zaadresowane — [opis co zostało zrobione]."

# Pobierz ID wątku i rozwiąż go (GraphQL node ID, nie databaseId)
gh api graphql -f query='
  query($owner:String!,$repo:String!,$pr:Int!) {
    repository(owner:$owner,name:$repo) {
      pullRequest(number:$pr) {
        reviewThreads(first:50) {
          nodes { id isResolved comments(first:1) { nodes { databaseId } } }
        }
      }
    }
  }
' -f owner="$(echo $REPO | cut -d/ -f1)" \
  -f repo="$(echo $REPO | cut -d/ -f2)" \
  -F pr=$PR_NUMBER \
  --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved==false) | {id, commentId: .comments.nodes[0].databaseId}'

gh api graphql -f query='mutation($id:ID!){resolveReviewThread(input:{threadId:$id}){thread{isResolved}}}' \
  -f id="{THREAD_NODE_ID}"
```

**Jeśli problem NIE został zaadresowany** — wymień go w `body` nowego review z odwołaniem:
```
*(Nierozwiązane z poprzedniego review — [link do komentarza lub cytat])* — [stan + oczekiwane działanie]
```

### Step 1 — determine the verdict

- `REQUEST_CHANGES` — any Critical or Warning findings
- `APPROVE` — only Suggestions / Highlights, or a clean PR
- `COMMENT` — **never use as a fallback for APPROVE**. `github-actions[bot]` with `pull-requests: write` CAN and MUST submit `APPROVE`. Use `COMMENT` only if you literally cannot determine a verdict (e.g. missing context that would require out-of-band knowledge).

### Step 2 — build `/tmp/review.json`

```json
{
  "body": "## 🔍 PR Review — .NET Backend\n\n### Summary\n2–3 sentence verdict.\n\n### ✅ Highlights\n- good pattern noted\n\n*(cross-cutting findings that don't map to a single diff line go here too)*",
  "event": "REQUEST_CHANGES",
  "comments": [
    {
      "path": "src/Module/Features/SomeFeature.cs",
      "line": 42,
      "side": "RIGHT",
      "body": "🚨 **Critical** — one-sentence explanation."
    },
    {
      "path": "src/Module/Features/SomeFeature.cs",
      "line": 17,
      "side": "RIGHT",
      "body": "⚠️ **Warning** — one-sentence explanation."
    }
  ]
}
```

### Step 3 — submit

```bash
REPO=$(gh repo view --json nameWithOwner --jq '.nameWithOwner')
gh api "repos/${REPO}/pulls/$PR_NUMBER/reviews" --method POST --input /tmp/review.json
```

### Rules

- **Inline comments** — only on lines present in the diff (`/tmp/pr_diff.patch`). Findings in unchanged files go in `body`.
- **`line`** — file line number (not diff position). **`side`** — always `"RIGHT"` for added/changed lines.
- **Severity prefix** — start each inline `body` with `🚨 Critical —`, `⚠️ Warning —`, or `💡 Suggestion —`.
- **`body`** — overall Summary + Highlights + any cross-cutting findings (e.g. missing module registration, two files affected by same issue).
- Omit `"comments"` key entirely if there are no file-level findings.
