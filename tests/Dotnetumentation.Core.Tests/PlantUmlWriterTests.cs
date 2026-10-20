using Dotnetumentation.Core.PlantUml;

namespace Dotnetumentation.Core.Tests;

public class PlantUmlWriterTests
{
    [Theory]
    [InlineData("Foo.Bar+Baz`1", "Foo_Bar_Baz_1")]
    [InlineData("1st", "_1st")]
    [InlineData("", "_")]
    public void Id_keeps_letters_digits_and_underscores(string name, string expected) =>
        Assert.Equal(expected, PlantUmlWriter.Id(name));

    [Theory]
    [InlineData("Place(Order, int)", "Place_Order_int")]
    [InlineData("Get(List<string>)", "Get_List_string")]
    [InlineData("A/B\\C:D*E", "ABCDE")]
    public void FileName_is_safe_on_every_platform(string name, string expected) =>
        Assert.Equal(expected, PlantUmlWriter.FileName(name));

    [Fact]
    public void Long_file_names_are_shortened_with_a_stable_hash()
    {
        var name = new string('a', 200);
        var fileName = PlantUmlWriter.FileName(name);
        Assert.True(fileName.Length <= 120);
        Assert.Equal(fileName, PlantUmlWriter.FileName(name));
        Assert.NotEqual(fileName, PlantUmlWriter.FileName(name + "b"));
    }

    [Fact]
    public void Text_doubles_backslashes_and_joins_lines()
    {
        Assert.Equal(@"Write(""a\\nb"") x", PlantUmlWriter.Text("Write(\"a\\nb\")\n   x"));
    }

    [Fact]
    public void Block_comment_markers_in_code_are_broken_up()
    {
        var text = new PlantUmlWriter().Line(":x.Contains(\"/'\");").ToString();
        // Ordinal: a culture aware comparison ignores the zero width space.
        Assert.False(text.Contains("/'", StringComparison.Ordinal));
        Assert.Contains("/​'", text);
    }

    [Fact]
    public void Kind_hint_is_written_after_startuml()
    {
        var text = new PlantUmlWriter().StartUml("T", "sequence").EndUml().ToString();
        Assert.Equal("@startuml\n' rice:kind=sequence\ntitle T\n@enduml\n", text);
    }
}
