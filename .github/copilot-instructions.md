# Repo conventions for Copilot

This is a solo project. The doc layout is small and opinionated; each file
has exactly one job. Keep it that way.

> Development is **Windows-only** for this repo. The Microsoft Analysis
> Services .NET client (TOM) doesn't load reliably on Linux despite the
> managed-only NuGet shape; macOS is unverified. Use Windows shortcuts
> in editor-binding suggestions (`Ctrl`, `Alt`, `Shift`) and PowerShell
> for terminal commands. The deployed *server* runs on Linux App Service
> (the platform handles the runtime), so cross-platform concerns only
> apply to the dev loop.

## Researching Microsoft products

For questions about Microsoft / Azure / Power BI / Fabric capabilities,
limits, or supported auth shapes, use the `microsoft-learn` MCP server
(configured in `.vscode/mcp.json`, copied locally from
`.vscode/mcp.json.sample` — the real file is gitignored) rather than
guessing or hedging. Prefer
its `microsoft_docs_search` / `microsoft_docs_fetch` tools over generic web
search when grounding claims about Microsoft products.

## Where things live

Root files are GitHub-conventional or visitor-landing-page material;
`docs/` files are reference material you navigate to deliberately.

### Root

| File | Job |
|---|---|
| `README.md` | What this is and how to run it. User-facing. |
| `SPEC.md` | **Public contract only**: tool surface, env vars, wire formats, audit-schema reference. No rationale, no roadmap, no checkboxes. |
| `ROADMAP.md` | What's next. Bullet list. No dates, no done-when checklists. |
| `CONTRIBUTING.md` | How to work in this repo (tests, format, layout). |
| `SECURITY.md` | Vulnerability reporting + hardening notes. |

### `docs/`

| File | Job |
|---|---|
| `docs/architecture.md` | How it's built and why. Code wins on conflict. |
| `docs/audit-schema.md` | Versioned audit-log schema. Bump `schema` when changing fields incompatibly. |
| `docs/deploy-azure.md` | Operator runbook for `azd up` to App Service. Dual-purpose: humans deploy from it, agents are pointed at it from `copilot-instructions.md`. |

Five-file doc budget for `README` + `SPEC` + the three under `docs/` and root.
Don't create new top-level docs without asking. Shipped history lives in
Git / GitHub release notes — there is intentionally no `CHANGELOG.md`.

## Editing rules

1. **SPEC.md is contract.** Touch it only if an external consumer (an MCP
   client, a log shipper, a deploy script) would notice the change. If
   unsure, don't.
2. **When implementation diverges from SPEC, default to updating SPEC
   to match code.** Only do the reverse (change code to match SPEC) when
   SPEC reflects a real commitment to a real consumer that we can't break.
3. **Design rationale goes in `architecture.md`** when it's about how the
   pieces fit together, or in a short code comment when it's local.
4. **Roadmap items get deleted when they ship** — don't leave checked-off
   items. Shipped history lives in Git / GitHub release notes, not in
   the repo.

## What NOT to do

- Don't put planning material (todos, "next we'll", checkboxes) in SPEC.md.
- Don't add design rationale to SPEC.md — it goes in `architecture.md`
  or a short code comment.
- Don't update README for internal changes. README is user-facing.
- Don't expand the doc budget without a conversation.

## Code conventions

- `Directory.Build.props` controls `Nullable=enable`,
  `TreatWarningsAsErrors=true`, analyzers. Don't override per-project.
- `Directory.Packages.props` is the only place package versions live.
  Central package management is on.
- **Stdout is the MCP transport.** Never `Console.WriteLine` (or `Console.Out`)
  in `src/`. Logging goes through `ILogger` (Serilog → stderr + rolling file).
- New tools: method on a `[McpServerToolType]` class under `src/.../Tools/`,
  decorated with `[McpServerTool]` + `[Description]`, delegating to
  `IModelingService` (read) or extending it (write). Tools never touch TOM
  directly.
- Write tools route through the safety pipeline in `ModelingService`:
  validate → audit pre → backup (real writes only) → apply → audit post.
- Tests mirror source layout under `tests/PbiModelingMcp.Tests/`. Live-model
  integration tests go under `tests/PbiModelingMcp.IntegrationTests/` and
  are gated on `PBI__*` env vars.
- Concrete service classes are `internal sealed` (DI binds via interfaces);
  `InternalsVisibleTo` exposes them to tests.

## When in doubt

- Update the smaller, lower-stakes doc first.
- Run `dotnet build` + `dotnet test` + `dotnet format --verify-no-changes`
  before saying you're done.

## Helping a user deploy this to Azure

If a user asks "help me deploy this to Azure" (or similar), follow
[`docs/deploy-azure.md`](../docs/deploy-azure.md). The infra is already
wired up under `infra/main.bicep`, `infra/resources.bicep`, and
`azure.yaml` — do not regenerate it.

## Git on this host

Author works in a VS Code code tunnel where Git Credential Manager
emits diagnostic chatter on stderr (renders red in PowerShell) and
sometimes returns a non-zero exit code even when the underlying
operation succeeded. Without a guard, `git push` / `git commit` can
appear to hang while GCM tries to pop a UI prompt that the agent
terminal can't surface.

Conventions:

- For any `git push` (and `git commit` if it triggers a credential
  refresh), run with both env vars set to disable interactive
  prompting:

  ```powershell
  $env:GIT_TERMINAL_PROMPT=0; $env:GCM_INTERACTIVE='Never'; git push origin main 2>&1
  ```

- Stderr-as-red and a non-zero exit code are **not** failures on their
  own. The signal of success is the ref-update line in the output
  (e.g. `4e341ca..25115a0  main -> main`) and `git status` reporting
  `Your branch is up to date with 'origin/main'`. Confirm with `git
  status` before assuming a push failed.
- Never retry a push by force-pushing or amending unless the user
  explicitly asks; the chatter is noise, not a real error.
