# 9. Third-party section

## Build side: the only third party is Microsoft

| Third party | Role | Contract basis | Data exposure | Controls |
|---|---|---|---|---|
| Microsoft Azure (State Fund's existing enterprise agreement) | Hosting, storage, SQL, Functions, AI Search, Service Bus, Event Grid | State Fund's existing Microsoft agreements and DPA | All data, in State Fund's tenant | Inherited controls: SOC 2 Type II, ISO 27001 and others, plus State Fund's own Azure policies |
| Azure OpenAI (`gpt-5.4-mini`, `text-embedding-3-large`) and Content Understanding | Layout, classification, learning a format, chat | The same Microsoft DPA. Data Zone Standard (US) deployments | Document text and images during processing. **Not used for training.** Abuse-monitoring retention per Microsoft's policy; modified abuse monitoring to be requested for production | Managed identity only. Defender for Cloud AI threat protection on. Prompt Shields policy defined (artefact 3) |

**No other vendor, model provider, SaaS or data processor is in the data path.**

The open-source libraries are build-time dependencies running inside the tenant, listed in the solution's package references and `package-lock.json`:
- .NET: EF Core, ClosedXML, PdfSharp, PDFtoImage;
- the web app: React, TanStack, Radix and others.

**What this means for NAIC Bulletin §4.3 (third-party AI systems).**
- The vendor due diligence it asks for reduces to the Microsoft relationship State Fund already governs.
- Model version pinning, the retirement calendar and the golden-set regression (artefacts 4 and 7) stand in for monitoring a vendor's model updates.

## Buy side: what the same section would need for a vendor

Each vendor in the bake-off (Nomad Data, Bevaya, SortSpoke, Convr) is itself the third party, and the AI system runs outside State Fund's tenant. The bulletin's §4.3 expectations then apply in full:
- a due-diligence record: security review, SOC 2 report, pen-test summary;
- a DPA covering claims data, including claimant personal data, with sub-processor lists (vendors' own model providers);
- contract terms:
  - audit rights;
  - a no-training clause;
  - data ownership, export and exit rights;
  - notice of model changes;
  - a model version pinning commitment;
- ongoing monitoring of the vendor's accuracy after their model updates. That needs a held-out test set and a re-run cadence the insurer owns: the same sealed set (`sealed-set-protocol.md`).
- a vendor-selection process record (Colorado 10-1-1 style).

**Questions to put to every vendor** (`docs/vendors/vendor-landscape.md`):
- price per 1,000 pages at State Fund's volumes;
- where data is processed, and by which model provider;
- whether customer data trains any model;
- whether every extracted value carries a page-and-box or sheet-and-cell source;
- how a correction is audited;
- what happens to accuracy when they change models.
