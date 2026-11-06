# F&O Studio: product manager review (research only, no files changed)

## Assessment

**Strengths**
- **Close to Visual Studio parity, without Visual Studio.** It has Application Explorer (classic and model views), designers generated from the same metadata classes, `.rnrproj` and `.axpp` files that VS can open, xppc/LabelC/xppbp builds, packaging, and pac deploy and sync. It covers UDE, cloud-hosted and VHD environments.
- **Agent-first with real guardrails.** Writes go through the metadata API, are checked on the server against your own models, and are all-or-nothing. Tools return validation results, large objects are read as a summary first, and `xpp_get_guide` loads the full guide on demand. That is unusual depth for an MCP server.
- **Analysis that Visual Studio does not have:**
  - its own `.etl` trace parser;
  - license and segregation-of-duties analysis;
  - upgrade readiness that checks CoC wrappers and extensions against the installed application;
  - live database insights;
  - Review My Changes and the Pre-Deploy Checklist.

**Three biggest gaps or risks**
1. **Trust in the debug and run loop.** The debugger has never been verified end to end (V2). On a UDE the runtime only sees code after a deploy, yet nothing tells the user whether what they built is what is deployed. Run Tests, the debugger and Run Snippet all depend on that, so failures will look like product bugs.
2. **The team and environment lifecycle is thin compared with the single-developer loop.**
   - Day-one onboarding (clone the team repo and wire its models into the custom metadata folder) is manual.
   - Nothing warns when a UDE has been serviced to a newer version than the local metadata. Old version folders (about 16 GB each) are never cleaned up.
   - Pipeline integration (L4) is still Planned.
   - Teams on TFVC lose Review My Changes, changed elements and history, which all assume git.
3. **Marketplace story and surface.**
   - About 186 commands, about 60 of them "Go to …" variants.
   - The product can't be evaluated without a UDE, and there is no parity statement against Visual Studio.
   - Polish defects hurt first impressions:
     - Two command titles in `package.json` show a literal `?` where `…` was meant: "Create D365FO Project?" and "Show Extensions and Handlers?".
     - The new code folding in the X++ editor isn't in FEATURES.md or the "What's new" list.
   - The agent can change and build code but cannot check the result in the running app.

## New ideas

### PM1. Environment version drift guard and metadata refresh
- **User and job:** UDE developer; keep the box in step with the environment.
- **Problem:** Microsoft or an admin services the UDE to a new application version. Local PackagesLocalDirectory, the cross-reference database and the compiler stay on the old version. Builds still succeed, but deploys fail or behave oddly. Old version folders pile up at about 16 GB each.
- **Proposal:**
  - On activation and in Check Environment, compare `msprov_getfinopsapplicationdetails` `applicationversion` with the selected configuration's version.
  - Show a status bar warning with **Update Metadata**, which re-runs the Connect steps for the new version and keeps the custom folder and projects.
  - Offer **Clean Up Old Versions**: list the old `%LOCALAPPDATA%\Microsoft\Dynamics365\<ver>` folders, cross-reference databases and junctions with their sizes, and delete only after confirmation.
  - Before cleaning up, offer to keep one older version for PM5.
- **Why now:** UDEs are serviced on Microsoft's cadence. Setup already knows every step.
- **Value:** High. **Effort:** M.
- **Success signal:** fewer "deploy failed" and "symbol not found" reports after service updates; disk space recovered.
- **Dependencies or risks:** the Dataverse actions are undocumented (already used). Deleting data needs a careful confirmation and a keep-one-previous option.

### PM2. Clone and wire a team repository
- **User and job:** a new developer on a project, or a lead onboarding people.
- **Problem:** a D365FO repo holds `Metadata\<Package>` folders that must be junctioned into the custom metadata folder, plus projects, labels and model references. Today this is tribal knowledge plus mklink by hand.
- **Proposal:** **Open Team Repository…** does the following:
  1. Clone, or pick a clone.
  2. Detect the package folders (descriptor files).
  3. Create junctions into `ModelStoreFolder` and check that no package name clashes.
  4. Build the models in dependency order.
  5. Offer database sync and the first project.
  6. Optionally write `.xpp/workspace.json` so the next developer gets the same layout.

  `changes.ts` and `history.ts` already resolve junctions, so the rest of the product already supports this layout.
- **Why now:** the walkthrough covers one developer. Team adoption needs the second developer to be productive on day one.
- **Value:** High. **Effort:** M.
- **Success signal:** time from install to first successful build on an existing project; walkthrough completion.
- **Dependencies or risks:** junction permissions; ISV binary packages in the repo; TFVC repos (clone is out of scope, but wiring still works).

### PM3. Deployed-state awareness: "is my code on the environment?"
- **User and job:** UDE developer; test and debug what was just written.
- **Problem:** Run Tests, the debugger and Run Snippet run against deployed code, but nothing tracks it. `needsDeploy()` only infers the environment type.
- **Proposal:**
  - Record the build timestamp and output hash per model. Read the last deployment from Dataverse (`msprov_fnopackage` records).
  - Status bar: "Deployed: current / stale (built 10:42, deployed 09:15)" with **Deploy now**.
  - Run Tests, Launch Debugger and the Pre-Deploy Checklist warn when the code is stale.
  - Agents get the same state through the environment tool output.
- **Why now:** this is the most common silent failure in the UDE loop. It removes false bug reports about tests and the debugger.
- **Value:** High. **Effort:** S–M.
- **Success signal:** fewer test runs or debug sessions against stale code (local statistics); fewer "breakpoint not hit" reports.
- **Dependencies or risks:** the deploy entity's shape is undocumented. Fall back to locally recorded pac deploy results.

### PM4. Verify in the running app (runtime checks for agents)
- **User and job:** AI agent and developer; confirm that a change works, not just that it compiles.
- **Problem:** the agent stops at BUILD SUCCEEDED. Nobody checks that the new field shows on the form, that the menu item opens, or that a role really reaches it.
- **Proposal:**
  - After a deploy, a **Smoke Check** step (and a guide section and tool hint) uses Microsoft's Dynamics 365 ERP MCP server (form and data tools). The steps: open the menu item, find the new controls, read a record through the entity, then close.
  - Generate the checklist from the change set (new controls, menu items, entity fields).
  - Report the result in Copilot Activity and the Pre-Deploy Checklist.
- **Why now:** Microsoft ships a runtime ERP MCP server. Pairing a design-time and a runtime MCP is a story no other D365FO tool has.
- **Value:** High. **Effort:** M.
- **Success signal:** share of agent sessions that end with a runtime check; defects caught before handover.
- **Dependencies or risks:** the ERP MCP server is in preview and needs tenant setup; must run only on dev environments; depends on PM3.

### PM5. Standard-code drift report for wrapped methods
- **User and job:** technical lead; upgrade or service update.
- **Problem:** W3 finds wrappers whose target is gone, changed its signature or can no longer be wrapped. The riskier case is a target that still matches but whose body changed: a CoC that relies on behaviour before or after `next` silently breaks.
- **Proposal:**
  - For every CoC wrapper and pre/post handler in your models, compare the target method's source between two installed versions. The previous version folder is kept by PM1.
  - List the changed targets with a diff link (native `vscode.diff`), ranked by how much changed and whether table fields or calls the extension uses are involved.
  - Add a tool option to `xpp_find_deprecations` (or a sibling tool) so agents can summarize the risk.
- **Why now:** Upgrade Readiness and the extension index already exist. This uses data that is already on disk.
- **Value:** High. **Effort:** M.
- **Success signal:** regressions after service updates traced back to the report; leads use it at each update.
- **Dependencies or risks:** needs two versions on disk (disk space); noise from formatting-only changes, so normalize whitespace.

### PM6. Agent session checkpoints and scope lock
- **User and job:** developer and reviewer; let an agent work without fear.
- **Problem:** undo goes through git (UI11). An agent run mixes with the developer's own uncommitted edits, and there is no way to say "only touch these elements".
- **Proposal:**
  - **Start Agent Checkpoint** takes a snapshot of the model files the session may touch, captured by the server before each write. **Revert Session** restores every element the session changed through the metadata API, with a list to confirm.
  - Optional **scope lock**: the project's elements, or a named list. Writes outside the scope are refused with a clear message. This is enforced next to `IWritePolicy`.
- **Why now:** write tools are mature. Trust is now the adoption limiter for leads.
- **Value:** High. **Effort:** M.
- **Success signal:** use of agent writes on real projects; how often Revert is used (low is good, and having it available is the point).
- **Dependencies or risks:** snapshots must cover labels and `.rnrproj`. Policy must be enforced on the server, not only in the UI.

### PM7. Integration "Try It" panel for OData entities and custom services
- **User and job:** integration developer; test an entity or a service just built.
- **Problem:** the Custom Service wizard and data entity tools create the objects, but testing them means Postman, an app registration and hand-built JSON.
- **Proposal:**
  - On an entity: run `$filter`, `$top` and `cross-company` queries against `/data/<PublicCollectionName>` with the signed-in token.
  - On a service operation: a request body pre-filled from the data contract's `parm` members; send it and show the response and errors.
  - Save requests as `.http` files in the repo.
  - Read-only by default; writes need a switch, as in DB6.
- **Why now:** integrations are most of the custom work in D365FO projects, and the wizard already creates the service.
- **Value:** Medium–High. **Effort:** M.
- **Success signal:** use per integration project; fewer "entity not public / wrong collection name" issues.
- **Dependencies or risks:** token audience for the F&O URL; never allow on production URLs.

### PM8. Data entity readiness review
- **User and job:** integration developer and reviewer; an entity that works in DMF, OData and dual-write.
- **Problem:** entities fail late, at import time:
  - staging fields don't match the entity;
  - mandatory table fields are unmapped;
  - IsPublic is set without a PublicEntityName or PublicCollectionName;
  - the natural key is missing;
  - set-based processing is enabled on an entity that cannot run set-based (views, unions, temporary tables);
  - change tracking costs on bulk loads.
- **Proposal:**
  - Static rules in the style of `PerformanceRules`, run per entity and on the branch in Review My Changes.
  - Add an `xpp_analyze_entity` mode, or extend `xpp_analyze_performance`.
  - Quick fixes where the metadata API allows them, for example adding the missing staging fields.
- **Why now:** the A3 research already lists the entity checks, but no P item covers them.
- **Value:** Medium. **Effort:** M.
- **Success signal:** entity findings fixed before deploy; fewer DMF import failures.
- **Dependencies or risks:** keep false positives low by marking rules with a confidence level.

### PM9. Business event wizard
- **User and job:** developer; publish an event to Power Automate, Service Bus or Event Grid.
- **Problem:** a business event needs a `BusinessEventsBase` subclass, a `BusinessEventsContract` with `parm` members, a trigger in a CoC or event handler, and labels for the catalog. The pattern is easy to get wrong, and nothing scaffolds it today.
- **Proposal:**
  - **New Business Event…**: pick the table or record, the contract fields and the trigger point (insert, update, or a method to wrap).
  - It creates the event, contract and trigger extension with labels and a module category.
  - It compiles all-or-nothing, like the other wizards. `scripts/scaffold-test.js` covers it.
- **Why now:** event-driven integration is Microsoft's recommended path, and the wizard framework already exists.
- **Value:** Medium. **Effort:** S–M.
- **Success signal:** generated events build without warnings and appear in the business event catalog after a rebuild.
- **Dependencies or risks:** the catalog needs a rebuild in the environment; document that in the setup output.

### PM10. Number sequence and parameter table plumbing wizard
- **User and job:** developer; add an ID with a number sequence to a new table.
- **Problem:** the steps are a classic multi-object chore:
  1. EDT;
  2. `NumberSeqApplicationModule` `loadModule` extension;
  3. a `numRef…` method on the parameters table;
  4. the parameters form tab;
  5. an `initValue`/`insert` hook to fetch the number.

  F4 lists table+form and data entity wizards, but not this.
- **Proposal:** **Add Number Sequence to Table…** creates and extends exactly these objects, reusing the standard precedent through `xpp_find_examples`. A companion step, "parameters table + form", is offered when the model has none.
- **Why now:** this ranks near the top of "how do I…" questions for new X++ developers; P11 already reviews sequences live.
- **Value:** Medium. **Effort:** M.
- **Success signal:** the generated reference appears in Number sequences > Generate on the environment; the build has no warnings.
- **Dependencies or risks:** module-specific `loadModule` classes; the number sequence wizard has to be run on the environment (setup note).

### PM11. Feature management scaffolding and a feature-gated code view
- **User and job:** ISV and partner developer; ship behaviour behind a feature flag.
- **Problem:**
  - Creating a feature (an `IFeatureMetadata` class, `FeatureClass` on a configuration key, `isFeatureEnabled` checks) is manual.
  - Reviewers can't see which code paths a feature gates or which features are left over after release.
- **Proposal:**
  - **New Feature…** scaffolds the class, labels and configuration key link.
  - **Show Feature Usage** lists the `isFeatureEnabled`/`FeatureStateProvider` checks per feature, using cross references.
  - Review My Changes flags new behaviour on standard flows that has no gate (opt-in rule).
- **Why now:** `FeatureClass` is already a Go To target, so half the metadata is understood. ISVs are a key segment.
- **Value:** Medium. **Effort:** S–M.
- **Success signal:** use by ISV models; features cleaned up after general availability.
- **Dependencies or risks:** the rule must stay opt-in to avoid noise.

### PM12. Tests built on the Acceptance Test Library (ATL)
- **User and job:** developer and tech lead; write meaningful SysTests for business processes.
- **Problem:** Generate SysTest Class produces a skeleton, but real tests need data (customers, items, orders). Microsoft's ATL provides fluent builders for that, and most teams don't know how to use them.
- **Proposal:**
  - **Generate Test with ATL** detects the tables and processes a class touches (cross references) and generates arrange steps with the matching ATL entities.
  - Add an ATL section to `xpp_get_guide` so agents write tests the same way.
  - Results stay in Test Explorer.
- **Why now:** Test Explorer (F1) and `xpp_run_tests` exist; test quality is the gap.
- **Value:** Medium. **Effort:** M.
- **Success signal:** share of generated tests that pass on first run; growth in test count per model.
- **Dependencies or risks:** ATL coverage varies by module; the ATL models must be in ModuleReferences (the tool should offer to add them).

### PM13. Extension collision report across models
- **User and job:** technical lead with several ISVs plus customer models; avoid deploy-time surprises.
- **Problem:** collisions surface only when packages are combined, or at runtime:
  - two models add the same field or index name to the same table;
  - enum extensions take the same value;
  - several CoC wrappers wrap the same method with an undefined call order;
  - form extensions hide or move the same control.
- **Proposal:**
  - A report per standard object across all installed models (the extension index already exists): conflicts first, then multi-wrapper hotspots with each model's wrapper code side by side.
  - An agent tool mode so `/review` can warn before adding yet another wrapper.
- **Why now:** the code intelligence index makes this cheap, and ISV-heavy customers are common.
- **Value:** Medium. **Effort:** S–M.
- **Success signal:** conflicts found before the integrated build or deploy.
- **Dependencies or risks:** keep it to real conflicts; multi-wrapper lists are informational only.

### PM14. Report a problem with a sanitized diagnostics bundle
- **User and job:** every user, and the maintainer; turn failures in preview into fixes.
- **Problem:** the product is in preview with a large surface. Users hit server errors or failed agent tool calls, and reports arrive without context. Usage statistics (W6) are local and have no consumer.
- **Proposal:**
  - **Report a Problem** (also on Copilot Activity entries and error toasts) builds a bundle: versions, environment type, the failing command or tool call with its arguments, server stderr, and local usage counters.
  - Code, labels, URLs and tokens are redacted, and the user previews the bundle before anything happens. It then opens a prefilled GitHub issue; nothing is sent automatically.
- **Why now:** this supports the Appendix C goal of 95% tool success without retry, and gives W6 a purpose.
- **Value:** Medium. **Effort:** S.
- **Success signal:** share of issues with a reproducible bundle; time to fix.
- **Dependencies or risks:** redaction must be strict (OWASP), with a preview before every send.

### PM15. Visual Studio parity page and "try without a UDE" story
- **User and job:** an evaluator on the Marketplace; decide whether to switch from Visual Studio.
- **Problem:** the README lists features but doesn't answer "what can't I do here that I do in VS?". The product can't be tried without a UDE.
- **Proposal:**
  - A parity table in FEATURES.md generated from the VS command inventory already collected (PkgCmdIDList): Done, Different, Not planned (with the reason). Examples: DGML diagrams, Compare, Infolog, debugger status.
  - A short animated walkthrough of the agent creating, building and reviewing an extension.
  - Name cloud-hosted and VHD environments as the no-UDE path.
- **Why now:** the product is published; conversion depends on trust in parity.
- **Value:** Medium. **Effort:** S.
- **Success signal:** install-to-active conversion; fewer "does it support X" issues.
- **Dependencies or risks:** the table must stay honest (for example, the debugger is still unverified).

## Prioritized shortlist

1. **PM3 Deployed-state awareness.** It is small and removes the most common silent failure behind tests, debugging and Run Snippet. Do it together with **V2**: verifying the debugger end to end should stay ahead of all new work.
2. **PM1 Environment version drift guard.** Every UDE gets serviced; without this the tool appears to break on its own, and disk usage grows unbounded.
3. **PM6 Agent session checkpoints and scope lock.** Leads are the gate to adoption, and this turns agent writes from a risk into something safe to try.
4. **PM2 Clone and wire a team repository.** It moves the product from a tool for one developer to a tool a whole team can adopt.
5. **PM4 Verify in the running app.** It closes the agent loop at runtime, and no other D365FO tool does this. It builds on PM3.

Challenges to the current priorities:
- **L4 (pipeline and work item links):** move it up. Teams won't adopt an IDE whose output doesn't flow into their Azure DevOps pipeline.
- **F4 (table + form and data entity wizards):** move it down. The agent tools and `@fno /design` already create these, while PM9 and PM10 cover patterns the agent gets wrong more often.

## Cut or simplify

- **About 60 "Go to <X>" commands:** collapse them into the existing **Open Referenced Element** with a picker of the references on the node. That gives the same behaviour with a much smaller `package.json` and smaller menus.
- **Environment commands** (Select, Check Connection, Show Active Environment, Sign In (pac), Initialize Deployment Solution (pac)): fold them into one status bar environment menu. Run `pac init` automatically on the first deploy.
- **Code intelligence toggles in two places** (the Dynamics 365 view and the settings): keep the settings plus one "Configure Code Intelligence…" command.
- **Telemetry Queries:** ship the static KQL as documentation or snippets rather than a command, unless it runs the query.
- **Usage statistics (W6):** feed them into PM14 or remove them. Local counters with no consumer only add surface.
- **Run Snippet in Environment:**
  - It leaves `<Model>ScratchRunner` in the model, which can then ship in a package. The Pre-Deploy Checklist and `xpp_package` should flag or exclude it.
  - Or keep it in a separate scratch model.
- **Polish fixes:**
  - The titles "Create D365FO Project?" and "Show Extensions and Handlers?" should end with `…`.
  - Add code folding to FEATURES.md and "What's new".
  - The README's Getting Started says "Select D365FO Project", but the command is "Open D365FO Project…".