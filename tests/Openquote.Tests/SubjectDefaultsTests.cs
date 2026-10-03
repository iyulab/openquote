using System.Text.Json.Nodes;
using Openquote.Fields;
using Openquote.Records;
using Openquote.Vault;
using static Openquote.Tests.TestChanges;

namespace Openquote.Tests;

public class SubjectDefaultsTests
{
    // A school's session fields: the grade counts up each school year (from March) up to the subject's school level's
    // last grade, the class does not carry over, the school stays.
    private const string Session = """
        { "format": "openquote.fields/1", "pack": "school", "type": "session", "version": 1, "fields": [
          { "name": "grade", "kind": "text", "default": { "subject": "grade",
              "year": { "startMonth": 3, "then": "advance", "max": { "subject": "level", "by": { "elementary": 6, "middle": 3, "high": 3 } } } } },
          { "name": "class", "kind": "text", "default": { "subject": "class", "year": { "startMonth": 3, "then": "drop" } } },
          { "name": "school", "kind": "text", "default": { "subject": "school" } } ] }
        """;

    private static IReadOnlyList<FieldDefinition> Fields(string json = Session) =>
        Assert.Single(VaultReader.Read([File("fields/school/session/v1.json", json)]).Fields).Fields;

    private static FieldDefinition Field(string name) => Fields().Single(f => f.Name == name);

    /// <summary>A subject written as each (day, fields) says, one change after another.</summary>
    private static Entity Subject(params (string At, JsonObject Fields)[] writes)
    {
        var files = writes.Select((w, i) =>
        {
            var change = Json(i + 1, "s1", i == 0 ? "create" : "update", i == 0 ? null : [i], w.Fields, entityType: "subject");
            change["at"] = $"{w.At}T10:00:00+09:00";
            return File(change);
        }).ToList();
        return Assert.Single(EntityMerger.Merge(VaultReader.Read(files).Changes)).Value;
    }

    private static JsonObject Level(string code) => new() { ["scheme"] = "level", ["version"] = 1, ["code"] = code };

    private static SubjectCarry Carry(string field, Entity subject, string date) =>
        SubjectDefaults.Carry(Field(field), subject, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void Reads_how_a_value_from_the_subject_ages()
    {
        var fields = Fields();

        var grade = fields.Single(f => f.Name == "grade").DefaultYear!;
        Assert.Equal((3, YearStep.Advance), (grade.StartMonth, grade.Then));
        Assert.Equal("level", grade.Max!.SubjectField);
        Assert.Equal(6, grade.Max.ByCode["elementary"]);
        Assert.Equal((3, YearStep.Drop, (YearCap?)null), (fields.Single(f => f.Name == "class").DefaultYear!.StartMonth, fields.Single(f => f.Name == "class").DefaultYear!.Then, fields.Single(f => f.Name == "class").DefaultYear!.Max));
        Assert.Null(fields.Single(f => f.Name == "school").DefaultYear);
    }

    [Theory]
    [InlineData("openquote.fields/0", """{ "subject": "g", "year": { "then": "advance" } }""")]                       // format 1 only
    [InlineData("openquote.fields/1", """{ "value": "1", "year": { "then": "advance" } }""")]                        // only beside a subject source
    [InlineData("openquote.fields/1", """{ "subject": "g", "year": { "then": "grow" } }""")]
    [InlineData("openquote.fields/1", """{ "subject": "g", "year": { "startMonth": 3 } }""")]                        // says what happens
    [InlineData("openquote.fields/1", """{ "subject": "g", "year": { "startMonth": 13, "then": "advance" } }""")]
    [InlineData("openquote.fields/1", """{ "subject": "g", "year": { "then": "drop", "max": 6 } }""")]               // a cap only for an advancing value
    [InlineData("openquote.fields/1", """{ "subject": "g", "year": { "then": "advance", "max": 0 } }""")]
    [InlineData("openquote.fields/1", """{ "subject": "g", "year": { "then": "advance", "max": { "subject": "l", "by": {} } } }""")]
    [InlineData("openquote.fields/1", """{ "subject": "g", "year": { "then": "advance", "max": { "subject": "l", "by": { "a": "6" } } } }""")]
    [InlineData("openquote.fields/1", """{ "subject": "g", "year": "advance" }""")]
    public void A_year_rule_that_does_not_say_one_thing_is_reported(string format, string @default)
    {
        var json = $$"""{ "format": "{{format}}", "pack": "school", "type": "session", "version": 1, "fields": [ { "name": "g", "kind": "text", "default": {{@default}} } ] }""";
        Assert.Equal(UnreadableReason.Invalid, Assert.Single(VaultReader.Read([File("fields/school/session/v1.json", json)]).Unreadable).Reason);
    }

    [Fact]
    public void Within_the_year_the_value_was_written_it_carries_as_it_is()
    {
        var subject = Subject(("2026-05-10", new JsonObject { ["grade"] = "1", ["class"] = "3", ["school"] = "A" }));

        foreach (var field in new[] { "grade", "class" })
        {
            var carry = Carry(field, subject, "2027-02-28");
            Assert.False(carry.Stale);
            Assert.Equal(carry.Value, carry.Offer);
            Assert.Equal(new DateOnly(2026, 5, 10), carry.WrittenOn);
        }
    }

    [Fact]
    public void Once_the_year_is_over_a_grade_counts_up_and_a_class_drops()
    {
        var subject = Subject(("2026-05-10", new JsonObject { ["grade"] = "1", ["class"] = "3", ["school"] = "A" }));

        Assert.Equal((true, 1, "2"), Facts(Carry("grade", subject, "2027-03-02")));
        Assert.Equal((true, 2, "3"), Facts(Carry("grade", subject, "2028-04-01")));
        Assert.Equal((true, 1, (string?)null), Facts(Carry("class", subject, "2027-03-02")));
        // A field without a rule holds whatever the year.
        Assert.Equal((false, 0, "A"), Facts(Carry("school", subject, "2030-01-01")));
    }

    [Fact]
    public void A_grade_is_not_counted_past_the_last_grade_of_the_subjects_level()
    {
        var middle = Subject(("2026-05-10", new JsonObject { ["grade"] = "3", ["level"] = Level("middle") }));
        Assert.Equal((true, 1, (string?)null), Facts(Carry("grade", middle, "2027-03-02")));

        var elementary = Subject(("2026-05-10", new JsonObject { ["grade"] = "3", ["level"] = Level("elementary") }));
        Assert.Equal("4", Carry("grade", elementary, "2027-03-02").Offer);
        Assert.Null(Carry("grade", elementary, "2030-03-02").Offer);   // 3 + 4 passes 6
    }

    [Theory]
    [InlineData("2", 1, "3")]    // 3 is a grade at every level
    [InlineData("3", 1, null)]  // the last grade of a middle or high school: it may have finished
    [InlineData("4", 1, "5")]   // only an elementary school has a 4th grade
    [InlineData("2", 2, null)]  // 4 only at an elementary school, and 2 could be at any
    [InlineData("6", 1, null)]  // every level ends by 6
    public void Without_a_level_a_grade_counts_up_only_where_every_level_it_could_be_at_agrees(string grade, int years, string? offer)
    {
        var subject = Subject(("2026-05-10", new JsonObject { ["grade"] = grade }));
        Assert.Equal(offer, Carry("grade", subject, $"{2026 + years}-03-02").Offer);
    }

    [Fact]
    public void A_value_that_is_not_a_whole_number_is_not_counted_up()
    {
        var subject = Subject(("2026-05-10", new JsonObject { ["grade"] = "중1" }));
        Assert.Equal((true, 1, (string?)null), Facts(Carry("grade", subject, "2027-03-02")));
    }

    [Fact]
    public void The_year_is_reckoned_from_the_change_that_wrote_the_current_value()
    {
        var subject = Subject(
            ("2026-05-10", new JsonObject { ["grade"] = "1", ["class"] = "3" }),
            ("2027-03-05", new JsonObject { ["grade"] = "2" }));

        Assert.Equal((false, 0, "2"), Facts(Carry("grade", subject, "2027-04-01")));
        // The class was not written again: it is still last year's.
        Assert.Equal((true, 1, (string?)null), Facts(Carry("class", subject, "2027-04-01")));
    }

    [Fact]
    public void A_record_dated_before_the_value_was_written_takes_it_as_it_is()
    {
        var subject = Subject(("2027-03-05", new JsonObject { ["grade"] = "2" }));
        Assert.Equal((false, 0, "2"), Facts(Carry("grade", subject, "2026-11-20")));
    }

    [Fact]
    public void A_subject_without_the_value_offers_nothing()
    {
        var subject = Subject(("2026-05-10", new JsonObject { ["school"] = "A" }));
        Assert.Equal(new SubjectCarry("grade", null, null, 0, null), Carry("grade", subject, "2027-03-02"));
    }

    [Fact]
    public void A_field_that_takes_nothing_from_the_subject_is_refused()
    {
        var json = """{ "format": "openquote.fields/1", "pack": "school", "type": "session", "version": 1, "fields": [ { "name": "minutes", "kind": "number" } ] }""";
        var subject = Subject(("2026-05-10", new JsonObject { ["grade"] = "1" }));
        Assert.Throws<ArgumentException>(() => SubjectDefaults.Carry(Fields(json).Single(), subject, new DateOnly(2027, 3, 2)));
    }

    private static (bool Stale, int Years, string? Offer) Facts(SubjectCarry c) => (c.Stale, c.YearsPassed, c.Offer);
}
