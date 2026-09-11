# ProductUpdateService 1.0 — Review Q&A

Prepared for the code review of the SIB service pack changes. The `Intersect` → `Union` change in
`SoftwareUpgradeService.GetAvailableServicePackReferences` draws most of the questions, so it comes
first and in the most detail.

Evidence throughout is from a traced `PreReleaseVerification` run on chassis NR-999222 — five nodes
(`9.1.0 EBS`, `4.3.0 FCIOM`, `4.2.0 CCIOM`, `76.1.0 VCM_C`, `76.11.0 VCM_C_s`) and five candidate
service packs (Version through Version5).

---

## Part 1 — The Union change

### Q1. Why change a fundamental selection rule? The intersection was surely deliberate.

It was, and it encoded a true statement: *a service pack must be applicable to every node on the
truck*. That held while every node participated in service packs.

It stopped holding when HPC-derived nodes entered the Bill of Material. Those nodes take their
software from RepoLib packages, not from chain-connected node templates, so they are connected to
**no service pack at all** — by design, not by omission. The trace is unambiguous:

```
node 9.1.0  EBS     (doc 24440313) reached 8 service pack(s)
node 4.3.0  FCIOM   (doc 24759479) reached 11 service pack(s)
node 4.2.0  CCIOM   (doc 24868542) reached 11 service pack(s)
node 76.1.0 VCM_C   (doc 26042381) reached 0 service pack(s)
node 76.11.0 VCM_C_s (doc 26042407) reached 0 service pack(s)
```

Intersecting a zero-length list with anything gives zero. On any truck with HPC nodes the candidate
list was **guaranteed empty**, and the request failed with *"No potential service packs exist in the
replacement chains"* every single time. The rule wasn't wrong when written; the vehicle changed
underneath it.

### Q2. Doesn't Union now let inapplicable packs through?

Into the **candidate list**, yes. Into the **result**, no.

This is the distinction I'd most like the reviewer to hold onto. `GetAvailableServicePackReferences`
does not decide anything — it produces the list `ServicePackUpdater` then tries, newest `SortOrder`
first, until one produces a valid configuration. Selection got looser; **acceptance did not change at
all**. The gate moved from "before we try" to "when we try", and the second gate is the stricter one
because it runs the real breakdown.

The trace shows this working exactly as intended:

```
Version5 (526670)  rejected: no node templates available for node 9.1.0 EBS
Version4 (526619)  rejected: same
Version3 (521458)  rejected: same
Version2 (521444)  ACCEPTED
```

Three of the five newly-visible candidates were rejected on their merits. Union didn't produce a
wrong answer; it made the fallback to a correct one possible.

### Q3. What actually stops a wrong pack being applied now?

Three gates downstream, all pre-existing except the second:

1. **The scenario filter** — `CheckIfServicePackIsAvailableForUpdateToServicePack`. A `Frozen`,
   unreleased pack passes only under PreReleaseVerification or InProgress.
2. **`servicePackNodeDefinitions` scoping** — a node that *does* participate in service packs still
   vetoes a candidate that omits it.
3. **`BuildInitialConfigurationQueue`** — throws when any chain has no templates left after filtering.

### Q4. So Union on its own would have been unsafe?

**Yes — and this is the most important thing to understand about the change.** Union and the
`servicePackNodeDefinitions` scoping must ship together.

Union alone widens the candidate list. What keeps a genuinely inapplicable pack out is that a
participating node still vetoes it. That veto lives entirely in the scoping change: if
`servicePackNodeDefinitions` had been left as "every node", the filter would reject everything for
the HPC nodes and nothing would ever pass — the old failure. If it were relaxed to "only nodes this
pack covers", the veto would disappear and Version5 would have been accepted despite not covering
EBS.

The rule that makes both work is: **govern the nodes that participate in service packs at all, not
the nodes this particular pack happens to cover.**

| Node | Reaches any pack? | Governed? | Effect |
|---|---|---|---|
| `9.1.0 EBS` | yes — 8 | yes | vetoes Version5/4/3, which omit it |
| `4.3.0 FCIOM` | yes — 11 | yes | participates normally |
| `76.1.0 VCM_C` | **no — 0** | no | keeps what it has, blocks nothing |

### Q5. Did you try a narrower fix first?

Yes, and it was measurably insufficient. The first attempt kept `Intersect` and merely skipped nodes
that reached **zero** packs:

```csharp
if (servicePackReferencesFromChain.Count == 0) continue;
```

That fixed VCM_C and VCM_C_s. It did **not** fix the real case, because EBS reaches 8 packs — it
just doesn't reach the *same* 8:

```
EBS   → 521435, 521526, 521527, 521528, 521530, 521419, 521444, 521532
FCIOM → those 8, plus 521458, 526619, 526670
```

Version3, Version4 and Version5 are in FCIOM's list and not EBS's, so intersecting still discarded
precisely the packs QA expected to see. The narrow fix was tried, traced, and abandoned on evidence.

### Q6. Is this a behaviour change for existing non-SIB requests?

**Yes, and this is the genuine risk in the change.** I'd rather state it plainly than argue it away.

Where every node reaches the same packs, `Intersect` and `Union` produce identical results and
nothing changes. The difference appears when nodes reach different subsets: a pack reachable by only
some nodes was previously never offered, and is now attempted.

In principle the downstream gates reject it, and in the traced case they did. But "in principle" is
not a regression suite. **This deserves a regression pass over non-SIB scenarios** — particularly
trucks with no HPC nodes, where the old behaviour was correct and unchanged conditions should produce
unchanged answers.

### Q7. Could it now select a *newer* pack than before?

Yes, and that's the case to think hardest about. Candidates are ordered `SortOrder` descending, so a
newly-visible newer pack is tried first. If it succeeds, the result is newer than the old code would
have produced.

Whether that is a bug depends on whether it *should* have been applicable. If a participating node
can't reach it, gate 2 rejects it. If every participating node can reach it, then it was applicable
all along and the intersection was excluding it only because of a non-participating HPC node — which
is the bug being fixed.

The residual risk is a pack that is inapplicable for a reason **none of the three gates model**. I
know of no such reason, but I can't prove a negative, which is another argument for the regression
pass.

### Q8. What's the blast radius?

Narrower than it looks. `GetAvailableServicePackReferences` has exactly **one live caller**:

| Caller | Status |
|---|---|
| `ServicePackUpdater.cs:55` | the only live call |
| `AftermarketService.cs:540` | commented out |

`ServicePackReplacementChainFilter` likewise has exactly one construction site,
`ServicePackUpdater.cs:184`.

**But `UpdateToServicePack` itself has two callers**, so both flows are affected:

| Caller | Flow |
|---|---|
| `ConfiguratorProductUpdater.cs:89` | ProductUpdateService 1.0 / SoftwareRequest |
| `AvailableUpdatesService.cs:207` | **AvailableProductUpdates 1.0** |

The reviewer should know that AvailableProductUpdates is in scope for the Union and scoping changes
even though all the testing has been through ProductUpdateService. It is **not** affected by the
applied-pack change, because it already consumed the returned `ServicePackReference` correctly.

### Q9. Why not fix the data instead of the code?

For the HPC nodes, because there is no data to fix. VCM_C and VCM_C_s are *designed* to reach no
service pack — their software arrives as RepoLib packages. Adding chain links for them would be
inventing data to satisfy an assumption that no longer holds.

For EBS and Version4, it may genuinely be a data gap, and **that question is still open with QA**.
EBS's 13 node templates connect only to 521419, 521435, 521444, 521526, 521527, 521528, 521530 and
521532 — never to 521458, 526619 or 526670. If 9.1 is supposed to be part of Version4, the links are
missing. Neither answer changes the Union rationale; Q1 stands either way.

### Q10. Performance?

More candidates means more attempts, but rejections are cheap once caches are warm:

```
Version5  0.12 s   rejected
Version4  0.10 s   rejected
Version3  0.10 s   rejected
Version2  6.2  s   accepted
```

The cost that matters is the **first** candidate on a cold process — about 14 s loading chains and
node templates — which is paid regardless of how many candidates exist. A 45 s client timeout is not
enough for a cold start; 125 s is comfortable.

### Q11. Why was the `first` flag removed?

It existed only to seed the intersection with the first node's list. With a union, `Union` on an
empty list is already correct, so the flag had no remaining purpose.

---

## Part 2 — The other changes

### Q12. Why is `exactMatch` forced to `true` when a pack was applied?

Because at that point the pack is *known*, not inferred — we applied it. `ServicePackIsExactMatch`
means "this is precisely the pack", and after a successful apply that is true by construction.

In practice nothing observable changes: `DetermineServicePack` returns `false` here regardless,
because it intersects its exact-match list across all nodes and the HPC nodes collapse it to empty.
If the reviewer would rather not assert it, dropping that line leaves the `ServicePack` fix intact
and only the flag unchanged.

### Q13. Why optional constructor parameters instead of required?

To keep the diff small and avoid touching call sites that don't care. The cost is that a missed DI
registration degrades silently rather than failing at startup.

Both registration sites are updated, so nothing is currently unwired. **If the reviewer prefers them
required, I agree** — a hard failure at startup beats a silent behaviour change in production, and
the compiler would then enforce that every future host wires them.

### Q14. Why `SoftwareIntegrationBundle` and not `"sib"`?

Because that is what the live packages actually declare. An earlier attempt tested `type == "sib"`,
which matches nothing, so it silently resolved zero SIBs every run and looked like "the feature
doesn't work". Confirmed by dumping the raw package JSON:

```json
{
  "name": "B1_T3_T3_ABC Node_202627",
  "type": "SoftwareIntegrationBundle",
  "content": { "guid": "e4521341-2c2b-45d5-8089-3ce97375b08e" }
}
```

`BaseService` — the production breakdown — tests the same value. Note its doc comment still says
`"sib"`, which is where the wrong value was copied from; the comment is stale, the code is right.

### Q15. Why does the filter match on document number rather than something stronger?

Because the document number is the identifier the SIB's baseline actually provides
(`DocumentNumberLinks.PartNumber`), and it's the same identifier the chain reference carries
(`NodeTemplateReplacementChainReference.DocumentNumber`). Matching on it is comparing like with like.

This mirrors what development and production already do — they merge the SIB's document number links
into the baseline via `BaselineWithSibDocumentNumberLinks` and let `DeriveNodeTemplates` resolve
them. Aftermarket has no baseline to merge into, so the same links are matched against the chain
instead. Same data, same identifier, different plumbing because the two paths are shaped differently.

### Q16. Why is `ExpandRepoLibEntitiesAsync` on the interface rather than duplicated?

So the gateway and the domain share one walk. An earlier iteration duplicated it into
`NodeTranslator`; exposing it on `IServicePackCompatibilityResolver` is better, because two copies of
a recursive expansion will drift the first time one is changed.

### Q17. Is there a rollback?

Yes, and it's granular — the changes are independent apart from one pairing:

| Revert | Effect |
|---|---|
| Union alone | **Don't.** Reverting it without also reverting the scoping leaves candidates that nothing vetoes. Revert both together or neither. |
| SIB resolution (rows 18, 19, 21, 22, 27) | Behaviour returns to pre-SIB exactly; `sibDocumentNumbers` defaults to empty and the filter's third route is dead code. |
| Applied-pack propagation | Independent of everything else. Reverting restores the wrong pack in the reply. |

---

## Part 3 — What I'd concede

Points where I think the reviewer would be right to push:

1. **No automated tests.** Everything here was verified by tracing a live request, not by a test
   suite. The `Union` and scoping changes in particular are exactly the kind of rule that should be
   pinned by a test with a fixture of nodes reaching different pack subsets.

2. **`.GetAwaiter().GetResult()` inside `async` methods** — in
   `GetTargetSoftwareProductsFromServicePackAsync` and `NodeTranslator.BuildExecutionEnvironmentsAsync`.
   Both block on a call they immediately `await` around. A deadlock and thread-starvation risk under
   load, and it buys nothing. One line each.

3. **Two recursive SIB walks now coexist** in `ServicePackCompatibilityResolver` —
   `CollectSibDocumentNumbersAsync` for document numbers and `ExpandRepoLibEntitiesAsync` for
   packages. They walk the same tree. One should absorb the other.

4. **`DetermineServicePack` still intersects across every node**, so its exact-match list always
   collapses on a truck with HPC nodes. It no longer reaches the reply, but it is the same flawed
   assumption we just fixed elsewhere, left in place.

5. **The EBS/Version4 data question is unresolved.** Until QA answers it, we cannot say whether the
   current result (Version2) is correct or merely the best available given incomplete test data.

6. **No cache invalidation for SIB-derived data.** Baselines and RepoLib packages are cached per
   process with no change monitor tied to them, so a SIB edited in the database may not be picked up
   until the entry expires or the host restarts.
