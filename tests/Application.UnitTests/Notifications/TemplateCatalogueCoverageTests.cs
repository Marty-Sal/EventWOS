using System.Reflection;
using EventOpsOracle.Application.Notifications.Contracts;
using EventOpsOracle.Persistence.Seed;
using FluentAssertions;
using Xunit;

namespace EventOpsOracle.Application.UnitTests.Notifications;

/// <summary>
/// Every template code must have a seeded template.
///
/// This exists because of how quietly the alternative fails. Declaring a code in
/// NotificationTemplateCodes and wiring a call site to it feels finished, but if the
/// seeder catalogue has no entry, there is no active InApp template at run time --
/// so the notification is dropped with a log line nobody is watching. Nothing
/// throws, no test goes red, and the feature is simply silent in production. That is
/// the exact failure mode this whole platform was built to end, so it should not be
/// reachable by forgetting one dictionary entry.
/// </summary>
public class TemplateCatalogueCoverageTests
{
    private static IEnumerable<string> DeclaredCodes() =>
        typeof(NotificationTemplateCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    private static IReadOnlyCollection<string> CataloguedCodes()
    {
        var field = typeof(NotificationTemplateSeeder)
            .GetField("Catalogue", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "NotificationTemplateSeeder.Catalogue was renamed. Update this test rather than deleting it: "
                + "it is the only thing stopping a declared code from shipping with no template.");

        var dictionary = (System.Collections.IDictionary)field.GetValue(null)!;
        return dictionary.Keys.Cast<string>().ToList();
    }

    [Fact]
    public void Every_declared_template_code_is_seeded()
    {
        var missing = DeclaredCodes()
            .Except(CataloguedCodes(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        missing.Should().BeEmpty(
            "a code with no catalogue entry has no active template, so every notification using it is "
            + "silently dropped at run time instead of failing loudly. Add it to "
            + "NotificationTemplateSeeder.Catalogue.");
    }

    [Fact]
    public void The_catalogue_does_not_seed_codes_nobody_declares()
    {
        // The reverse direction catches a renamed or deleted code leaving a stale
        // template behind -- harmless in itself, but it makes the catalogue lie about
        // what the system can actually send.
        var orphans = CataloguedCodes()
            .Except(DeclaredCodes(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();

        orphans.Should().BeEmpty("these catalogue entries match no NotificationTemplateCodes constant.");
    }

    [Fact]
    public void Registration_pending_approval_is_seeded_and_names_both_the_applicant_and_the_link()
    {
        // Pinned explicitly because this code is the one whose ABSENCE was the bug:
        // approvers were never told a registration was waiting, so signups sat in the
        // queue for days. A template that renders without the applicant's name or the
        // review link would leave the same person guessing.
        CataloguedCodes().Should().Contain(NotificationTemplateCodes.RegistrationPendingApproval);

        var field = typeof(NotificationTemplateSeeder)
            .GetField("Catalogue", BindingFlags.NonPublic | BindingFlags.Static)!;
        var dictionary = (System.Collections.IDictionary)field.GetValue(null)!;
        var entry = dictionary[NotificationTemplateCodes.RegistrationPendingApproval]!;

        var body = entry.GetType().GetProperty("Line")!.GetValue(entry) as string;
        body.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("{{ActorName}}");
        body.Should().Contain("{{Link}}");
    }
}
