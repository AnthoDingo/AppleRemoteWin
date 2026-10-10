// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 AnthoDingo

using System.Text.Json;
using Xunit;

namespace AppleRemote.Tests;

public class ReplayTests
{
    [Fact]
    public void Line_has_the_capture_format()
    {
        string line = Replay.Line(1.23456, new byte[] { 0xFC, 0x00, 0x20 });
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("report", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(1.2346, doc.RootElement.GetProperty("t").GetDouble(), 6);
        Assert.Equal("fc0020", doc.RootElement.GetProperty("hex").GetString());
    }

    [Fact]
    public void Read_roundtrips_a_capture_and_adds_idle_ticks()
    {
        string path = Path.Combine(Path.GetTempPath(), $"appleremote-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(path, new[]
            {
                """{"kind":"mark","t":0.0,"step":"0: ignored"}""",
                Replay.Line(0.0, new byte[] { 0xFC, 0x00, 0x20 }),
                Replay.Line(0.05, new byte[] { 0xFC, 0x00, 0x00 }),
            });

            var items = Replay.Read(path).ToList();

            var reports = items.Where(i => i.Report is not null).ToList();
            Assert.Equal(2, reports.Count);
            Assert.Equal(new byte[] { 0xFC, 0x00, 0x20 }, reports[0].Report);
            Assert.Equal(0.05, reports[1].Time, 6);

            // between the two reports (50 ms) and at the end (1 s), null ticks every 10 ms
            Assert.True(items.Count(i => i.Report is null) >= 3 + 100);
            Assert.True(items.Zip(items.Skip(1)).All(p => p.Second.Time >= p.First.Time));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
