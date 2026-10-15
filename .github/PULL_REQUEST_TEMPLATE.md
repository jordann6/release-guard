## What changed

<!-- One or two sentences. What does this do, not how. -->

## Why

<!-- Link the issue or ADR. If this is an architectural choice, it needs an ADR
     in docs/adr/ before merge, not after. -->

## Author checklist

- [ ] I can explain what every line of this diff does without looking it up
- [ ] Database changes are expand-only, or the `contract-migration` label is applied and the column is unused by the release currently deployed (ADR-0003)
- [ ] The previous release still works against this schema (`n-minus-1` check)
- [ ] New dependencies are pinned and lock files are committed
- [ ] Any `.trivyignore` entry has an `# expires: YYYY-MM-DD` line and a reason
- [ ] No credentials, account IDs, or customer data in the diff or in the commit history

## Rollback

<!-- Releases roll back by promoting the last verified release (ADR-0006). Say if
     anything here would make that unsafe. -->
