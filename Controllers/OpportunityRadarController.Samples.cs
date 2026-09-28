using System.Text.Json;
using HendersonSoftwareLabsAPI.Data;
using HendersonSoftwareLabsAPI.Entities;
using HendersonSoftwareLabsAPI.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HendersonSoftwareLabsAPI.Controllers;

public partial class OpportunityRadarController
{
    private static IReadOnlyList<(string Key, ActiveProjectSourceType SourceType, ActiveProjectImportRequest Request)> ActiveProjectSamples() =>
    [
        ("integration-strong", ActiveProjectSourceType.ExplicitDemand, new("Shopify supplier reconciliation",
            "We need a tool that reconciles Shopify orders with supplier spreadsheets, flags mismatches, and produces a daily exception report within 8 weeks.",
            "ExplicitDemand", Budget: "$8,000", OpportunityRating: "High")),
        ("automation-semantic", ActiveProjectSourceType.ExplicitDemand, new("Remove daily order rekeying",
            "Our operations coordinator copies new orders from email attachments into three vendor systems every morning. We want the repeated entry removed and failures surfaced for review.",
            "ExplicitDemand", Budget: "$6,000")),
        ("keyword-bad-scope", ActiveProjectSourceType.ExplicitDemand, new("Enterprise React .NET transformation",
            "Seeking a full-time principal engineer to lead a multi-year enterprise React, .NET, SQL, and AWS transformation with a team of twelve developers. Salary and employee benefits provided.",
            "ExplicitDemand")),
        ("unknown-budget", ActiveProjectSourceType.ExplicitDemand, new("Customer reporting portal",
            "Build a secure customer portal where clients can view monthly SQL-backed reports and download approved exports. We have a clear design and would like delivery in 10 weeks.",
            "ExplicitDemand")),
        ("low-budget", ActiveProjectSourceType.ExplicitDemand, new("Inventory dashboard",
            "Create a React inventory dashboard connected to our existing API. The deadline is two weeks.",
            "ExplicitDemand", Budget: "$500")),
        ("full-time-role", ActiveProjectSourceType.ExplicitDemand, new("Senior software engineer",
            "Full-time employee role for a senior C# and React engineer. This position is 40 hours per week and includes salary, health insurance, and paid leave.",
            "ExplicitDemand")),
        ("vague-request", ActiveProjectSourceType.ExplicitDemand, new("Need an app",
            "We need an app to make our business better. Please send a quote.",
            "ExplicitDemand")),
        ("api-risk", ActiveProjectSourceType.ExplicitDemand, new("Legacy vendor synchronization",
            "Build an integration that synchronizes customer records with our legacy vendor.",
            "ExplicitDemand", Budget: "$9,000", Risk: "API access is pending vendor approval and the undocumented API may not expose update operations.")),
        ("operational-signal", ActiveProjectSourceType.OperationalSignal, new("Daily spreadsheet order processing",
            "Synthetic company job description: the operations specialist downloads orders, rekeys them into supplier spreadsheets, and emails exception reports each day.",
            "OperationalSignal")),
        ("integration-near-duplicate", ActiveProjectSourceType.ExplicitDemand, new("Shopify order and supplier reconciliation",
            "We need a tool to reconcile Shopify orders against supplier spreadsheets, highlight mismatches, and send a daily exception report with an 8 week delivery target.",
            "ExplicitDemand", Budget: "$8,000"))
    ];

    private static IReadOnlyList<(string Key, BusinessProspectImportRequest Request)> BusinessProspectSamples() =>
    [
        ("prospect-strong", new("Riverside Family Dental",
            [new EvidenceFact("Established dental practice, well known locally, 5-star reviews and loyal customers for over fifteen years.", "https://example-riverside-dental.test", null),
             new EvidenceFact("The website is outdated, not mobile friendly, and has no online booking system.", "https://example-riverside-dental.test", null)],
            "https://example-riverside-dental.test", "Local", "Healthcare",
            Fit: "The practice needs a new website with online booking.", OpportunityRating: "High")),
        ("prospect-already-modern", new("Crestline Auto Body",
            [new EvidenceFact("Well-regarded auto body shop with a modern website, mobile friendly, recently redesigned, with online booking already in place.", "https://example-crestline-autobody.test", null)],
            "https://example-crestline-autobody.test", "Local", "Automotive")),
        ("prospect-unknown-intent", new("Maple Street Bakery",
            [new EvidenceFact("A small bakery with a loyal local following.", "https://example-maple-bakery.test", null),
             new EvidenceFact("The website has not been updated in years and has no contact form.", "https://example-maple-bakery.test", null)],
            "https://example-maple-bakery.test", "Local", "Food",
            Risk: "No hiring or purchasing signal was found.")),
        ("prospect-reputation-mismatch", new("Sterling Home Roofing",
            [new EvidenceFact("Highly rated, trusted roofing company with hundreds of positive reviews and a long-standing reputation.", "https://example-sterling-roofing.test", null),
             new EvidenceFact("The current website is broken on mobile and hasn't been updated in years.", "https://example-sterling-roofing.test", null)],
            "https://example-sterling-roofing.test", "Regional", "HomeServices")),
        ("prospect-excluded-industry", new("Bayview Legal Group",
            [new EvidenceFact("An established law firm with an outdated website and no online intake form.", "https://example-bayview-legal.test", null)],
            "https://example-bayview-legal.test", "Regional", "Legal")),
        ("prospect-thin-evidence", new("Downtown Coffee Cart",
            [new EvidenceFact("A small coffee cart; not much else is known.", null, null)],
            null, null, "Food")),
        ("prospect-no-contact", new("Northgate Landscaping",
            [new EvidenceFact("An established landscaping company with an outdated site.", "https://example-northgate-landscaping.test", null),
             new EvidenceFact("No phone number listed and no way to reach the business was found anywhere online.", "https://example-northgate-landscaping.test", null)],
            "https://example-northgate-landscaping.test", "Local", "HomeServices")),
        ("prospect-entry-project", new("Value Hardware Supply",
            [new EvidenceFact("A long-standing hardware supplier.", "https://example-value-hardware.test", null),
             new EvidenceFact("There is no online store and the contact form is broken.", "https://example-value-hardware.test", null),
             new EvidenceFact("A contact page lists a phone number.", "https://example-value-hardware.test", null)],
            "https://example-value-hardware.test", "Local", "Retail",
            Fit: "The business needs a new website with an online store.")),
        ("prospect-near-duplicate-a", new("Harbor View Physical Therapy",
            [new EvidenceFact("Established physical therapy clinic, well known and trusted locally.", null, null),
             new EvidenceFact("Outdated website with no online booking.", null, null)],
            null, "Local", "Healthcare")),
        ("prospect-near-duplicate-b", new("Harbor View Physical Therapy Clinic",
            [new EvidenceFact("Same clinic found through a different source, established and highly rated.", null, null),
             new EvidenceFact("Website hasn't been updated in years.", null, null)],
            null, "Local", "Healthcare"))
    ];
}
