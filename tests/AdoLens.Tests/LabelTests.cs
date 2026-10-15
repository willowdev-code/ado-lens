namespace AdoLens.Tests;

public class LabelTests
{
    [Theory]
    [InlineData("Item %1: received %2, on hand %3. Quantity %3 was consumed.", "Item 510234 Sparkling water: received 12, on hand 5. Quantity 5 was consumed.")]
    [InlineData("Order %1 for %2 to %3 on %4, %5, %6, %7, %8, %9, %10 and %11", "Order SO-1 for Alpha to Main store on 2026-10-15, a, b, c, d, e, f and g")]
    [InlineData("No on-hand for item %1", "no ON-HAND for item X")]
    [InlineData("The journal cannot be posted", "cannot be posted")]
    public void A_pasted_message_with_its_values_matches_the_label(string label, string seen) =>
        Assert.True(Labels.Matches(label, seen));

    [Theory]
    [InlineData("Item %1: received %2", "Order 5: received 3")]
    [InlineData("The journal cannot be posted", "The journal was posted")]
    public void A_different_message_does_not_match(string label, string seen) =>
        Assert.False(Labels.Matches(label, seen));

    [Fact]
    public void Numbers_and_short_words_are_left_out_of_the_search()
    {
        Assert.Equal("Rozchodowano przyjęto fizyczny Towar stan", Labels.SearchWords("Towar 510234: przyjęto 12, stan fizyczny 5. Rozchodowano 5."));
    }

    [Fact]
    public void Label_files_are_parsed_without_comments()
    {
        var map = Labels.Parse("﻿First=One %1\n ;comment for First\nSecond=Two=2\n\nBad line\n");
        Assert.Equal("One %1", map["first"]);
        Assert.Equal("Two=2", map["Second"]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Places_name_the_methods_and_the_fields_that_use_a_label()
    {
        const string xml = """
            <AxTable xmlns:i="http://www.w3.org/2001/XMLSchema-instance">
              <Name>DemoTable</Name>
              <SourceCode><Methods>
                <Method><Name>validateWrite</Name><Source><![CDATA[ret = checkFailed("@Demo:Missing");]]></Source></Method>
                <Method><Name>other</Name><Source><![CDATA[info("x");]]></Source></Method>
              </Methods></SourceCode>
              <Fields><AxTableField><Name>Amount</Name><Label>@Demo:Missing</Label></AxTableField></Fields>
            </AxTable>
            """;
        Assert.Equal(": validateWrite; Label of Amount", Labels.Places(xml, "@Demo:Missing"));
        Assert.Equal("", Labels.Places(xml, "@Demo:Other"));
    }

    [Fact]
    public void Review_sees_new_lines_per_method()
    {
        const string before = """
            <AxClass><Name>Demo</Name><SourceCode><Methods>
              <Method><Name>run</Name><Source><![CDATA[void run()
            {
                info("@Demo:Done");
            }]]></Source></Method>
            </Methods></SourceCode></AxClass>
            """;
        const string after = """
            <AxClass><Name>Demo</Name><SourceCode><Methods>
              <Method><Name>run</Name><Source><![CDATA[void run()
            {
                info("@Demo:Done");
                warning("Nothing to post"); // TODO label
            }]]></Source></Method>
              <Method><Name>check</Name><Source><![CDATA[boolean check() { return checkFailed("@Demo:Missing"); }]]></Source></Method>
            </Methods></SourceCode></AxClass>
            """;
        var added = Review.AddedLines(before, after);
        Assert.Contains(added, x => x.Method == "run" && x.Line.Contains("Nothing to post"));
        Assert.Contains(added, x => x.Method == "check");
        Assert.DoesNotContain(added, x => x.Line.Contains("@Demo:Done"));
    }
}
