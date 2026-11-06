using System;
using System.Threading.Tasks;
using LexBrowser.Core.AdBlock;
using Xunit;

namespace LexBrowser.Tests;

public class ShieldLoggerTests
{
    [Fact]
    public void ShieldLogger_Appends_And_Formats_Entries()
    {
        ShieldLogger.Clear();

        ShieldLogger.Log("REQUEST_BLOCKED_403", "example.com", "https://ad.example.com/banner.js", "Script", "Blocked domain");
        ShieldLogger.Log("REQUEST_STUB_200", "streaming.com", "https://imasdk.googleapis.com/js/sdkloader/ima3.js", "Script", "IMA SDK stub");

        Assert.Equal(2, ShieldLogger.Count);

        string dump = ShieldLogger.DumpLog();
        Assert.Contains("Total entries: 2", dump);
        Assert.Contains("REQUEST_BLOCKED_403", dump);
        Assert.Contains("https://ad.example.com/banner.js", dump);
        Assert.Contains("REQUEST_STUB_200", dump);
        Assert.Contains("IMA SDK stub", dump);
    }

    [Fact]
    public void ShieldLogger_Enforces_Ring_Buffer_Limit_Of_2000()
    {
        ShieldLogger.Clear();

        // Encolar 2100 entradas
        for (int i = 0; i < 2100; i++)
        {
            ShieldLogger.Log("TEST_EVENT", "domain.com", $"https://domain.com/item_{i}", "MainFrame", $"Reason {i}");
        }

        Assert.Equal(2000, ShieldLogger.Count);

        string dump = ShieldLogger.DumpLog();
        // Las primeras 100 deben haber sido descartadas (FIFO)
        Assert.DoesNotContain("https://domain.com/item_0\r", dump);
        Assert.DoesNotContain("https://domain.com/item_0\n", dump);
        Assert.DoesNotContain("https://domain.com/item_99\r", dump);
        // Las últimas deben estar presentes
        Assert.Contains("https://domain.com/item_2099", dump);
    }

    [Fact]
    public void ShieldLogger_Thread_Safe_Concurrent_Writes()
    {
        ShieldLogger.Clear();

        Parallel.For(0, 500, i =>
        {
            ShieldLogger.Log("PARALLEL_EVENT", "test.org", $"https://test.org/{i}", "Subframe", "Parallel log");
        });

        Assert.Equal(500, ShieldLogger.Count);
    }
}
