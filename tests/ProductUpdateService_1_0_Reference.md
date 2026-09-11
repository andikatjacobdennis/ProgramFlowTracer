# ProductUpdateService 1.0 — Reference

Every step from the MQ message arriving to the reply going back, what each method genuinely does,
and for the seven things that changed, why they had to.

**Reading this.** Rows marked **[CHANGED]** or **[NEW]** were touched for the SIB work; their last
column says what was wrong before and what the change does about it. Unchanged rows leave it blank.
The service does exactly one operation — `UpdateToServicePack` — and everything below exists to
answer one question: which service pack can this truck move to, and what gets installed.

Verified against the working copy on 11 Sep 2026, and against a traced `PreReleaseVerification` run
on chassis NR-999222 — five nodes, five candidate packs, Version2 applied.

---

## Vocabulary

| Term | Meaning |
|---|---|
| **Node** | One ECU on the truck (EBS, FCIOM, VCM_C …), written `family.type.position`, e.g. `9.1.0`. |
| **Node template (NTP)** | The definition of what software a node should hold. Identified by a *document number*. |
| **Replacement chain** | The recorded history of which node template superseded which. Walking it forward is how newer software is found. |
| **Service pack** | An approved collection of software, connected in the chains to the node templates it contains. |
| **SIB** | Software Integration Baseline. A RepoLib package typed `SoftwareIntegrationBundle` whose `content.guid` resolves to a baseline carrying both software products and node templates. |
| **HPC node / execution environment** | A compute ECU whose software arrives as RepoLib packages rather than as a classic node template. |
| **Scenario** | How strict to be: Released, Introduced, PreReleaseVerification, InProgress. Decides which packs are permitted at all. |

---

## Phase 1 — The message arrives

*GatewayComponents · Request.Listener*

| # | Project | Class | Method | What it does | What changed and why |
|---|---|---|---|---|---|
| 1 | Request.Listener | `Handler` | `ProcessRequest` | Owns the request end to end. Pulls the JSON off the queue, validates it against the 1.0 schema, warns if the schema version isn't 1.0, then hands off to the domain and serialises whatever comes back. | |
| 2 | Request.Listener | `RequestParser` | `Parse` | Turns JSON into domain objects: chassis and variants, the conventional nodes with their current node template document numbers, and the HPC nodes with their execution environments and software products. Forces `OperationType = UpdateToServicePack`. | |
| 3 | Request.Listener | `RequestParser` | `ParseBreakdownReplacementChainFilter` | Maps `criteria.scenario` to a scenario filter — Released, Introduced, PreReleaseVerification or InProgress. This single choice decides which service packs are permitted at all, before anything else runs. | |
| 4 | Request.Listener | `Handler` | `ProcessRequest` *(guard)* | Rejects anything that isn't UpdateToServicePack with error code 100. This service does one job. | |

---

## Phase 2 — Build the picture of the truck

*BreakdownDomain.Services*

| # | Project | Class | Method | What it does | What changed and why |
|---|---|---|---|---|---|
| 5 | Services | `AftermarketService` | `RequestSoftware` | Domain entry point. Validates the chassis are unique and share a product class, runs the update, then assembles workflows and release notes for the reply. | |
| 6 | Services | `AftermarketService` | `GetRequestSoftwareProduct` | Builds an accurate picture of the truck *before* anything changes: per product, build it, determine its current service pack, validate the nodes, set the market context, add default variants, apply any simulation kit, attach HPC nodes. | |
| 7 | Services | `AftermarketServiceHelperBase` | `BuildProductFromRequest` | Converts the request's node list into a Bill of Material — every node, its slots, and the software in them right now. | |
| 8 | Infrastructure | `CachedNodeTemplateRepository` | `LoadReplacementChainsAsync` | Loads every replacement chain and its connected service packs from the database. Cached per process in memory, so only the first request pays for it. | |
| 9 | Services | `SoftwareUpgrader` | `DetermineServicePack` | Works out which pack the truck is on *now*, by walking each node's chain. Returns an exact match when the node's current template is directly connected to a pack; otherwise the highest `SortOrder` the chain can reach, flagged as not exact. | **Left alone deliberately.** It still intersects across every node, so the HPC-derived nodes collapse the exact-match list to empty and it always answers "highest reachable, not exact". That no longer reaches the reply (see row 26), but the method is still loose and is worth revisiting. |
| 10 | Services | `AftermarketServiceHelperBase` | `ValidateNodes` | Sanity-checks the nodes so the request fails early with a clear message rather than deep inside the configuration search. | |
| 11 | Services | `SoftwareUpgradeService` | `SetContext` | Picks the market context all products share. The context governs which node templates apply at all. | |

---

## Phase 3 — Choose a service pack

*Where the decision is made, and where every failure originated.*

| # | Project | Class | Method | What it does | What changed and why |
|---|---|---|---|---|---|
| 12 | Services | `ProductUpdaterRouter` | `RequestSoftwareUpdate` | Routes the work. Several update kinds exist; UpdateToServicePack goes to the configurator updater. | |
| 13 | Services | `ConfiguratorProductUpdater` | `RunSoftwareUpgradeOperation` | Recognises the operation as UpdateToServicePack and delegates to the specialist, returning the updated installation, the upgrade path steps, and the pack that was applied. | **[CHANGED]** Returned a 2-tuple and threw the applied pack away with `var (installation, _, steps)`. It now returns it as a third element, because the caller re-derived it and got a different answer — see row 26. |
| 14 | Services | `ServicePackUpdater` | `UpdateToServicePack` | The heart of the service. Gets the candidates, filters them by scenario, sorts newest `SortOrder` first, and tries each until one produces a valid configuration. If none does, it throws the first failure's reason. | |
| 15 | Services | `SoftwareUpgradeService` | `GetAvailableServicePackReferences` | For each service-pack node, walks its chain forward from the node's current document number and collects the packs connected to each link. Returns two things: the candidate list, and a map of which nodes each pack covers. | **[CHANGED]** `Intersect` → `Union`. A pack covers *part* of the truck. Intersecting demanded every node reach it — but the HPC-derived VCM nodes reach no pack at all, and EBS reaches a different subset, so the result was reliably empty and the request failed with "no potential service packs exist in the replacement chains". The coverage map, not the intersection, is what limits each pack. |
| 16 | Model | `Introduced` / `Released` / `PreReleaseVerification` / `InProgress` filters | `CheckIfServicePackIsAvailableForUpdateToServicePack` | Applies the scenario. A `Frozen` pack with no introduction date passes only under PreReleaseVerification or InProgress; Released and Introduced reject it. | |
| 17 | Services | `ServicePackUpdater` | `servicePackNodeDefinitions` *(scoping)* | Decides which node definitions the candidate pack is allowed to govern, and so which nodes can veto it. | **[CHANGED]** Built from `BillOfMaterial.ServicePackNodes`, which returns *every* node. That told the filter it governed VCM_C and VCM_C_s, whose templates no pack is connected to, so every candidate was rejected. Now only nodes reaching at least one pack are governed — the HPC-derived nodes keep what they have, while a node that does participate still vetoes a pack that omits it, which is what preserves the Version5 → Version4 fallback. |
| 18 | Services | `ServicePackCompatibilityResolver` | `GetSibDocumentNumbersAsync` | Resolves which node templates a pack carries inside its SIBs: RepoLib packages → the ones typed `SoftwareIntegrationBundle` → `content.guid` → the baseline → its `DocumentNumberLinks.PartNumber`. Recurses into SIBs nested in SIBs, guarded against cycles. | **[NEW]** The VCM node templates (26042381, 26042407) exist only inside the SIB `SWBundling_Nandini_ProdUpdateService_31Aug`, never in the replacement chains — so nothing in the old flow could find them. An earlier attempt tested `type == "sib"`, which matches nothing; the live value is `SoftwareIntegrationBundle`. |
| 19 | Model | `ServicePackReplacementChainFilter` | `ShouldIncludeNodeTemplateIssue` | Called once per candidate node template. Accepts it if the node definition isn't governed by this pack (passed to the scenario filter), or the template is connected to the pack in the chains, or — now — its document number is one the pack carries in a SIB. Anything else is rejected. | **[CHANGED]** Only the middle route existed. A pack whose templates live in a SIB is connected to none of them in the chains, so the lookup found nothing and every template was rejected — the "No node templates available for node 76.1.0 VCM_C" failure. Development and production solve this by merging the SIB's document number links into the baseline; aftermarket has no baseline, so they're matched by number here instead. |

---

## Phase 4 — Work out the software

*Per candidate, until one configuration holds.*

| # | Project | Class | Method | What it does | What changed and why |
|---|---|---|---|---|---|
| 20 | Services | `UpgradePathWalkerServicePackCheck` | `IsValidUpgradePathAsync` | For a candidate newer than the current pack, checks the HPC side: can the software on the compute nodes actually get from where it is to where the pack wants it. Skipped entirely when the request has no HPC nodes. | |
| 21 | Services | `ServicePackCompatibilityResolver` | `GetTargetSoftwareProductsFromServicePackAsync` | Fetches the candidate's RepoLib packages and evaluates them against this exact truck's specification, producing the target software products the upgrade path walker compares against. | **[CHANGED]** Read only the pack's *top-level* packages. A product delivered inside a SIB therefore had no target snapshot id, which hits Case 3 — "target service pack not usable" — and the candidate was silently skipped. Now uses `ExpandRepoLibEntitiesAsync`, so packages inside SIBs and nested SIBs are included. |
| 22 | Services | `ServicePackCompatibilityResolver` | `ExpandRepoLibEntitiesAsync` | Recursively expands RepoLib links into a flat, de-duplicated package list plus every SIB baseline found on the way, tracking duplicates and guarding against circular references. | **[NEW]** The shared walk. Exposed on `IServicePackCompatibilityResolver` so the gateway can use the same one rather than keeping a second copy that drifts. |
| 23 | Services | `SoftwareUpgrader` | `BatchUpgradeSoftware` | Splits large requests into batches so the search stays tractable, then builds and evaluates configurations for each. | |
| 24 | Services | `SoftwareUpgradeService` | `BuildInitialConfigurationQueue` | Turns the filtered chains into a starting set of candidate configurations. Throws when any chain came back empty — this is the line that produces "No node templates available for node X". | |
| 25 | Model | `ConfigurationAnalyzer` | `FindBestConfiguration` | Searches the combinations for one where every node is consistent — dependencies satisfied, parameters valid, hardware compatible. The expensive step, and the one that hits `abortAt` when the timeout is too tight. | |

---

## Phase 5 — Build the reply

*Back out to the queue.*

| # | Project | Class | Method | What it does | What changed and why |
|---|---|---|---|---|---|
| 26 | Services | `ConfiguratorProductUpdater` | `RequestSoftwareUpdate` | Assembles the `RequestSoftwareContainer`: the modified installation, the service pack, delta configurations and transition step results. | **[CHANGED]** It discarded the applied pack and called `DetermineServicePack` again, which answers with the highest `SortOrder` the chain can reach. With Version5 rejected and Version2 applied, the reply still said **Version5**. It now reports the pack actually applied, and marks it as an exact match because that pack is known, not inferred. |
| 27 | Request.Listener | `NodeTranslator` | `BuildExecutionEnvironmentsAsync` | Builds each HPC node's execution environments for the response and fills them with the software products that match. | **[CHANGED]** Fetched only top-level RepoLib packages, so software delivered inside a SIB was **missing from the reply entirely** — the workshop would never be told to install it. Now calls the resolver's `ExpandRepoLibEntitiesAsync`, which is why `NodeTranslator` gained `IBaselineRepository` and `IServicePackCompatibilityResolver`. |
| 28 | Request.Listener | `ResponseCreator` | `CreateSuccess` | Assembles the response: workflows, HPC hardware snapshots, and any HPC nodes without CAN nodes. | |
| 29 | Request.Listener | `Handler` | `ProcessRequest` *(reply)* | Validates the response against the schema, serialises it, and returns it on the reply queue. A `NotificationException` becomes an error response with a code rather than a crash — it means the truck genuinely can't be updated, and the reason is meaningful to the workshop. | |

---

## The failures you will actually see

| Message | What it means | Where |
|---|---|---|
| *No potential service packs exist in the replacement chains* | No candidate was found at all. Before the Union change this was the normal outcome for any truck with HPC nodes. | row 15 |
| *… are not available in the {scenario} scenario* | Candidates existed but the scenario rejected all of them. Usually a `Frozen` test pack against a Released or Introduced scenario. | row 16 |
| *No node templates available for node X and template type NTP* | A candidate was tried and the filter rejected every template for node X, so the queue builder had nothing. Either the pack genuinely doesn't cover that node, or it carries it in a SIB that isn't being resolved. | rows 19, 24 |
| *No valid configuration was found before the max processing time was reached* | Not a data problem — `abortAt` fired mid-search. A cold process spends ~14 s on the first candidate warming caches; set the client timeout to 125 rather than 45. | row 25 |

---

## Summary of the seven changes

| Change | File | One-line reason |
|---|---|---|
| Applied pack propagated | `ConfiguratorProductUpdater` | The reply named a pack the truck was never updated to. |
| `Intersect` → `Union` | `SoftwareUpgradeService` | A pack covers part of the truck, not all of it. |
| Node-definition scoping | `ServicePackUpdater` | HPC-derived nodes were vetoing every candidate. |
| `GetSibDocumentNumbersAsync` | `ServicePackCompatibilityResolver` | The VCM node templates live only inside a SIB. |
| Third acceptance route | `ServicePackReplacementChainFilter` | Aftermarket has no baseline to merge the SIB's links into. |
| `ExpandRepoLibEntitiesAsync` | `ServicePackCompatibilityResolver` | SIB-delivered products had no target snapshot id. |
| Response expansion | `NodeTranslator` | SIB software was missing from the reply entirely. |

## Known loose ends

- **`DetermineServicePack` still intersects across every node** (row 9), so its exact-match list always collapses. It no longer reaches the reply, but the looseness remains.
- **Two recursive SIB walks now exist** in `ServicePackCompatibilityResolver` — `CollectSibDocumentNumbersAsync` for document numbers and `ExpandRepoLibEntitiesAsync` for packages. They walk the same tree and can drift apart.
- **Blocking calls inside `async` methods.** `GetTargetSoftwareProductsFromServicePackAsync` and `NodeTranslator.BuildExecutionEnvironmentsAsync` both use `.GetAwaiter().GetResult()` on a call they immediately `await` around. A deadlock and thread-starvation risk under load.
- **`Union` widens candidate selection for every request**, not only SIB ones. The candidate loop rejects unusable packs in `SortOrder` order, so it should be self-correcting — but it deserves a regression pass on non-SIB scenarios.
