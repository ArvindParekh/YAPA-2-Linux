using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YAPA.Avalonia.Journal;

namespace YAPA.Avalonia.Tests.Journal;

[TestClass]
public class DayPageTests
{
    [TestMethod]
    [DataRow("interview-prep", "Interview prep")]
    [DataRow("langpair", "Langpair")]
    [DataRow("career_ops", "Career ops")]
    [DataRow("YAPA-2", "YAPA-2")]
    [DataRow("NEU Cloud", "NEU Cloud")]
    [DataRow("Interview prep", "Interview prep")]
    public void Repository_slugs_read_as_words_and_chosen_names_stay(string project, string shown)
        => Assert.AreEqual(shown, ProjectColors.Display(project));

    [TestMethod]
    [DataRow(0, "Late night")]
    [DataRow(4, "Late night")]
    [DataRow(5, "Morning")]
    [DataRow(11, "Morning")]
    [DataRow(12, "Afternoon")]
    [DataRow(16, "Afternoon")]
    [DataRow(17, "Evening")]
    [DataRow(20, "Evening")]
    [DataRow(21, "Night")]
    [DataRow(23, "Night")]
    public void Each_hour_falls_in_one_part_of_the_day(int hour, string part)
        => Assert.AreEqual(part, DayPart.For(new DateTime(2026, 11, 23, hour, 30, 0)));
}
