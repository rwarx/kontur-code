<!--
  One logical change per pull request. Describe the behaviour that changed and
  what you verified — a reviewer should be able to tell whether the change does
  what it claims without re-deriving it from the diff.
-->

## What this changes

<!-- Behaviour, not implementation. What is different for a user after this merges? -->

## Why

<!-- The problem. Link the issue if there is one. -->

## How it was verified

<!-- Be specific: "added X test asserting Y" beats "tests pass". -->

- [ ] `dotnet build AIClient.slnx` — clean (warnings are errors)
- [ ] `dotnet test` — green
- [ ] `cd electron && npm run typecheck` — clean

## Checklist

- [ ] The dependency rule holds: `Domain` and `Application` still target plain `net10.0`, and nothing under `ViewModels` names an Infrastructure type beyond `AddInfrastructure` and `DatabaseInitializer`
- [ ] Any new or changed agent tool declares a risk level, and its schema is exercised by `AgentToolTests`
- [ ] Any change to the agent's safety model adds the check **at the right layer**, not at the call site, and says which gate it sits behind
- [ ] No secret, key, path or conversation content can reach a log line
- [ ] New public behaviour is reflected in `CHANGELOG.md`
- [ ] Documentation updated if a setting, a tool or a rule changed
- [ ] No `node_modules`, `dist/`, `sidecar/` or build output included

## Notes for the reviewer

<!--
  Point at the lines that matter. Especially for a change to containment:
  say plainly which guard was added and why that layer.
-->